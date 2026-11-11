using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Controllers;

public record InboundSmsDto(string From, string Body, string? ReceivedAt);
public record JobResultDto(string Status, string? Error);

[ApiController]
[Route("api/device")]
public class DeviceAgentController(AppDbContext db, EmailOtpService emailOtp) : ControllerBase
{
    private async Task<Device?> ResolveDeviceAsync()
    {
        if (!Request.Headers.TryGetValue("X-Device-Token", out var token) ||
            string.IsNullOrWhiteSpace(token))
            return null;
        return await db.Devices.Include(d => d.User)
            .FirstOrDefaultAsync(d => d.DeviceToken == token.ToString());
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

        var msg = new SmsMessage
        {
            UserId = device.UserId,
            DeviceId = device.Id,
            Direction = "inbound",
            PeerNumber = dto.From?.Trim() ?? "unknown",
            Body = dto.Body ?? "",
            Status = "received"
        };
        db.Messages.Add(msg);
        await db.SaveChangesAsync();

        try
        {
            await emailOtp.ForwardSmsEmailAsync(device.User, msg.PeerNumber, msg.Body);
            msg.EmailForwarded = device.User.ForwardEmailEnabled;
            await db.SaveChangesAsync();
        }
        catch
        {
            // keep message even if email fails
        }

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

        job.Status = status;
        job.Error = dto.Error;
        job.CompletedAt = DateTime.UtcNow;

        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == job.SmsMessageId);
        if (msg != null) msg.Status = status;

        await db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
