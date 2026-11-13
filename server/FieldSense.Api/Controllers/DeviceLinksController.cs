using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Net.Mail;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Controllers;
public record LinkRequestDto(string Email, string InstallationId, string DeviceName, string? AppVersion, string? PreviousDeviceToken);
public record LinkDecisionDto(bool Approve, string ComparisonCode);
[ApiController, Route("api/device-links")]
public class DeviceLinksController(AppDbContext db) : ControllerBase
{
    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);
    private async Task<DeviceLinkRequest?> PollRequest(Guid id) {
        var secret = Request.Headers["X-Link-Secret"].ToString();
        if (secret.Length != 64) return null;
        var item = await db.DeviceLinks.SingleOrDefaultAsync(r => r.Id == id);
        return item != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(item.SecretHash), Encoding.ASCII.GetBytes(Hash(secret))) ? item : null;
    }
    [HttpPost("request")]
    public async Task<IActionResult> RequestLink(LinkRequestDto dto) {
        var email = (dto.Email ?? "").Trim().ToLowerInvariant();
        if (email.Length > 254 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email ||
            !email.Contains('.') || !Guid.TryParse(dto.InstallationId, out _) || string.IsNullOrWhiteSpace(dto.DeviceName) || dto.DeviceName.Length > 80 || dto.AppVersion?.Length > 30)
            return BadRequest(new { error="اطلاعات گوشی یا ایمیل معتبر نیست." });
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        if (await db.DeviceLinks.CountAsync(r => r.CreatedAt > cutoff && r.Email == email) >= 10 ||
            await db.DeviceLinks.CountAsync(r => r.CreatedAt > cutoff && r.SourceIp == ip) >= 1000)
            return StatusCode(429, new { error="کمی بعد دوباره تلاش کنید." });
        await using var transaction = await db.Database.BeginTransactionAsync();
        // A public installation ID alone never authorizes revoking another account.
        if (!string.IsNullOrWhiteSpace(dto.PreviousDeviceToken)) {
            var previous = await db.Devices.SingleOrDefaultAsync(d => d.DeviceToken == dto.PreviousDeviceToken && d.RevokedAt == null);
            if (previous != null) { previous.RevokedAt=DateTime.UtcNow; previous.IsOnline=false; previous.DeviceToken=NewSecret(); }
        }
        var secret=NewSecret();
        var item=new DeviceLinkRequest { Email=email, InstallationId=dto.InstallationId, DeviceName=dto.DeviceName.Trim(),
            AppVersion=dto.AppVersion, SecretHash=Hash(secret), SourceIp=ip, ComparisonCode=RandomNumberGenerator.GetInt32(100000,1000000).ToString() };
        db.DeviceLinks.Add(item); await db.SaveChangesAsync(); await transaction.CommitAsync();
        // Same response for existing and unknown accounts; no OTP is sent by this endpoint.
        return Ok(new { item.Id, secret, item.ComparisonCode, item.ExpiresAt, status="pending" });
    }
    [HttpGet("{id:guid}/status")]
    public async Task<IActionResult> Status(Guid id) {
        var item=await PollRequest(id); if (item==null) return NotFound();
        if (item.ExpiresAt <= DateTime.UtcNow) return Ok(new { status="expired" });
        if (item.Status != "approved") return Ok(new { status=item.Status });
        var device=await db.Devices.AsNoTracking().SingleOrDefaultAsync(d=>d.Id==item.DeviceId && d.RevokedAt==null);
        if (device==null) return Ok(new { status="revoked" });
        return Ok(new { status="approved", device=new { device.Id, device.Name, device.DeviceToken }, email=item.Email });
    }
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id) {
        var item=await PollRequest(id); if(item==null) return NotFound();
        if(item.Status=="approved") {
            var device=await db.Devices.SingleOrDefaultAsync(d=>d.Id==item.DeviceId);
            if(device!=null) { device.RevokedAt=DateTime.UtcNow; device.IsOnline=false; device.DeviceToken=NewSecret(); }
        }
        item.Status="cancelled"; await db.SaveChangesAsync();return Ok(new {ok=true});
    }
    [Authorize, HttpGet("pending")]
    public async Task<IActionResult> Pending() {
        var email=await db.Users.Where(u=>u.Id==UserId).Select(u=>u.Email).SingleAsync();var now=DateTime.UtcNow;
        return Ok(await db.DeviceLinks.AsNoTracking().Where(r=>r.Email==email && r.Status=="pending" && r.ExpiresAt>now)
            .OrderByDescending(r=>r.CreatedAt).Select(r=>new {r.Id,r.DeviceName,r.ComparisonCode,r.CreatedAt,r.ExpiresAt}).ToListAsync());
    }
    [Authorize, HttpPost("{id:guid}/decision")]
    public async Task<IActionResult> Decision(Guid id, LinkDecisionDto dto) {
        if(dto.Approve && !await db.Users.AnyAsync(u=>u.Id==UserId && u.EmailVerifiedAt!=null))return StatusCode(403,new {code="email_verification_required"});
        await using var transaction=await db.Database.BeginTransactionAsync();
        var email=await db.Users.Where(u=>u.Id==UserId).Select(u=>u.Email).SingleAsync();
        var item=await db.DeviceLinks.SingleOrDefaultAsync(r=>r.Id==id && r.Email==email);
        if(item==null)return NotFound();
        if(item.Status!="pending" || item.ExpiresAt<=DateTime.UtcNow)return Conflict(new {error="این درخواست دیگر معتبر نیست."});
        if(dto.Approve && dto.ComparisonCode!=item.ComparisonCode)return BadRequest(new {error="کد گوشی را بررسی کنید."});
        if(dto.Approve) {
            var device=await db.Devices.SingleOrDefaultAsync(d=>d.UserId==UserId && d.InstallationId==item.InstallationId);
            if(device==null) {device=new Device {UserId=UserId,InstallationId=item.InstallationId};db.Devices.Add(device);}
            device.Name=item.DeviceName;device.DeviceModel=item.DeviceName;device.AppVersion=item.AppVersion;device.Platform="android";
            device.DeviceToken=NewSecret();device.RevokedAt=null;device.IsOnline=false;device.LastSeenAt=null;
            await db.DeviceLinks.Where(r=>r.Id!=id && r.DeviceId==device.Id && r.Status=="approved").ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Status,"revoked"));
            item.DeviceId=device.Id;item.Status="approved";
            db.Activities.Add(new AccountActivity {UserId=UserId,ActorId=UserId,Action="phone_approved",Detail=item.DeviceName});
            // Cancel other requests from the same installation for this account only.
            await db.DeviceLinks.Where(r=>r.Id!=id && r.Email==email && r.InstallationId==item.InstallationId && r.Status=="pending")
                .ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Status,"cancelled"));
        } else item.Status="rejected";
        await db.SaveChangesAsync();await transaction.CommitAsync();return Ok(new {ok=true});
    }
}
