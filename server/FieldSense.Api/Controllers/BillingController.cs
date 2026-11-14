using System.Security.Claims;
using FieldSense.Api.Data;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Controllers;

public record CheckoutDto(Guid PlanId, string Currency, string RequestKey);

[ApiController]
[Route("api/billing")]
public class BillingController(BillingService billing, AppDbContext db, ILogger<BillingController> logger) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);
    [Authorize, HttpGet("catalog")]
    public async Task<IActionResult> Catalog() => Ok(await billing.Catalog(UserId));

    [Authorize, HttpPost("checkout")]
    public async Task<IActionResult> Checkout(CheckoutDto dto)
    {
        if(!await db.Users.AnyAsync(u=>u.Id==UserId && u.EmailVerifiedAt!=null))return StatusCode(403,new {code="email_verification_required",error="ابتدا ایمیل خود را تأیید کنید."});
        var currency = (dto.Currency ?? "").ToUpperInvariant();
        if (currency == "IRT")
        {
            // Ezpay works only from Iranian IP addresses: no link is created for a buyer abroad or behind a VPN.
            var region = PaymentRegion.Of(Request);
            logger.LogInformation("event_type=toman_checkout_region country={Country} allowed={Allowed}", region.Country ?? "unknown", region.TomanAllowed);
            if (!region.TomanAllowed)
                return StatusCode(403, new { code = "toman_requires_iran_ip", error = PaymentRegion.NotIranMessage, country = region.Country });
        }
        try
        {
            var order = await billing.Checkout(UserId, dto.PlanId, currency, dto.RequestKey ?? "");
            if (order.Provider != "ezpay") return Ok(new { order.Id, order.Status, order.CheckoutUrl });
            // Open Ezpay's card gateway first (where toman payments have succeeded); keep Ezpay's own link as the alternative.
            var card = PaymentRegion.CardGatewayLink(order.ProviderUniqueId);
            var primary = card ?? order.CheckoutUrl;
            return Ok(new { order.Id, order.Status, CheckoutUrl = primary, AlternateUrl = order.CheckoutUrl != primary ? order.CheckoutUrl : null });
        }
        catch (BillingException error) { return StatusCode(error.Status, new { error = error.Message, code = error.Code }); }
    }

    /// <summary>Whether toman (Ezpay) checkout can work from the caller's connection.</summary>
    [AllowAnonymous, HttpGet("region")]
    public IActionResult Region()
    {
        var region = PaymentRegion.Of(Request);
        return Ok(new { country = region.Country, tomanAllowed = region.TomanAllowed });
    }

    [Authorize, HttpGet("orders")]
    public async Task<IActionResult> Orders() => Ok(await db.PaymentOrders.AsNoTracking().Where(p => p.UserId == UserId)
        .OrderByDescending(p => p.CreatedAt).Take(100).Select(p => new {
            p.Id, p.PlanName, p.DurationDays, p.MessageLimit, p.Currency, p.AmountMinor, p.RefundedMinor,
            p.Status, p.CreatedAt, p.PaidAt, p.FulfilledAt }).ToListAsync());

    [Authorize, HttpPost("orders/{id:guid}/reconcile")]
    public async Task<IActionResult> Reconcile(Guid id)
    {
        try
        {
            var order = await billing.Reconcile(UserId, id);
            return order == null ? NotFound() : Ok(new { order.Id, order.Status, order.PaidAt, order.FulfilledAt });
        }
        catch (BillingException error) { return StatusCode(error.Status, new { error = error.Message, code = error.Code }); }
    }

    [AllowAnonymous, HttpPost("stripe/webhook"), RequestSizeLimit(262144)]
    public async Task<IActionResult> Webhook()
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            await billing.Webhook(await reader.ReadToEndAsync(), Request.Headers["Stripe-Signature"].ToString());
            return Ok(new { received = true });
        }
        catch (BillingException error) { return StatusCode(error.Status, new { error = error.Code }); }
        catch (Stripe.StripeException) { return BadRequest(new { error = "invalid_signature" }); }
        catch (System.Text.Json.JsonException) { return BadRequest(new { error = "invalid_payload" }); }
        catch (ArgumentException) { return BadRequest(new { error = "invalid_signature" }); }
    }
}
