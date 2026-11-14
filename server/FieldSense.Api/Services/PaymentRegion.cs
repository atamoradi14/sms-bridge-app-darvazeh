using System.Text.RegularExpressions;

namespace FieldSense.Api.Services;

/// <summary>
/// Where a buyer connects from, as Cloudflare reports it (CF-IPCountry, CF-Connecting-IP). Ezpay accepts only Iranian IP addresses,
/// so a toman link is created only for buyers in Iran. Without Cloudflare in front (local development) the country is unknown and allowed.
/// </summary>
public static class PaymentRegion
{
    public record Result(string? Country, string? Ip, bool TomanAllowed);

    public static Result Of(HttpRequest request)
    {
        var raw = request.Headers["CF-IPCountry"].FirstOrDefault()?.Trim().ToUpperInvariant();
        var country = raw is { Length: 2 } && Regex.IsMatch(raw, "^[A-Z][A-Z0-9]$") ? raw : null;
        var ip = request.Headers["CF-Connecting-IP"].FirstOrDefault()?.Trim() ?? request.HttpContext.Connection.RemoteIpAddress?.ToString();
        // XX = unknown and T1 = Tor are refused too; only a missing header (no Cloudflare) is allowed.
        return new Result(country, ip, country is null || country == "IR");
    }

    /// <summary>Ezpay's card gateway page for a link. Payments succeeded there; newer API answers point to a wallet page instead.</summary>
    public static string? CardGatewayLink(string? uniqueId) =>
        uniqueId is { Length: > 0 and <= 100 } && Regex.IsMatch(uniqueId, "^[a-z0-9]+$") ? "https://ipg.ezpay.ir/pdu/" + uniqueId : null;

    public const string NotIranMessage =
        "آی‌پی شما داخل ایران نیست، بنابراین پرداخت تومانی (ایزی‌پی) کار نمی‌کند. فیلترشکن را خاموش کنید یا روش پرداخت دیگری را انتخاب کنید.";
}
