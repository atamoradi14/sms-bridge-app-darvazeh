using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Services;

public record WalletConnectionView(string Status, string? PhoneNumber, DateTime? ConnectedAt, Guid? ChallengeId, DateTime? ChallengeExpiresAt, string? PendingPhoneNumber);
public record PaymentWalletView(WalletConnectionView Connection, bool MerchantEnabled, bool PurchaseReady, string ReadinessReason, EzpayWalletStatus? ProviderStatus, bool RelayConfigured);

/// <summary>
/// Darvazeh's default receiving wallet, following Ravand's superadmin wallet (RavandsWalletService) step for step:
/// <list type="bullet">
/// <item>Connect by phone: the relay asks Ezpay for broker access by OTP. If Ezpay sends a consent code, the admin confirms it here.</item>
/// <item>If Ezpay answers that access already exists (no consent code), Ezpay has not proved who holds the phone, so the admin must prove
/// it: either the phone was verified before (a <see cref="WalletOwner"/>) or with a one-time SMS code. Ravand sends that code through its
/// SMS service; Darvazeh sends it through the admin's own linked Darvazeh phone.</item>
/// <item>Purchases need the wallet connected, Ezpay access active, identity verified at Ezpay, and merchant services switched on here.
/// Changing the number switches merchant services off again.</item>
/// </list>
/// </summary>
public class PaymentWalletService(AppDbContext db, EzpayBroker broker, MembershipService membership, ILogger<PaymentWalletService> logger)
{
    private static BillingException Fail(string code, string message, int status) => new(code, message, status);

    private async Task<PaymentWallet> Row() => await db.PaymentWallets.SingleOrDefaultAsync(w => w.Id == 1)
        ?? db.PaymentWallets.Add(new PaymentWallet()).Entity;

    private static void ClearChallenge(PaymentWallet w)
    { w.PendingPhoneNumber = null; w.RequestUid = null; w.ChallengeId = null; w.ChallengeUserId = null; w.ChallengeExpiresAt = null; w.FailedAttempts = 0; }

    private static void Connect(PaymentWallet w, Guid actor, string phone)
    {
        if (w.PhoneNumber != phone) w.MerchantEnabled = false;
        w.PhoneNumber = phone; w.ConnectedAt = DateTime.UtcNow; w.ConnectedByUserId = actor;
        ClearChallenge(w);
    }

    private async Task RememberOwner(Guid actor, string phone)
    {
        if (!await db.WalletOwners.AnyAsync(o => o.PhoneNumber == phone))
            db.WalletOwners.Add(new WalletOwner { PhoneNumber = phone, OwnerUserId = actor, VerifiedAt = DateTime.UtcNow });
    }

    private async Task<PaymentWalletView> View(PaymentWallet w, Guid actor, bool relayConfigured)
    {
        var readiness = w.PhoneNumber == null ? new WalletReadiness(false, "ابتدا کیف پول را متصل کنید.", null) : await broker.Readiness(w.PhoneNumber);
        var pending = w.ChallengeId != null && w.ChallengeExpiresAt > DateTime.UtcNow;
        var mine = pending && w.ChallengeUserId == actor;
        var connection = new WalletConnectionView(w.PhoneNumber != null ? "Connected" : pending ? "PendingConsent" : "Disconnected",
            w.PhoneNumber, w.ConnectedAt, mine ? w.ChallengeId : null, mine ? w.ChallengeExpiresAt : null, mine ? w.PendingPhoneNumber : null);
        var ready = readiness.PurchaseReady && w.MerchantEnabled;
        var reason = readiness.PurchaseReady && !w.MerchantEnabled ? "پس از احراز هویت، خدمات پذیرندگی را فعال کنید." : readiness.Reason;
        return new(connection, w.MerchantEnabled, ready, reason, readiness.ProviderStatus, relayConfigured);
    }

    public async Task<PaymentWalletView> View(Guid actor, bool relayConfigured)
    {
        var w = await db.PaymentWallets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1) ?? new PaymentWallet();
        if (!relayConfigured) return new(new("Disconnected", w.PhoneNumber, w.ConnectedAt, null, null, null), w.MerchantEnabled, false,
            "اتصال به سرور پرداخت ایران پیکربندی نشده است.", null, false);
        return await View(w, actor, true);
    }

    /// <summary>Starts connecting a wallet phone. With <paramref name="proofCode"/>, first consumes the SMS ownership code.</summary>
    public async Task<PaymentWalletView> Connect(Guid actor, string phoneInput, string? ip, string? proofCode)
    {
        var phone = EzpayBroker.NormalizePhone(phoneInput);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var w = await Row();
        if (w.LastOtpAt > DateTime.UtcNow.AddSeconds(-60)) throw Fail("wallet_wait", "پیش از درخواست کد تازه کمی صبر کنید.", 429);
        if (proofCode != null)
        {
            var error = await ConsumeProof(actor, phone, proofCode);
            if (error != null) { await db.SaveChangesAsync(); await transaction.CommitAsync(); throw Fail("wallet_proof_invalid", error, 400); }
        }
        var body = await broker.Call(HttpMethod.Post, "link", new { phoneNumber = phone, ipAddress = ip });
        if (!body.TryGetProperty("requestUid", out var challenge) || challenge.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw Fail("payment_provider_unavailable", "پاسخ اتصال ایزی‌پی معتبر نیست.", 502);
        var uid = EzpayBroker.Text(body, "requestUid");
        if (uid?.Length > 512) throw Fail("payment_provider_unavailable", "پاسخ اتصال ایزی‌پی معتبر نیست.", 502);
        var known = await db.WalletOwners.AnyAsync(o => o.PhoneNumber == phone);
        if (string.IsNullOrEmpty(uid) && !known && proofCode == null)
        {
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            throw Fail("wallet_phone_proof_required", "این کیف پول در ایزی‌پی وجود دارد. مالکیت شماره را با کد پیامکی تأیید کنید.", 409);
        }
        ClearChallenge(w); w.LastOtpAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(uid)) { Connect(w, actor, phone); await RememberOwner(actor, phone); membership.Log(actor, "payment_wallet_connected", EzpayBroker.MaskPhone(phone), actor); }
        else
        {
            w.PendingPhoneNumber = phone; w.RequestUid = uid; w.ChallengeId = Guid.NewGuid(); w.ChallengeUserId = actor;
            w.ChallengeExpiresAt = DateTime.UtcNow.AddMinutes(10);
            membership.Log(actor, "payment_wallet_consent_requested", EzpayBroker.MaskPhone(phone), actor);
        }
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return await View(w, actor, true);
    }

    private static void RequireChallenge(PaymentWallet w, Guid actor, Guid challenge)
    {
        if (w.ChallengeId != challenge || w.ChallengeUserId != actor || w.ChallengeExpiresAt == null || w.ChallengeExpiresAt <= DateTime.UtcNow
            || w.RequestUid == null || w.PendingPhoneNumber == null || w.FailedAttempts >= 5)
            throw Fail("wallet_consent_expired", "درخواست تأیید ایزی‌پی منقضی شده یا تغییر کرده است. کد تازه بخواهید.", 409);
    }

    /// <summary>Confirms Ezpay's consent code, or (<paramref name="resend"/>) asks Ezpay to send it again.</summary>
    public async Task<PaymentWalletView> Confirm(Guid actor, Guid challenge, string? code, bool resend)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var w = await Row(); RequireChallenge(w, actor, challenge);
        if (resend)
        {
            if (w.LastOtpAt > DateTime.UtcNow.AddSeconds(-60)) throw Fail("wallet_wait", "پیش از درخواست کد تازه کمی صبر کنید.", 429);
            await broker.Call(HttpMethod.Put, $"link/{Uri.EscapeDataString(w.RequestUid!)}/resend");
            w.LastOtpAt = DateTime.UtcNow;
        }
        else
        {
            code = NormalizeDigits(code);
            if (code == null || !Regex.IsMatch(code, "^[0-9]{4,8}$")) throw Fail("wallet_code_required", "کد تأیید ایزی‌پی را وارد کنید.", 400);
            try { await broker.Call(HttpMethod.Put, $"link/{Uri.EscapeDataString(w.RequestUid!)}", new { verifyCode = int.Parse(code, CultureInfo.InvariantCulture) }); }
            catch (BillingException) { w.FailedAttempts++; await db.SaveChangesAsync(); await transaction.CommitAsync(); throw; }
            var phone = w.PendingPhoneNumber!;
            Connect(w, actor, phone); await RememberOwner(actor, phone);
            membership.Log(actor, "payment_wallet_connected", EzpayBroker.MaskPhone(phone), actor);
        }
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return await View(w, actor, true);
    }

    private static string? NormalizeDigits(string? value) => value == null ? null
        : Regex.Replace(value.Trim(), "[۰-۹٠-٩]", c => ((char)('0' + (c.Value[0] >= '۰' ? c.Value[0] - '۰' : c.Value[0] - '٠'))).ToString());

    /// <summary>
    /// Sends a one-time code to the wallet phone by SMS from the admin's own linked Darvazeh phone (Ravand uses its SMS service here).
    /// Limits: 5 codes per phone and 20 per address an hour; a code lives 10 minutes and allows 5 attempts.
    /// </summary>
    public async Task RequestProof(Guid actor, string phoneInput, string? ip)
    {
        var phone = EzpayBroker.NormalizePhone(phoneInput);
        var since = DateTime.UtcNow.AddHours(-1);
        if (await db.WalletPhoneProofs.CountAsync(p => p.PhoneNumber == phone && p.CreatedAt > since) >= 5 ||
            (ip != null && await db.WalletPhoneProofs.CountAsync(p => p.RequestedFromIp == ip && p.CreatedAt > since) >= 20))
            throw Fail("wallet_proof_rate_limit", "درخواست‌های کد زیاد است. کمی بعد دوباره تلاش کنید.", 429);
        var device = await db.Devices.Where(d => d.UserId == actor && d.RevokedAt == null && d.AdminPausedAt == null)
            .OrderByDescending(d => d.LastSeenAt).FirstOrDefaultAsync()
            ?? throw Fail("wallet_proof_no_device", "برای ارسال کد مالکیت، ابتدا یک گوشی را به حساب مدیر متصل کنید.", 409);
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.WalletPhoneProofs.Where(p => p.PhoneNumber == phone && p.UserId == actor && !p.Consumed).ExecuteUpdateAsync(u => u.SetProperty(p => p.Consumed, true));
        db.WalletPhoneProofs.Add(new WalletPhoneProof { UserId = actor, PhoneNumber = phone, CodeHash = BCrypt.Net.BCrypt.HashPassword(code),
            RequestedFromIp = ip, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        // A system message from the admin's device: it is not counted against the admin's message allowance.
        var message = new SmsMessage { UserId = actor, DeviceId = device.Id, Direction = "outbound", PeerNumber = phone, Status = "queued",
            Body = $"کد تأیید مالکیت کیف پول دروازه: {code}\nاین کد را به کسی ندهید." };
        db.Messages.Add(message);
        db.OutboundJobs.Add(new OutboundJob { UserId = actor, DeviceId = device.Id, SmsMessageId = message.Id, ToNumber = phone, Body = message.Body, Status = "queued" });
        membership.Log(actor, "payment_wallet_proof_requested", EzpayBroker.MaskPhone(phone), actor);
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        logger.LogInformation("event_type=wallet_phone_proof phone={Phone} device={Device}", EzpayBroker.MaskPhone(phone), device.Id);
    }

    /// <summary>Returns null when the code is right (and consumes it), else the reason. Failed attempts are counted.</summary>
    private async Task<string?> ConsumeProof(Guid actor, string phone, string? input)
    {
        var code = NormalizeDigits(input);
        if (code == null || !Regex.IsMatch(code, "^[0-9]{6}$")) return "کد پیامکی شش‌رقمی را وارد کنید.";
        var proof = await db.WalletPhoneProofs.Where(p => p.UserId == actor && p.PhoneNumber == phone && !p.Consumed && p.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync();
        if (proof == null || proof.Attempts >= 5) return "کد منقضی شده است. کد تازه بخواهید.";
        if (!BCrypt.Net.BCrypt.Verify(code, proof.CodeHash)) { proof.Attempts++; return "کد پیامکی درست نیست."; }
        proof.Consumed = true;
        return null;
    }

    public async Task<object> Reusable() => await db.WalletOwners.AsNoTracking().OrderByDescending(o => o.VerifiedAt)
        .Select(o => new { o.PhoneNumber, o.VerifiedAt }).ToListAsync();

    /// <summary>Selects a wallet whose ownership was proved before. No code: Ezpay is only asked whether the broker still has access.</summary>
    public async Task<PaymentWalletView> Reuse(Guid actor, string phoneInput)
    {
        var phone = EzpayBroker.NormalizePhone(phoneInput);
        if (!await db.WalletOwners.AnyAsync(o => o.PhoneNumber == phone))
            throw Fail("wallet_not_reusable", "یک کیف پول تأییدشدهٔ قبلی انتخاب کنید یا مالکیت شماره را تأیید کنید.", 403);
        var status = await broker.Probe(phone);
        if (status.Access != "Active") throw Fail("wallet_access_inactive", "ایزی‌پی دیگر به این کیف پول دسترسی نمی‌دهد. آن را دوباره با کد ایزی‌پی متصل کنید.", 409);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var w = await Row(); Connect(w, actor, phone);
        membership.Log(actor, "payment_wallet_selected", EzpayBroker.MaskPhone(phone), actor);
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return await View(w, actor, true);
    }

    public async Task<PaymentWalletView> Merchant(Guid actor, bool enabled)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var w = await Row();
        if (enabled)
        {
            if (w.PhoneNumber == null) throw Fail("wallet_not_connected", "ابتدا کیف پول را متصل کنید.", 409);
            await broker.RequireVerified(w.PhoneNumber);
        }
        w.MerchantEnabled = enabled;
        membership.Log(actor, enabled ? "payment_wallet_merchant_enabled" : "payment_wallet_merchant_disabled", "", actor);
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        return await View(w, actor, true);
    }

    public async Task Disconnect(Guid actor)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var w = await Row();
        w.PhoneNumber = null; w.ConnectedAt = null; w.ConnectedByUserId = null; w.MerchantEnabled = false; ClearChallenge(w);
        membership.Log(actor, "payment_wallet_disconnected", "", actor);
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    /// <summary>The wallet new toman payments go into. Requires merchant services on and a verified, accessible Ezpay wallet.</summary>
    public async Task<string> PurchaseReceiver()
    {
        var w = await db.PaymentWallets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
        if (w?.PhoneNumber == null || !w.MerchantEnabled)
            throw Fail("payment_method_unavailable", "پرداخت تومانی پس از اتصال کیف پول فعال می‌شود.", 503);
        await broker.RequireVerified(w.PhoneNumber);
        return w.PhoneNumber;
    }

    /// <summary>Whether toman checkout can be offered (connected and merchant services on). Ezpay itself is checked when paying.</summary>
    public async Task<bool> Offered() => await db.PaymentWallets.AsNoTracking().AnyAsync(w => w.Id == 1 && w.PhoneNumber != null && w.MerchantEnabled);

    private async Task<string> ConnectedPhone() => await db.PaymentWallets.AsNoTracking().Where(w => w.Id == 1).Select(w => w.PhoneNumber).SingleOrDefaultAsync()
        ?? throw Fail("wallet_not_connected", "ابتدا کیف پول را متصل کنید.", 409);

    private static string Integer(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && long.TryParse(value.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n.ToString(CultureInfo.InvariantCulture) : throw Fail("payment_provider_unavailable", "مبلغ یا زمان در پاسخ ایزی‌پی معتبر نیست.", 502);

    /// <summary>Only the relay's narrow balance response, never profile data.</summary>
    public async Task<IReadOnlyList<EzpayBalance>> Balances()
    {
        var body = await broker.Call(HttpMethod.Get, $"wallet/{await ConnectedPhone()}/balances");
        if (!body.TryGetProperty("balances", out var rows) || rows.ValueKind != JsonValueKind.Array) throw Fail("payment_provider_unavailable", "پاسخ موجودی ایزی‌پی معتبر نیست.", 502);
        return rows.EnumerateArray().Select(x => new EzpayBalance(EzpayBroker.Text(x, "accountType") ?? "", Integer(x, "balance"), Integer(x, "availableBalance"))).ToArray();
    }

    public async Task<object> History(int page)
    {
        if (page is < 0 or > 100000) throw Fail("invalid_request", "صفحهٔ تاریخچه معتبر نیست.", 400);
        var body = await broker.Call(HttpMethod.Get, $"wallet/{await ConnectedPhone()}/history?currentPage={page}");
        if (!body.TryGetProperty("pageItems", out var items) || items.ValueKind != JsonValueKind.Array || !body.TryGetProperty("totalCount", out var total) || !total.TryGetInt32(out var count) || count < 0)
            throw Fail("payment_provider_unavailable", "پاسخ تاریخچهٔ ایزی‌پی معتبر نیست.", 502);
        return new { currentPage = page, total = count, items = items.EnumerateArray().Select(x => new EzpayActivity(Integer(x, "amount"), EzpayBroker.Text(x, "description"),
            EzpayBroker.Text(x, "fromOrTo"), EzpayBroker.Text(x, "transactionNumber"), long.Parse(Integer(x, "utcTimestamp"), CultureInfo.InvariantCulture), Integer(x, "balance"))).ToArray() };
    }

    /// <summary>Darvazeh's own toman orders paid into the default wallet (any wallet number it had).</summary>
    public async Task<object> Transactions(int page)
    {
        if (page is < 0 or > 100000) throw Fail("invalid_request", "صفحه معتبر نیست.", 400);
        var query = from order in db.PaymentOrders.AsNoTracking() join user in db.Users.AsNoTracking() on order.UserId equals user.Id
                    where order.Provider == "ezpay"
                    select new { order.Id, user.Email, order.PlanName, order.ReceivingPhone, order.AmountMinor, order.Currency, order.Status, order.ProviderPaymentId, order.CreatedAt, order.PaidAt };
        return new { currentPage = page, total = await query.CountAsync(), items = await query.OrderByDescending(x => x.CreatedAt).Skip(page * 25).Take(25).ToListAsync() };
    }
}
