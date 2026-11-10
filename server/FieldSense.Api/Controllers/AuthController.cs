using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldSense.Api.Controllers;

public record RequestOtpDto(string Email);
public record VerifyOtpDto(
    string Email,
    string Code,
    string? AdminPassword,
    /// <summary>web (default) or phone — phone auto-links a device to the account.</summary>
    string? Client = null,
    string? InstallationId = null,
    string? DeviceName = null,
    string? PhoneNumber = null);

[ApiController]
[Route("api/auth")]
public class AuthController(
    AppDbContext db,
    EmailOtpService emailOtp,
    JwtTokenService jwt,
    IOptions<AppSettings> options) : ControllerBase
{
    private readonly AppSettings _settings = options.Value;

    [HttpPost("request-otp")]
    public async Task<IActionResult> RequestOtp([FromBody] RequestOtpDto dto)
    {
        var (ok, detail, debugCode) = await emailOtp.SendLoginOtpAsync(dto.Email);
        if (!ok) return BadRequest(new { error = detail });
        return Ok(new
        {
            ok = true,
            detail,
            debugCode
        });
    }

    [HttpPost("verify-otp")]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpDto dto)
    {
        var email = dto.Email.Trim().ToLowerInvariant();
        if (!await emailOtp.VerifyOtpAsync(email, dto.Code))
            return Unauthorized(new { error = "Invalid or expired code" });

        var isAdminEmail = string.Equals(email, _settings.AdminEmail.Trim(), StringComparison.OrdinalIgnoreCase);
        if (isAdminEmail)
        {
            if (string.IsNullOrEmpty(dto.AdminPassword) ||
                dto.AdminPassword != _settings.AdminPassword)
            {
                return Unauthorized(new { error = "Admin password required", needAdminPassword = true });
            }
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
        {
            user = new AppUser
            {
                Email = email,
                Role = isAdminEmail ? UserRole.Admin : UserRole.User,
                CanRelaySms = isAdminEmail,
                ForwardEmailAddress = email
            };
            db.Users.Add(user);
        }
        else if (isAdminEmail)
        {
            user.Role = UserRole.Admin;
        }

        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        object? devicePayload = null;
        var client = (dto.Client ?? "web").Trim().ToLowerInvariant();
        if (client is "phone" or "android" or "device")
        {
            var device = await UpsertPhoneDeviceAsync(user.Id, dto);
            devicePayload = new
            {
                device.Id,
                device.Name,
                device.DeviceToken,
                device.PhoneNumber,
                device.IsOnline,
                device.LastSeenAt
            };
        }

        var token = jwt.CreateToken(user);
        return Ok(new
        {
            token,
            user = new
            {
                user.Id,
                user.Email,
                role = user.Role.ToString(),
                user.CanRelaySms,
                user.ForwardEmailEnabled,
                user.ForwardEmailAddress
            },
            device = devicePayload
        });
    }

    private static string NewDeviceToken() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');

    private async Task<Device> UpsertPhoneDeviceAsync(Guid userId, VerifyOtpDto dto)
    {
        var installId = string.IsNullOrWhiteSpace(dto.InstallationId)
            ? null
            : dto.InstallationId.Trim();
        var name = string.IsNullOrWhiteSpace(dto.DeviceName) ? "Iran phone" : dto.DeviceName.Trim();

        Device? device = null;
        if (!string.IsNullOrEmpty(installId))
        {
            device = await db.Devices.FirstOrDefaultAsync(d =>
                d.UserId == userId && d.InstallationId == installId);
        }

        if (device == null && string.IsNullOrEmpty(installId))
        {
            // One phone per account when install id is missing: reuse latest.
            device = await db.Devices
                .Where(d => d.UserId == userId)
                .OrderByDescending(d => d.LastSeenAt ?? d.CreatedAt)
                .FirstOrDefaultAsync();
        }

        if (device == null)
        {
            device = new Device
            {
                UserId = userId,
                Name = name,
                InstallationId = installId,
                PhoneNumber = string.IsNullOrWhiteSpace(dto.PhoneNumber) ? null : dto.PhoneNumber.Trim(),
                DeviceToken = NewDeviceToken(),
                IsOnline = true,
                LastSeenAt = DateTime.UtcNow
            };
            db.Devices.Add(device);
        }
        else
        {
            device.DeviceToken = NewDeviceToken();
            device.Name = name;
            if (!string.IsNullOrEmpty(installId))
                device.InstallationId = installId;
            if (!string.IsNullOrWhiteSpace(dto.PhoneNumber))
                device.PhoneNumber = dto.PhoneNumber.Trim();
            device.IsOnline = true;
            device.LastSeenAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return device;
    }
}
