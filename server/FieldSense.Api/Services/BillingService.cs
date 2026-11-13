using System.Text.Json;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldSense.Api.Services;

public class BillingService(AppDbContext db, MembershipService membership, IStripeGateway stripe,
    EzpayBroker ezpay, PaymentWalletService wallet, IOptions<BillingSettings> options, IWebHostEnvironment environment)
{
    private readonly BillingSettings settings = options.Value;
    private static string Text(JsonElement o, string key) => o.ValueKind == JsonValueKind.Object &&
        o.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    private static long Number(JsonElement o, string key) => o.ValueKind == JsonValueKind.Object &&
        o.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) ? n : -1;
    private static bool Flag(JsonElement o, string key) => o.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
    private static JsonElement Object(JsonElement o, string key) => o.ValueKind == JsonValueKind.Object &&
        o.TryGetProperty(key, out var p) ? p : default;
    private static string Reference(JsonElement o, string key) { var p = Object(o, key); return p.ValueKind == JsonValueKind.String ? p.GetString()! : Text(p, "id"); }
    private static void Check(bool condition)
    {
        if (!condition) throw new BillingException("payment_verification_failed", "تأیید پرداخت انجام نشد.");
    }
    private void Ready()
    {
        if (!settings.Configured) throw new BillingException("payment_not_configured", "پرداخت دلاری هنوز فعال نشده است.", 503);
    }
    private bool EzpayConfigured => settings.EzpayConfiguredFor(environment.IsDevelopment());
    private void EzpayReady()
    {
        if (!EzpayConfigured) throw new BillingException("payment_method_unavailable", "پرداخت تومانی پس از اتصال کیف پول فعال می‌شود.", 503);
    }
    // Ezpay's card-payment floor is 10,000 rial; the relay caps one link at 4,000,000,000 rial.
    public const long MinToman = 1_000, MaxToman = 400_000_000;
    public static string FactorFor(PaymentOrder order) => "darvazeh-" + order.Id.ToString("N");

    public async Task<object> Catalog(Guid userId)
    {
        await membership.Current(userId);
        return new {
            plans = await db.Plans.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.DurationDays).ToListAsync(),
            stripeAvailable = settings.Configured, stripeTestMode = !settings.IsLive,
            tomanAvailable = EzpayConfigured && await wallet.Offered(), tomanProvider = "ezpay", legalName = settings.LegalName,
            tradingName = settings.TradingName,
            queuedPeriods = await db.PaymentOrders.CountAsync(p => p.UserId == userId && p.Status == "paid_queued")
        };
    }

    public async Task<PaymentOrder> Checkout(Guid userId, Guid planId, string currency, string requestKey)
    {
        if (currency == "IRT")
        {
            EzpayReady();
            // No order is recorded while no receiving wallet is offered; Ezpay itself is checked again when the link is created.
            if (!await wallet.Offered()) throw new BillingException("payment_method_unavailable", "پرداخت تومانی پس از اتصال کیف پول فعال می‌شود.", 503);
        }
        else if (currency == "USD") Ready();
        else throw new BillingException("payment_method_unavailable", "این روش پرداخت پشتیبانی نمی‌شود.", 400);
        if (!Guid.TryParse(requestKey, out _)) throw new BillingException("invalid_request", "درخواست پرداخت معتبر نیست.");
        PaymentOrder order;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var existing = await db.PaymentOrders.SingleOrDefaultAsync(p => p.UserId == userId && p.RequestKey == requestKey);
            if (existing != null)
            {
                Check(existing.PlanId == planId && existing.Currency == currency);
                order = existing;
            }
            else
            {
                var cutoff = DateTime.UtcNow.AddHours(-1);
                if (await db.PaymentOrders.CountAsync(p => p.UserId == userId && p.CreatedAt > cutoff &&
                    (p.Status == "created" || p.Status == "checkout_open")) >= 10)
                    throw new BillingException("checkout_rate_limit", "درخواست‌های پرداخت زیادی باز است. کمی بعد تلاش کنید.", 429);
                var plan = await db.Plans.SingleOrDefaultAsync(p => p.Id == planId && p.IsActive && p.IsPurchasable && p.Code != "trial");
                if (plan == null) throw new BillingException("plan_not_for_sale", "این طرح برای فروش نیست.");
                if (currency == "USD" && plan.PriceUsdCents is not (>= 50 and <= 99999999))
                    throw new BillingException("plan_not_for_sale", "قیمت دلاری این طرح هنوز مشخص نشده است.");
                if (currency == "IRT" && plan.PriceToman is not (>= MinToman and <= MaxToman))
                    throw new BillingException("plan_not_for_sale", "قیمت تومانی این طرح هنوز مشخص نشده است.");
                var origin = settings.PortalOrigin.TrimEnd('/');
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.UserInfo != "" ||
                    uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" ||
                    (uri.Scheme != "https" && !(environment.IsDevelopment() && uri.IsLoopback)))
                    throw new BillingException("billing_configuration", "پرداخت در دسترس نیست.", 503);
                order = new PaymentOrder { UserId = userId, PlanId = plan.Id, PlanName = plan.Name,
                    DurationDays = plan.DurationDays, MessageLimit = plan.MessageLimit, EmailForwardLimit = plan.EmailForwardLimit,
                    SendEnabled = plan.SendEnabled, ReceiveEnabled = plan.ReceiveEnabled,
                    Provider = currency == "IRT" ? "ezpay" : "stripe", Currency = currency,
                    AmountMinor = currency == "IRT" ? plan.PriceToman!.Value : plan.PriceUsdCents!.Value, RequestKey = requestKey, ReturnOrigin = origin };
                db.PaymentOrders.Add(order);
                await db.SaveChangesAsync();
            }
            await transaction.CommitAsync();
        }
        if (order.Status is not ("created" or "checkout_open")) return order;
        if (order.ProviderSessionId != null) return order;
        if (order.CreatedAt < DateTime.UtcNow.AddMinutes(-50))
            throw new BillingException("checkout_expired", "زمان این درخواست گذشته است. پرداخت تازه‌ای آغاز کنید.");
        if (order.Provider == "ezpay") return await CreateEzpayLink(order);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        var session = await stripe.CreateCheckout(order, user.Email);
        var id = Text(session, "id"); var url = Text(session, "url");
        Check(id.StartsWith("cs_", StringComparison.Ordinal) && Flag(session, "livemode") == settings.IsLive &&
            Uri.TryCreate(url, UriKind.Absolute, out var destination) && destination.Scheme == "https" && destination.Host == "checkout.stripe.com");
        await using var updateTransaction = await db.Database.BeginTransactionAsync();
        // A webhook may beat the session-create response; never downgrade paid state.
        await db.Entry(order).ReloadAsync();
        Check(order.ProviderSessionId == null || order.ProviderSessionId == id);
        order.ProviderSessionId = id; order.CheckoutUrl = url;
        if (order.Status == "created") order.Status = "checkout_open";
        if (Number(session, "expires_at") > 0) order.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(Number(session, "expires_at")).UtcDateTime;
        await db.SaveChangesAsync(); await updateTransaction.CommitAsync();
        return order;
    }

    public async Task<PaymentOrder?> Reconcile(Guid userId, Guid id)
    {
        var order = await db.PaymentOrders.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (order == null) return null;
        if (order.Provider == "ezpay" && (order.Status is "checkout_open" or "failed" or "expired") && order.ProviderUniqueId != null)
        {
            EzpayReady();
            await ApplyEzpay(order.Id);
        }
        else if (order.Provider == "stripe" && (order.Status is "created" or "checkout_open" or "failed" or "expired") && order.ProviderSessionId != null)
        {
            Ready();
            await ApplySession(await stripe.Checkout(order.ProviderSessionId));
        }
        await membership.Current(userId);
        return await db.PaymentOrders.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    /// <summary>Creates the Ezpay link into the default wallet (as Ravand does for plan payments). Stored before the provider call, so a retry is safe.</summary>
    private async Task<PaymentOrder> CreateEzpayLink(PaymentOrder order)
    {
        var phone = await wallet.PurchaseReceiver();
        var factor = FactorFor(order);
        var amount = PaymentMoney.TomanToRial(order.AmountMinor);
        var body = await ezpay.Call(HttpMethod.Post, "payments", new { phoneNumber = phone, factorNumber = factor, amount,
            subject = "اشتراک دروازه", description = $"Darvazeh {order.PlanName} · {order.DurationDays} days" });
        var uniqueId = EzpayBroker.Text(body, "uniqueId");
        var link = EzpayBroker.Text(body, "paymentLink");
        Check(EzpayBroker.IsProviderLink(link) && uniqueId is { Length: > 0 and <= 100 });
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Entry(order).ReloadAsync();
        Check(order.ProviderSessionId == null || order.ProviderSessionId == factor);
        order.ProviderSessionId = factor; order.ProviderUniqueId = uniqueId; order.ReceivingPhone = phone; order.CheckoutUrl = link;
        if (order.Status == "created") order.Status = "checkout_open";
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return order;
    }

    /// <summary>Asks the relay about an Ezpay order and applies the answer. Ezpay links stay payable, so a late payment still activates it.</summary>
    private async Task ApplyEzpay(Guid orderId)
    {
        var snapshot = await db.PaymentOrders.AsNoTracking().SingleAsync(p => p.Id == orderId && p.Provider == "ezpay");
        var amount = PaymentMoney.TomanToRial(snapshot.AmountMinor);
        var path = $"payments/{Uri.EscapeDataString(snapshot.ProviderUniqueId!)}?phoneNumber={Uri.EscapeDataString(snapshot.ReceivingPhone ?? "")}" +
            $"&factorNumber={Uri.EscapeDataString(snapshot.ProviderSessionId!)}&amount={amount}";
        var body = await ezpay.Call(HttpMethod.Get, path);
        EzpayBroker.ValidatePayment(body, snapshot.ProviderUniqueId!, amount);
        var providerStatus = EzpayBroker.Text(body, "status")?.ToUpperInvariant();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var order = await db.PaymentOrders.SingleAsync(p => p.Id == orderId);
        if (providerStatus == "SUCCESS" && order.PaidAt == null && order.Status != "refunded")
        {
            var reference = EzpayBroker.Text(body, "transactionid")!;
            Check(reference.Length <= 120);
            order.ProviderPaymentId = "ezpay:" + reference;
            order.PaidAt = DateTime.UtcNow; order.Status = "paid_queued";
            membership.Log(order.UserId, "payment_confirmed", order.PlanName);
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            await membership.Current(order.UserId);
            return;
        }
        if (providerStatus is "FAILED" or "DENIED" or "REVERSED" or "REFUNDED" && order.PaidAt == null) order.Status = "failed";
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    /// <summary>Checks open Ezpay orders from the last three days, so a paid order is activated even if the buyer never returns.</summary>
    public async Task<int> ReconcileEzpayOrders(CancellationToken ct)
    {
        if (!EzpayConfigured) return 0;
        var since = DateTime.UtcNow.AddDays(-3);
        var open = await db.PaymentOrders.AsNoTracking()
            .Where(p => p.Provider == "ezpay" && p.Status == "checkout_open" && p.ProviderUniqueId != null && p.CreatedAt > since)
            .OrderBy(p => p.CreatedAt).Select(p => new { p.Id, p.UserId }).Take(100).ToListAsync(ct);
        var paid = 0;
        foreach (var item in open)
        {
            ct.ThrowIfCancellationRequested();
            try { if ((await Reconcile(item.UserId, item.Id))?.PaidAt != null) paid++; }
            catch (BillingException) { /* Relay or Ezpay unavailable: try again next round. */ }
            db.ChangeTracker.Clear();
        }
        return paid;
    }

    public async Task Webhook(string raw, string signature)
    {
        Ready();
        Stripe.EventUtility.ValidateSignature(raw, signature, settings.StripeWebhookSecret, 300);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement; var eventId = Text(root, "id"); var type = Text(root, "type");
        Check(eventId.Length is > 0 and <= 255 && Flag(root, "livemode") == settings.IsLive);
        if (await db.PaymentEvents.AnyAsync(e => e.Id == eventId)) return;
        var data = Object(Object(root, "data"), "object");
        if (type is "checkout.session.completed" or "checkout.session.async_payment_succeeded" or
            "checkout.session.expired" or "checkout.session.async_payment_failed")
            await ApplySession(await stripe.Checkout(Text(data, "id")), eventId, type);
        else if (type == "charge.refunded")
            await ApplyRefund(await stripe.Charge(Text(data, "id")), eventId, type);
        // Unrelated event types are acknowledged without changing entitlements.
    }

    private async Task ApplySession(JsonElement session, string? eventId = null, string type = "checkout_reconciled")
    {
        var metadata = Object(session, "metadata");
        if (!Guid.TryParse(Text(metadata, "order_id"), out var id)) return; // Other applications on this Stripe account.
        Check(Flag(session, "livemode") == settings.IsLive && Text(session, "mode") == "payment");
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (eventId != null && await db.PaymentEvents.AnyAsync(e => e.Id == eventId)) return;
        var order = await db.PaymentOrders.SingleOrDefaultAsync(p => p.Id == id && p.Provider == "stripe");
        if (order == null) return;
        var sessionId = Text(session, "id");
        Check((order.ProviderSessionId == null || order.ProviderSessionId == sessionId) &&
            Text(metadata, "user_id").Equals(order.UserId.ToString(), StringComparison.OrdinalIgnoreCase) &&
            Text(session, "client_reference_id").Equals(order.UserId.ToString(), StringComparison.OrdinalIgnoreCase) &&
            Text(session, "currency") == "usd" && Number(session, "amount_total") == order.AmountMinor);
        if (Text(session, "payment_status") == "paid" && order.Status != "refunded")
        {
            var lines = Object(Object(session, "line_items"), "data");
            Check(lines.ValueKind == JsonValueKind.Array && lines.GetArrayLength() == 1 &&
                Number(lines[0], "quantity") == 1 && Number(lines[0], "amount_total") == order.AmountMinor);
            var payment = Reference(session, "payment_intent");
            Check(payment.StartsWith("pi_", StringComparison.Ordinal));
            Check(order.ProviderPaymentId == null || order.ProviderPaymentId == payment);
            order.ProviderPaymentId = payment; order.ProviderSessionId = sessionId;
            if (order.PaidAt == null)
            {
                order.PaidAt = DateTime.UtcNow; order.Status = "paid_queued";
                membership.Log(order.UserId, "payment_confirmed", order.PlanName);
            }
            await db.SaveChangesAsync();
            await membership.Current(order.UserId);
        }
        else if (order.PaidAt == null && Text(session, "status") == "expired") order.Status = "expired";
        else if (order.PaidAt == null && type == "checkout.session.async_payment_failed") order.Status = "failed";
        if (eventId != null) db.PaymentEvents.Add(new PaymentEventReceipt { Id = eventId, Type = type });
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    private async Task ApplyRefund(JsonElement charge, string eventId, string type)
    {
        var intentId = Reference(charge, "payment_intent");
        if (intentId == "") return;
        var intent = await stripe.PaymentIntent(intentId);
        if (!Guid.TryParse(Text(Object(intent, "metadata"), "order_id"), out var id)) return;
        Check(Flag(charge, "livemode") == settings.IsLive);
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (await db.PaymentEvents.AnyAsync(e => e.Id == eventId)) return;
        var order = await db.PaymentOrders.SingleOrDefaultAsync(p => p.Id == id && p.Provider == "stripe");
        if (order == null) return;
        Check((order.ProviderPaymentId == null || order.ProviderPaymentId == intentId) &&
            Text(charge, "currency") == "usd" && Number(charge, "amount") == order.AmountMinor &&
            Text(Object(intent, "metadata"), "user_id").Equals(order.UserId.ToString(), StringComparison.OrdinalIgnoreCase));
        var refunded = Number(charge, "amount_refunded");
        Check(refunded >= 0 && refunded <= order.AmountMinor);
        order.ProviderPaymentId = intentId;
        order.RefundedMinor = Math.Max(order.RefundedMinor, refunded);
        if (order.RefundedMinor == order.AmountMinor && order.Status != "refunded")
        {
            order.Status = "refunded";
            var current = await db.Subscriptions.SingleOrDefaultAsync(s => s.PaymentOrderId == order.Id && s.IsCurrent);
            if (current != null) current.ExpiresAt = DateTime.UtcNow;
            membership.Log(order.UserId, "payment_refunded", order.PlanName);
        }
        db.PaymentEvents.Add(new PaymentEventReceipt { Id = eventId, Type = type });
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }
}

/// <summary>Polls the relay for open toman orders every minute (Ezpay does not call back to Darvazeh).</summary>
public class EzpayPaymentReconciler(IServiceScopeFactory scopes, IOptions<BillingSettings> billing, IOptions<AppSettings> app,
    IWebHostEnvironment environment, ILogger<EzpayPaymentReconciler> logger) : BackgroundService
{
    private bool bootstrapped;

    private async Task BootstrapDefaultWallet(IServiceProvider services, CancellationToken ct)
    {
        var phone = billing.Value.DefaultWalletPhone;
        if (bootstrapped || string.IsNullOrWhiteSpace(phone) || !billing.Value.EzpayConfiguredFor(environment.IsDevelopment())) return;
        try { bootstrapped = await services.GetRequiredService<PaymentWalletService>().Bootstrap(phone, app.Value.AdminEmail, ct); }
        catch (BillingException error)
        {
            bootstrapped = error.Code == "invalid_phone";
            logger.LogWarning("Default wallet not connected yet: {Code}", error.Code);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { using var first = scopes.CreateScope(); await BootstrapDefaultWallet(first.ServiceProvider, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception error) { logger.LogWarning(error, "Default wallet bootstrap failed"); }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await BootstrapDefaultWallet(scope.ServiceProvider, stoppingToken);
                var paid = await scope.ServiceProvider.GetRequiredService<BillingService>().ReconcileEzpayOrders(stoppingToken);
                if (paid > 0) logger.LogInformation("Confirmed {Count} toman payments through Ezpay", paid);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "Toman payment reconciliation failed"); }
        }
    }
}
