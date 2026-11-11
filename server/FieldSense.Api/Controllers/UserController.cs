using System.Security.Claims;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Controllers;

public record UpdateSettingsDto(bool ForwardEmailEnabled, string? ForwardEmailAddress);
public record SendSmsDto(string ToNumber, string Body);
public record PairDeviceDto(string Name, string? PhoneNumber);

[ApiController]
[Authorize]
[Route("api/user")]
public class UserController(AppDbContext db) : ControllerBase
{
    private Guid UserId => Guid.Parse(
        User.FindFirstValue("sub")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("No user id"));

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == UserId);
        var devices = await db.Devices.AsNoTracking().Where(d => d.UserId == UserId).ToListAsync();
        return Ok(new
        {
            user.Id,
            user.Email,
            role = user.Role.ToString(),
            user.CanRelaySms,
            user.ForwardEmailEnabled,
            user.ForwardEmailAddress,
            devices = devices.Select(d => new
            {
                d.Id,
                d.Name,
                d.PhoneNumber,
                d.LastIpAddress,
                d.Platform,
                d.AppVersion,
                d.IsOnline,
                d.LastSeenAt
            })
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSettingsDto dto)
    {
        var user = await db.Users.FirstAsync(u => u.Id == UserId);
        user.ForwardEmailEnabled = dto.ForwardEmailEnabled;
        user.ForwardEmailAddress = string.IsNullOrWhiteSpace(dto.ForwardEmailAddress)
            ? user.Email
            : dto.ForwardEmailAddress.Trim();
        await db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpGet("messages")]
    public async Task<IActionResult> Messages([FromQuery] int take = 100)
    {
        take = Math.Clamp(take, 1, 500);
        var items = await db.Messages.AsNoTracking()
            .Where(m => m.UserId == UserId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .Select(m => new
            {
                m.Id,
                m.Direction,
                m.PeerNumber,
                m.Body,
                m.Status,
                m.EmailForwarded,
                m.CreatedAt
            })
            .ToListAsync();
        return Ok(items);
    }

    /// <summary>
    /// Legacy manual pair. Prefer phone OTP login (client=phone) which auto-links.
    /// </summary>
    [HttpPost("devices/pair")]
    public async Task<IActionResult> PairDevice([FromBody] PairDeviceDto dto)
    {
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
        db.Devices.Remove(device);
        await db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpPost("sms/send")]
    public async Task<IActionResult> SendSms([FromBody] SendSmsDto dto)
    {
        var user = await db.Users.FirstAsync(u => u.Id == UserId);
        if (!user.CanRelaySms && user.Role != UserRole.Admin)
            return StatusCode(403, new { error = "Admin has not granted SMS relay for this account" });

        var device = await db.Devices
            .Where(d => d.UserId == UserId)
            .OrderByDescending(d => d.LastSeenAt)
            .FirstOrDefaultAsync();
        if (device == null)
            return BadRequest(new { error = "No linked Iran phone. Sign in on the Android app with the same email." });

        var to = dto.ToNumber.Trim();
        var body = dto.Body?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(body))
            return BadRequest(new { error = "Phone number and message required" });

        var msg = new SmsMessage
        {
            UserId = UserId,
            DeviceId = device.Id,
            Direction = "outbound",
            PeerNumber = to,
            Body = body,
            Status = "queued"
        };
        db.Messages.Add(msg);
        await db.SaveChangesAsync();

        var job = new OutboundJob
        {
            UserId = UserId,
            DeviceId = device.Id,
            SmsMessageId = msg.Id,
            ToNumber = to,
            Body = body,
            Status = "queued"
        };
        db.OutboundJobs.Add(job);
        await db.SaveChangesAsync();

        return Ok(new { messageId = msg.Id, jobId = job.Id, status = "queued" });
    }
}
