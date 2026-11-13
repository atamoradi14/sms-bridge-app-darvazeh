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
}
