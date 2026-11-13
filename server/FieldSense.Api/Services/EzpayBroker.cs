using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FieldSense.Api.Services;

/// <summary>Access: Active, NoAccess (Ezpay 103), Disabled (Ezpay 107). IdentityVerified: null when unknown. Level: the Ezpay user group (DEFAULT = not verified).</summary>
public record EzpayWalletStatus(string Access, bool? IdentityVerified, string? Level, string Source, DateTime CheckedAt);
public record WalletReadiness(bool PurchaseReady, string Reason, EzpayWalletStatus? ProviderStatus);
public record EzpayBalance(string AccountType, string Balance, string AvailableBalance);
public record EzpayActivity(string Amount, string? Description, string? FromOrTo, string? TransactionNumber, long UtcTimestamp, string Balance);

/// <summary>
/// Client for the Iran-hosted relay's /broker routes (same relay and protocol as Ravand): wallet consent by Ezpay OTP, wallet
/// status/balances/history, and payment links into a wallet. The relay holds the Ezpay broker credentials; this service holds only
/// the relay's x-api-key. Failures become <see cref="BillingException"/> with a stable code and a Persian message.
/// </summary>
public class EzpayBroker(HttpClient http, IOptions<BillingSettings> options, IWebHostEnvironment environment, ILogger<EzpayBroker> logger)
{
    private readonly BillingSettings settings = options.Value;

    public static string NormalizePhone(string? phone)
    {
        var value = (phone ?? "").Trim();
        value = Regex.Replace(value, "[۰-۹]", c => ((char)('0' + c.Value[0] - '۰')).ToString());
        if (value.StartsWith("+98", StringComparison.Ordinal)) value = "0" + value[3..];
        else if (value.StartsWith("0098", StringComparison.Ordinal)) value = "0" + value[4..];
        if (!Regex.IsMatch(value, "^09[0-9]{9}$", RegexOptions.CultureInvariant))
            throw new BillingException("invalid_phone", "شمارهٔ موبایل ایران معتبر وارد کنید.");
        return value;
    }

    public static string MaskPhone(string phone) => phone.Length == 11 ? "09*******" + phone[^2..] : "***";

    /// <summary>Approved Ezpay payment pages only.</summary>
    public static bool IsProviderLink(string? link) => Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("ezpay.ir", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".ezpay.ir", StringComparison.OrdinalIgnoreCase));

    /// <summary>Plain reasons for Ezpay result codes (spec v22 §25, plus 97 seen in production), as Ravand shows them.</summary>
    public static string? Describe(int code) => code switch
    {
        0 => null,
        97 => "ایزی‌پی رد کرد: قرارداد پذیرندگی این کیف پول در ایزی‌پی فعال نیست (کد ۹۷). مالک کیف پول باید فعال‌سازی را از ایزی‌پی بخواهد.",
        103 => "ایزی‌پی رد کرد: دسترسی به این کیف پول وجود ندارد (کد ۱۰۳). کیف پول را دوباره متصل کنید.",
        107 => "ایزی‌پی رد کرد: حساب ایزی‌پی این کیف پول غیرفعال است (کد ۱۰۷).",
        13 => "ایزی‌پی رد کرد: سطح کاربری کیف پول اجازه نمی‌دهد (کد ۱۳). مالک باید احراز هویت را در ایزی‌پی کامل کند.",
        6 or 7 => $"ایزی‌پی رد کرد: پروفایل مالک کیف پول ناقص یا نامعتبر است (کد {code}).",
        9 => "ایزی‌پی موقتاً در دسترس نیست (کد ۹). چند دقیقه بعد دوباره تلاش کنید.",
        95 or 96 or 110 => $"ایزی‌پی مبلغ این پرداخت را نپذیرفت (کد {code}).",
        111 => "ایزی‌پی رد کرد: سقف تراکنش کیف پول پر شده است (کد ۱۱۱).",
        93 => "ایزی‌پی رد کرد: لینک پرداخت منقضی شده است (کد ۹۳).",
        94 => "ایزی‌پی رد کرد: لینک پرداخت قبلاً پرداخت شده است (کد ۹۴).",
        _ => $"ایزی‌پی درخواست را رد کرد (کد {code}).",
    };

    private Uri Url(string path)
    {
        if (!settings.EzpayConfiguredFor(environment.IsDevelopment()))
            throw new BillingException("payment_not_configured", "پرداخت تومانی هنوز فعال نشده است.", 503);
        return new Uri(settings.PaymentRelayEndpoint.TrimEnd('/') + "/broker/" + path);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, Url(path));
        request.Headers.Add("x-api-key", settings.PaymentRelayApiKey);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>Calls a broker route that must answer resultCode 0. Ezpay refusals keep their reason; outages are 503.</summary>
    public async Task<JsonElement> Call(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var request = Request(method, path, body);
        try
        {
            using var response = await http.SendAsync(request, ct);
            JsonElement? json = null;
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                try { json = await response.Content.ReadFromJsonAsync<JsonElement>(ct); } catch (JsonException) { }
            }
            var code = json is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("resultCode", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : (int?)null;
            if (!response.IsSuccessStatusCode || code != 0)
            {
                logger.LogWarning("event_type=ezpay_relay method={Method} route={Route} http_status={Status} result_code={Code}",
                    method, Regex.Replace(path, "09[0-9]{9}", m => MaskPhone(m.Value)), (int)response.StatusCode, code?.ToString() ?? "none");
                if (code is { } refused && refused != 0 && Describe(refused) is { } reason)
                    throw new BillingException("ezpay_refused", reason, refused == 9 ? 503 : 409) { ResultCode = refused };
                throw new BillingException("payment_provider_unavailable", "درگاه پرداخت موقتاً در دسترس نیست. دوباره تلاش کنید.", 503);
            }
            return json!.Value;
        }
        catch (HttpRequestException) { throw new BillingException("payment_provider_unavailable", "درگاه پرداخت موقتاً در دسترس نیست. دوباره تلاش کنید.", 503); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new BillingException("payment_provider_unavailable", "پاسخ ایزی‌پی دیر رسید. پیش از تلاش دوباره وضعیت را بررسی کنید.", 503); }
    }

    private async Task<(int Status, JsonElement? Body)> Raw(string path, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path, null);
        try
        {
            using var response = await http.SendAsync(request, ct);
            JsonElement? body = null;
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                try { body = await response.Content.ReadFromJsonAsync<JsonElement>(ct); } catch (JsonException) { }
            }
            return ((int)response.StatusCode, body);
        }
        catch (HttpRequestException) { throw new BillingException("payment_provider_unavailable", "درگاه پرداخت موقتاً در دسترس نیست.", 503); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new BillingException("payment_provider_unavailable", "پاسخ ایزی‌پی دیر رسید.", 503); }
    }

    public static string? Text(JsonElement body, string property) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Asks Ezpay whether the broker can still use this wallet and whether its owner is identity-verified. Reads only.</summary>
    public async Task<EzpayWalletStatus> Probe(string phone, CancellationToken ct = default)
    {
        var (status, body) = await Raw($"wallet/{phone}/status", ct);
        if (status == 200 && body is { } ok && Text(ok, "access") is { } access)
        {
            bool? verified = ok.TryGetProperty("identityVerified", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            var mapped = access switch
            {
                "ACTIVE" => "Active", "NO_ACCESS" => "NoAccess", "DISABLED" => "Disabled",
                _ => throw new BillingException("payment_provider_unavailable", "پاسخ وضعیت ایزی‌پی معتبر نیست.", 502)
            };
            return new(mapped, verified, Text(ok, "group"), "profile", DateTime.UtcNow);
        }
        if (status != 404) throw new BillingException("payment_provider_unavailable", "وضعیت کیف پول از ایزی‌پی دریافت نشد.", 503);
        // A relay without the status route: a balance read still proves whether the broker has access (Ezpay 0, 103 or 107).
        var (balanceStatus, balances) = await Raw($"wallet/{phone}/balances", ct);
        int? code = balances is { } b && b.TryGetProperty("resultCode", out var c) && c.TryGetInt32(out var n) ? n : null;
        return (balanceStatus, code) switch
        {
            (200, 0) => new("Active", null, null, "balances", DateTime.UtcNow),
            (422, 103) => new("NoAccess", null, null, "balances", DateTime.UtcNow),
            (422, 107) => new("Disabled", null, null, "balances", DateTime.UtcNow),
            _ => throw new BillingException("payment_provider_unavailable", "وضعیت کیف پول از ایزی‌پی دریافت نشد.", 503),
        };
    }

    public async Task<WalletReadiness> Readiness(string phone, CancellationToken ct = default)
    {
        try
        {
            var status = await Probe(phone, ct);
            var reason = status.Access != "Active" ? "دسترسی ایزی‌پی به این کیف پول فعال نیست؛ پرداخت‌ها غیرفعال‌اند."
                : status.IdentityVerified != true ? "پیش از دریافت پرداخت، احراز هویت این کیف پول در ایزی‌پی لازم است."
                : "احراز هویت شده و آمادهٔ دریافت پرداخت.";
            return new(status.Access == "Active" && status.IdentityVerified == true, reason, status);
        }
        catch (BillingException) { return new(false, "وضعیت کیف پول از ایزی‌پی تأیید نشد؛ پرداخت‌ها غیرفعال‌اند.", null); }
    }

    public async Task RequireVerified(string phone, CancellationToken ct = default)
    {
        var status = await Probe(phone, ct);
        if (status.Access != "Active" || status.IdentityVerified != true)
            throw new BillingException("wallet_not_verified", "برای دریافت پرداخت، احراز هویت و دسترسی فعال ایزی‌پی لازم است.", 409);
    }

    /// <summary>Checks a broker payment answer: success code, the link's own id, the exact amount and a transaction reference when paid.</summary>
    public static void ValidatePayment(JsonElement body, string uniqueId, long amountRial)
    {
        if (!body.TryGetProperty("resultCode", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var result) || result != 0
            || Text(body, "requestid") != uniqueId
            || !body.TryGetProperty("amount", out var amount) || !long.TryParse(amount.ToString(), out var actual) || actual != amountRial)
            throw new BillingException("payment_verification_failed", "تأیید پرداخت انجام نشد.");
        if (Text(body, "status") == "SUCCESS" && string.IsNullOrWhiteSpace(Text(body, "transactionid")))
            throw new BillingException("payment_verification_failed", "ایزی‌پی شمارهٔ تراکنش را تأیید نکرد.");
    }
}
