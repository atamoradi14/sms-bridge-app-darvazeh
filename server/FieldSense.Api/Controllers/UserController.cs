using System.Security.Claims;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Controllers;

public record UpdateSettingsDto(bool ForwardEmailEnabled, string? ForwardEmailAddress);
public record SendSmsDto(string ToNumber, string Body, string? ClientMessageId = null);
public record PairDeviceDto(string Name, string? PhoneNumber);
public record DeleteMessagesDto(Guid[] Ids);

[ApiController]
[Authorize]
[Route("api/user")]
public class UserController(AppDbContext db, MembershipService membership) : ControllerBase
{
    private Guid UserId => Guid.Parse(
        User.FindFirstValue("sub")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("No user id"));

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await db.Users.FirstAsync(u => u.Id == UserId);
        var devices = await db.Devices.AsNoTracking().Where(d => d.UserId == UserId).ToListAsync();
        return Ok(new
        {
            user.Id,
            user.Email, user.Username, hasPassword = user.PasswordHash != null || user.Role==UserRole.Admin,
            emailVerified = user.EmailVerifiedAt != null, user.EmailVerifiedAt, user.TrialStartedAt,
            role = user.Role.ToString(),
            subscription = await membership.Access(user.Id),
            user.ForwardEmailEnabled,
            user.ForwardEmailAddress,
            devices = devices.Select(d => new
            {
                d.Id,
                d.Name,
                d.PhoneNumber,
                d.LastIpAddress,
                d.Platform,
                d.DeviceModel,
                d.AppVersion,
                isOnline = d.AdminPausedAt==null && d.RevokedAt == null && d.LastSeenAt > DateTime.UtcNow.AddMinutes(-3),
                d.LastSeenAt, d.BatteryLevel, d.IsCharging, d.NetworkType, d.NetworkValidated, d.SmsPermissionsGranted,
                d.BatteryOptimizationExempt, d.HealthReportedAt, d.RevokedAt, d.AdminPausedAt
            })
        });
    }

    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts() => Ok(await db.DeviceAlerts.AsNoTracking().Where(a=>a.UserId==UserId)
        .OrderByDescending(a=>a.CreatedAt).Take(100).ToListAsync());
    [HttpPost("alerts/{id:guid}/read")]
    public async Task<IActionResult> ReadAlert(Guid id) {
        var item=await db.DeviceAlerts.SingleOrDefaultAsync(a=>a.Id==id && a.UserId==UserId);
        if(item==null)return NotFound();item.ReadAt=DateTime.UtcNow;await db.SaveChangesAsync();return Ok(new {ok=true});
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsDto dto)
    {
        await using var transaction=await db.Database.BeginTransactionAsync();
        var user = await db.Users.FirstAsync(u => u.Id == UserId);
        if(user.AuthVersion!=(int.TryParse(User.FindFirstValue("auth_version"),out var version)?version:0))return Unauthorized();
        user.ForwardEmailEnabled = dto.ForwardEmailEnabled;
        user.ForwardEmailAddress = string.IsNullOrWhiteSpace(dto.ForwardEmailAddress)
            ? user.Email
            : dto.ForwardEmailAddress.Trim();
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Ok(new { ok = true });
    }

    [HttpGet("messages")]
    public async Task<IActionResult> Messages([FromQuery] int take = 100)
    {
        take = Math.Clamp(take, 1, 500);
        var items = await db.Messages.AsNoTracking()
            .Where(m => m.UserId == UserId && m.DeletedAt==null)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .Select(m => new
            {
                m.Id,
                m.Direction,
                m.PeerNumber,
                m.Body,
                m.Status,
                m.EmailForwarded, m.EmailForwardStatus, m.BatteryLevel, m.IsCharging, m.NetworkType,
                m.CreatedAt
            })
            .ToListAsync();
        return Ok(items);
    }

    [HttpGet("messages/page")]
    public async Task<IActionResult> MessagePage(string? search=null,string? direction=null,int page=1,int pageSize=20)=>Ok(await MessageHistory.Page(db,UserId,search,direction,page,pageSize));
    [HttpPost("messages/delete")]
    public async Task<IActionResult> DeleteMessages(DeleteMessagesDto dto) {
        if(dto.Ids==null)return BadRequest();
        await using var transaction=await db.Database.BeginTransactionAsync();
        var error=await MessageHistory.Delete(db,UserId,dto.Ids);
        if(error!=null)return StatusCode(error=="not_found"?404:409,new {code=error});
        membership.Log(UserId,"messages_deleted",dto.Ids.Length.ToString());
        await db.SaveChangesAsync();await transaction.CommitAsync();return Ok(new {ok=true});
    }

    /// <summary>
    /// Legacy manual pair. Prefer phone OTP login (client=phone) which auto-links.
    /// </summary>
    [HttpPost("devices/pair")]
    public async Task<IActionResult> PairDevice([FromBody] PairDeviceDto dto)
    {
        if(!await db.Users.AnyAsync(u=>u.Id==UserId && u.EmailVerifiedAt!=null))return StatusCode(403,new {code="email_verification_required"});
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');
        var device = new Device
        {
            UserId = UserId,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? "Iran phone" : dto.Name.Trim(),
            PhoneNumber = dto.PhoneNumber,
            DeviceToken = token
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return Ok(new { device.Id, device.Name, device.DeviceToken, device.PhoneNumber });
    }

    [HttpDelete("devices/{id:guid}")]
    public async Task<IActionResult> RemoveDevice(Guid id)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.UserId == UserId);
        if (device == null) return NotFound();
        await db.DeviceLinks.Where(r=>r.DeviceId==device.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Status,"revoked"));
        db.Devices.Remove(device);
        await db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpPost("sms/send")]
    public async Task<IActionResult> SendSms([FromBody] SendSmsDto dto)
    {
        var user = await db.Users.FirstAsync(u => u.Id == UserId);
        var device = await db.Devices
            .Where(d => d.UserId == UserId && d.RevokedAt == null && d.AdminPausedAt==null)
            .OrderByDescending(d => d.LastSeenAt)
            .FirstOrDefaultAsync();
        if (device == null)
            return BadRequest(new { error = "No linked phone. Request a connection in the Android app and approve it here." });

        var to = dto.ToNumber.Trim();
        var body = dto.Body?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(body))
            return BadRequest(new { error = "Phone number and message required" });

        if (body.Length > 4000 || to.Length > 40) return BadRequest(new { error = "پیام یا شماره بیش از حد طولانی است." });
        var clientId = string.IsNullOrWhiteSpace(dto.ClientMessageId) ? null : "out:" + dto.ClientMessageId.Trim();
        if (clientId?.Length > 150) return BadRequest(new { error = "Invalid request identifier" });
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (clientId != null)
        {
            var existing = await db.Messages.FirstOrDefaultAsync(m => m.UserId == UserId && m.ClientMessageId == clientId);
            if (existing != null) return Ok(new { messageId = existing.Id, status = existing.Status });
        }
        var msg = new SmsMessage
        {
            UserId = UserId, DeviceId = device.Id, Direction = "outbound", PeerNumber = to,
            Body = body, Status = "queued", ClientMessageId = clientId, BatteryLevel=device.BatteryLevel, IsCharging=device.IsCharging, NetworkType=device.NetworkType
        };
        var denial = await membership.Reserve(UserId, "outbound", msg);
        if (denial != null) return StatusCode(403, new { error = "ارسال در اشتراک شما در دسترس نیست. وضعیت اشتراک و ظرفیت پیام‌ها را بررسی کنید.", code = denial });
        db.Messages.Add(msg);
        var job = new OutboundJob
        {
            UserId = UserId, DeviceId = device.Id, SmsMessageId = msg.Id,
            ToNumber = to, Body = body, Status = "queued"
        };
        db.OutboundJobs.Add(job);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok(new { messageId = msg.Id, jobId = job.Id, status = "queued" });
    }
}
