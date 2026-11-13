using System.Text.Json;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldSense.Api.Services;

public class BillingService(AppDbContext db, MembershipService membership, IStripeGateway stripe,
    IOptions<BillingSettings> options, IWebHostEnvironment environment)
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

    public async Task<object> Catalog(Guid userId)
    {
        await membership.Current(userId);
        return new {
            plans = await db.Plans.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.DurationDays).ToListAsync(),
            stripeAvailable = settings.Configured, stripeTestMode = !settings.IsLive,
            tomanAvailable = false, tomanProvider = "ezpay", legalName = settings.LegalName,
            tradingName = settings.TradingName,
            queuedPeriods = await db.PaymentOrders.CountAsync(p => p.UserId == userId && p.Status == "paid_queued")
        };
    }

    public async Task<PaymentOrder> Checkout(Guid userId, Guid planId, string currency, string requestKey)
    {
        if (currency != "USD") throw new BillingException("payment_method_unavailable", "پرداخت تومانی پس از اتصال کیف پول فعال می‌شود.", 503);
        Ready();
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
                if (plan?.PriceUsdCents is not (>= 50 and <= 99999999))
                    throw new BillingException("plan_not_for_sale", "قیمت دلاری این طرح هنوز مشخص نشده است.");
                var origin = settings.PortalOrigin.TrimEnd('/');
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.UserInfo != "" ||
                    uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" ||
                    (uri.Scheme != "https" && !(environment.IsDevelopment() && uri.IsLoopback)))
                    throw new BillingException("billing_configuration", "پرداخت در دسترس نیست.", 503);
                order = new PaymentOrder { UserId = userId, PlanId = plan.Id, PlanName = plan.Name,
                    DurationDays = plan.DurationDays, MessageLimit = plan.MessageLimit, EmailForwardLimit = plan.EmailForwardLimit,
                    SendEnabled = plan.SendEnabled, ReceiveEnabled = plan.ReceiveEnabled,
                    AmountMinor = plan.PriceUsdCents.Value, RequestKey = requestKey, ReturnOrigin = origin };
                db.PaymentOrders.Add(order);
                await db.SaveChangesAsync();
            }
            await transaction.CommitAsync();
        }
        if (order.Status is not ("created" or "checkout_open")) return order;
        if (order.ProviderSessionId != null) return order;
        if (order.CreatedAt < DateTime.UtcNow.AddMinutes(-50))
            throw new BillingException("checkout_expired", "زمان این درخواست گذشته است. پرداخت تازه‌ای آغاز کنید.");
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
        if ((order.Status is "created" or "checkout_open" or "failed" or "expired") && order.ProviderSessionId != null)
        {
            Ready();
            await ApplySession(await stripe.Checkout(order.ProviderSessionId));
        }
        await membership.Current(userId);
        return await db.PaymentOrders.AsNoTracking().SingleAsync(p => p.Id == id);
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
