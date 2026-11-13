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
public class BillingController(BillingService billing, AppDbContext db) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);
    [Authorize, HttpGet("catalog")]
    public async Task<IActionResult> Catalog() => Ok(await billing.Catalog(UserId));

    [Authorize, HttpPost("checkout")]
    public async Task<IActionResult> Checkout(CheckoutDto dto)
    {
        if(!await db.Users.AnyAsync(u=>u.Id==UserId && u.EmailVerifiedAt!=null))return StatusCode(403,new {code="email_verification_required",error="ابتدا ایمیل خود را تأیید کنید."});
        try
        {
            var order = await billing.Checkout(UserId, dto.PlanId, (dto.Currency ?? "").ToUpperInvariant(), dto.RequestKey ?? "");
            return Ok(new { order.Id, order.Status, order.CheckoutUrl });
        }
        catch (BillingException error) { return StatusCode(error.Status, new { error = error.Message, code = error.Code }); }
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
