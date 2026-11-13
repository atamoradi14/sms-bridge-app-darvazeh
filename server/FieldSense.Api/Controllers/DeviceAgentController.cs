using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Controllers;

public record InboundSmsDto(string From, string Body, string? ReceivedAt, string? ClientMessageId = null);
public record JobResultDto(string Status, string? Error);

[ApiController]
[Route("api/device")]
public class DeviceAgentController(AppDbContext db, EmailOtpService emailOtp, MembershipService membership) : ControllerBase
{
    private async Task<Device?> ResolveDeviceAsync()
    {
        if (!Request.Headers.TryGetValue("X-Device-Token", out var token) ||
            string.IsNullOrWhiteSpace(token))
            return null;
        var device = await db.Devices.Include(d => d.User)
            .FirstOrDefaultAsync(d => d.DeviceToken == token.ToString() && d.RevokedAt == null && d.AdminPausedAt==null && d.User.SuspendedAt==null && d.User.DeletedAt==null);
        if (device != null) {
            device.LastSeenAt=DateTime.UtcNow; device.IsOnline=true;
            if(int.TryParse(Request.Headers["X-Battery-Level"],out var level) && level is >=0 and <=100) {
                device.BatteryLevel=level;device.HealthReportedAt=DateTime.UtcNow;
            }
            if(bool.TryParse(Request.Headers["X-Is-Charging"],out var charging))device.IsCharging=charging;
            if(bool.TryParse(Request.Headers["X-Network-Validated"],out var validated))device.NetworkValidated=validated;
            if(bool.TryParse(Request.Headers["X-Sms-Permissions"],out var sms))device.SmsPermissionsGranted=sms;
            if(bool.TryParse(Request.Headers["X-Battery-Exempt"],out var exempt))device.BatteryOptimizationExempt=exempt;
            var network=Request.Headers["X-Network-Type"].ToString();
            if(network is "wifi" or "cellular" or "ethernet" or "vpn" or "offline" or "other")device.NetworkType=network;
        }
        return device;
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect() {
        var device=await ResolveDeviceAsync();if(device==null)return Unauthorized();
        device.RevokedAt=DateTime.UtcNow;device.IsOnline=false;
        device.DeviceToken=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await db.DeviceLinks.Where(r=>r.DeviceId==device.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Status,"revoked"));
        await db.SaveChangesAsync();return Ok(new {ok=true});
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat()
    {
        var device = await ResolveDeviceAsync();
        if (device == null) return Unauthorized(new { error = "Invalid device token" });
        device.IsOnline = true;
        device.LastSeenAt = DateTime.UtcNow;
        var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        device.LastIpAddress = !string.IsNullOrWhiteSpace(forwarded)
            ? forwarded.Split(',')[0].Trim()
            : HttpContext.Connection.RemoteIpAddress?.ToString();
        await db.SaveChangesAsync();
        return Ok(new
        {
            ok = true,
            serverTime = DateTime.UtcNow,
            subscription = await membership.Access(device.UserId),
            apiBase = $"{Request.Scheme}://{Request.Host.Value}".TrimEnd('/')
        });
    }

    [HttpPost("inbound-sms")]
    public async Task<IActionResult> InboundSms([FromBody] InboundSmsDto dto)
    {
        var device = await ResolveDeviceAsync();
        if (device == null) return Unauthorized(new { error = "Invalid device token" });

        device.IsOnline = true;
        device.LastSeenAt = DateTime.UtcNow;

        var clientId = string.IsNullOrWhiteSpace(dto.ClientMessageId) ? null : "in:" + dto.ClientMessageId.Trim();
        if (clientId?.Length > 150 || (dto.Body?.Length ?? 0) > 10000) return BadRequest(new { error = "Invalid message" });
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (clientId != null)
        {
            var existing = await db.Messages.FirstOrDefaultAsync(m => m.DeviceId == device.Id && m.ClientMessageId == clientId);
            if (existing != null) return Ok(new { messageId = existing.Id, emailForwarded = existing.EmailForwarded });
        }
        var msg = new SmsMessage
        {
            UserId = device.UserId,
            DeviceId = device.Id,
            Direction = "inbound",
            PeerNumber = dto.From?.Trim() ?? "unknown",
            Body = dto.Body ?? "",
            ClientMessageId = clientId,
            Status = "received", BatteryLevel=device.BatteryLevel, IsCharging=device.IsCharging, NetworkType=device.NetworkType
        };
        var denial = await membership.Reserve(device.UserId, "inbound", msg);
        if (denial != null) return StatusCode(403, new { error = "دریافت در اشتراک شما در دسترس نیست.", code = denial });
        var emailSubscription = await membership.ReserveEmail(device.User, msg);
        if(emailSubscription!=null)emailOtp.QueueSmsEmail(device.User,msg,emailSubscription.Value);
        db.Messages.Add(msg);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok(new { messageId = msg.Id, emailForwarded = msg.EmailForwarded });
    }

    [HttpGet("outbound/pending")]
    public async Task<IActionResult> PendingOutbound()
    {
        var device = await ResolveDeviceAsync();
        if (device == null) return Unauthorized(new { error = "Invalid device token" });

        device.IsOnline = true;
        device.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var access = await membership.Access(device.UserId);
        if (!access.Active || !access.SendEnabled) return Ok(Array.Empty<object>());

        var jobs = await db.OutboundJobs
            .Where(j => j.DeviceId == device.Id && j.Status == "queued")
            .OrderBy(j => j.CreatedAt)
            .Take(20)
            .Select(j => new { j.Id, j.ToNumber, j.Body, j.SmsMessageId })
            .ToListAsync();

        return Ok(jobs);
    }

    [HttpPost("outbound/{jobId:guid}/result")]
    public async Task<IActionResult> OutboundResult(Guid jobId, [FromBody] JobResultDto dto)
    {
        var device = await ResolveDeviceAsync();
        if (device == null) return Unauthorized(new { error = "Invalid device token" });

        var job = await db.OutboundJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.DeviceId == device.Id);
        if (job == null) return NotFound();

        var status = (dto.Status ?? "").ToLowerInvariant();
        if (status is not ("sent" or "failed"))
            return BadRequest(new { error = "Status must be sent or failed" });

        if (job.Status != "queued") return Ok(new { ok = true });
        membership.Log(device.UserId, status == "sent" ? "message_sent" : "message_failed");
        job.Status = status;
        job.Error = dto.Error;
        job.CompletedAt = DateTime.UtcNow;

        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == job.SmsMessageId);
        if (msg != null) { msg.Status = status; msg.BatteryLevel=device.BatteryLevel; msg.IsCharging=device.IsCharging; msg.NetworkType=device.NetworkType; }

        await db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
