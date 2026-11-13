using System.Security.Claims;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FieldSense.Api.Controllers;

public record WalletPhoneDto(string PhoneNumber);
public record WalletProofDto(string PhoneNumber, string Code);
public record WalletConsentDto(string VerifyCode);
public record WalletMerchantDto(bool Enabled);

/// <summary>The default receiving wallet for toman payments, connected by phone number (same flow as Ravand's superadmin wallet page).</summary>
[ApiController]
[Authorize(Policy = "Owner")]
[Route("api/admin/wallet")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("password-auth")]
public class AdminWalletController(PaymentWalletService wallet, IOptions<BillingSettings> options, IWebHostEnvironment environment, ILogger<AdminWalletController> logger) : ControllerBase
{
    private Guid Actor => Guid.Parse(User.FindFirstValue("sub")!);
    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool RelayConfigured => options.Value.EzpayConfiguredFor(environment.IsDevelopment());

    private async Task<IActionResult> Run(Func<Task<object?>> action)
    {
        if (!RelayConfigured) return StatusCode(503, new { code = "payment_not_configured", error = "اتصال به سرور پرداخت ایران پیکربندی نشده است." });
        try { var result = await action(); return result == null ? NoContent() : Ok(result); }
        catch (BillingException error)
        {
            logger.LogWarning("event_type=payment_wallet_request code={Code} status={Status}", error.Code, error.Status);
            return StatusCode(error.Status, new { code = error.Code, error = error.Message, resultCode = error.ResultCode });
        }
    }

    [HttpGet]
    public async Task<IActionResult> View() => Ok(await wallet.View(Actor, RelayConfigured));
    [HttpGet("reusable")]
    public Task<IActionResult> Reusable() => Run(async () => await wallet.Reusable());
    [HttpPost("connection")]
    public Task<IActionResult> Connect(WalletPhoneDto dto) => Run(async () => await wallet.Connect(Actor, dto.PhoneNumber, Ip, null));
    [HttpPost("phone-proof/request")]
    public Task<IActionResult> RequestProof(WalletPhoneDto dto) => Run(async () => { await wallet.RequestProof(Actor, dto.PhoneNumber, Ip); return new { sent = true }; });
    [HttpPost("phone-proof/confirm")]
    public Task<IActionResult> ConfirmProof(WalletProofDto dto) => Run(async () => await wallet.Connect(Actor, dto.PhoneNumber, Ip, dto.Code ?? ""));
    [HttpPut("consent/{challengeId:guid}")]
    public Task<IActionResult> Confirm(Guid challengeId, WalletConsentDto dto) => Run(async () => await wallet.Confirm(Actor, challengeId, dto.VerifyCode, false));
    [HttpPost("consent/{challengeId:guid}/resend")]
    public Task<IActionResult> Resend(Guid challengeId) => Run(async () => await wallet.Confirm(Actor, challengeId, null, true));
    [HttpPost("reuse")]
    public Task<IActionResult> Reuse(WalletPhoneDto dto) => Run(async () => await wallet.Reuse(Actor, dto.PhoneNumber));
    [HttpPut("merchant")]
    public Task<IActionResult> Merchant(WalletMerchantDto dto) => Run(async () => await wallet.Merchant(Actor, dto.Enabled));
    [HttpDelete]
    public Task<IActionResult> Disconnect() => Run(async () => { await wallet.Disconnect(Actor); return null; });
    [HttpGet("balances")]
    public Task<IActionResult> Balances() => Run(async () => await wallet.Balances());
    [HttpGet("history")]
    public Task<IActionResult> History(int currentPage = 0) => Run(async () => await wallet.History(currentPage));
    [HttpGet("transactions")]
    public async Task<IActionResult> Transactions(int currentPage = 0)
    {
        try { return Ok(await wallet.Transactions(currentPage)); }
        catch (BillingException error) { return StatusCode(error.Status, new { code = error.Code, error = error.Message }); }
    }
}
