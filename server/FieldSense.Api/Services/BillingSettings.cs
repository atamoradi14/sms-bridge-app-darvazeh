namespace FieldSense.Api.Services;

public class BillingSettings
{
    public bool StripeEnabled { get; set; }
    public string StripeSecretKey { get; set; } = "";
    public string StripeWebhookSecret { get; set; } = "";
    public string PortalOrigin { get; set; } = "https://darvazeh.daryajan.com";
    public string LegalName { get; set; } = "Niliarya Limited";
    public string TradingName { get; set; } = "ND ORBIT";
    public string StripeApiBase { get; set; } = "https://api.stripe.com";
    public bool IsLive => StripeSecretKey.Contains("_live_", StringComparison.Ordinal);
    public bool Configured => StripeEnabled && !string.IsNullOrWhiteSpace(StripeSecretKey) &&
        !string.IsNullOrWhiteSpace(StripeWebhookSecret);

    // Toman payments: the Iran-hosted Ezpay relay (its /broker routes), the same relay and protocol Ravand uses.
    // Payments go into Darvazeh's own default wallet, connected by phone number on the admin wallet page.
    public bool EzpayEnabled { get; set; }
    public string PaymentRelayEndpoint { get; set; } = "";
    public string PaymentRelayApiKey { get; set; } = "";
    /// <summary>Configured relay: HTTPS, or loopback HTTP in development (local test fixtures).</summary>
    public bool EzpayConfiguredFor(bool development) => EzpayEnabled && !string.IsNullOrWhiteSpace(PaymentRelayApiKey) &&
        IsRelayEndpoint(PaymentRelayEndpoint, development);

    /// <summary>An absolute URL without credentials, query or fragment: HTTPS, or HTTP to a loopback host when allowed.</summary>
    public static bool IsRelayEndpoint(string? value, bool allowLoopbackHttp) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "" &&
        (uri.Scheme == Uri.UriSchemeHttps || (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}

public static class PaymentMoney
{
    // Storage/display use toman; the future merchant adapter must explicitly
    // submit ten conventional rial per toman if its protocol uses IRR.
    public static long TomanToRial(long toman) => checked(toman * 10);
}

public class BillingException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    /// <summary>Ezpay's result code when Ezpay refused the request.</summary>
    public int? ResultCode { get; init; }
}
