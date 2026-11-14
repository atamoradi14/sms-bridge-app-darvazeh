using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using FieldSense.Api.Models;
using Microsoft.Extensions.Options;
using Stripe;

namespace FieldSense.Api.Services;

public interface IStripeGateway
{
    Task<JsonElement> CreateCheckout(PaymentOrder order, string email);
    Task<JsonElement> Checkout(string id);
    Task<JsonElement> Charge(string id);
    Task<JsonElement> PaymentIntent(string id);
}

public class StripeGateway(HttpClient http, IOptions<BillingSettings> options, IWebHostEnvironment environment,
    ILogger<StripeGateway> logger) : IStripeGateway
{
    private readonly BillingSettings settings = options.Value;

    private async Task<JsonElement> Request(string path, Dictionary<string, string>? form = null, string? key = null)
    {
        var uri = new Uri(settings.StripeApiBase.TrimEnd('/') + "/");
        if (uri.AbsoluteUri != "https://api.stripe.com/" && !(environment.IsDevelopment() && uri.IsLoopback))
            throw new BillingException("billing_configuration", "پرداخت در دسترس نیست.", 503);
        using var request = new HttpRequestMessage(form == null ? HttpMethod.Get : HttpMethod.Post, new Uri(uri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.StripeSecretKey);
        request.Headers.Add("Stripe-Version", StripeConfiguration.ApiVersion);
        if (key != null) request.Headers.Add("Idempotency-Key", key);
        if (form != null) request.Content = new FormUrlEncodedContent(form);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Payment provider request failed with HTTP {Status}", (int)response.StatusCode);
            throw new BillingException("payment_provider_unavailable", "درگاه پرداخت موقتاً در دسترس نیست. دوباره تلاش کنید.", 503);
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    public Task<JsonElement> CreateCheckout(PaymentOrder order, string email)
    {
        var origin = order.ReturnOrigin;
        var form = new Dictionary<string, string> {
            ["mode"] = "payment", ["customer_email"] = email,
            ["client_reference_id"] = order.UserId.ToString(),
            ["success_url"] = origin + (order.Product == "vpn" ? "/?view=vpn&" : "/?") + "payment=success&order=" + order.Id + "&session_id={CHECKOUT_SESSION_ID}",
            ["cancel_url"] = origin + (order.Product == "vpn" ? "/?view=vpn&" : "/?") + "payment=cancelled&order=" + order.Id,
            ["expires_at"] = new DateTimeOffset(order.CreatedAt.AddHours(1), TimeSpan.Zero).ToUnixTimeSeconds().ToString(),
            // Current Checkout API uses payment methods configured in the Stripe Dashboard.
            ["allow_promotion_codes"] = "false",
            ["adaptive_pricing[enabled]"] = "false", ["automatic_tax[enabled]"] = "false",
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = "usd",
            ["line_items[0][price_data][unit_amount]"] = order.AmountMinor.ToString(),
            ["line_items[0][price_data][product_data][name]"] = "Darvazeh · " + order.PlanName,
            ["line_items[0][price_data][product_data][description]"] = order.Product == "vpn" ? "Iran VPN · one user · one calendar month · manual renewal" : $"{order.DurationDays} days · {order.MessageLimit} messages · {order.EmailForwardLimit} email forwards · one period, no automatic renewal",
            ["metadata[order_id]"] = order.Id.ToString(), ["metadata[user_id]"] = order.UserId.ToString(),
            ["payment_intent_data[metadata][order_id]"] = order.Id.ToString(),
            ["payment_intent_data[metadata][user_id]"] = order.UserId.ToString(),
        };
        return Request("v1/checkout/sessions", form, "darvazeh-checkout-" + order.Id);
    }

    private static string Id(string id, string prefix)
    {
        if (!Regex.IsMatch(id, "^" + prefix + "[A-Za-z0-9_]{1,240}$"))
            throw new BillingException("invalid_payment", "شناسه پرداخت معتبر نیست.");
        return id;
    }
    public Task<JsonElement> Checkout(string id) => Request("v1/checkout/sessions/" + Id(id, "cs_") + "?expand[]=line_items");
    public Task<JsonElement> Charge(string id) => Request("v1/charges/" + Id(id, "ch_"));
    public Task<JsonElement> PaymentIntent(string id) => Request("v1/payment_intents/" + Id(id, "pi_"));
}
