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
    string? PhoneNumber = null,
    string? Platform = null,
    string? DeviceModel = null,
    string? AppVersion = null);

[ApiController]
[Route("api/auth")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("password-auth")]
public class AuthController(
    AppDbContext db,
    EmailOtpService emailOtp,
    JwtTokenService jwt,
    IOptions<AppSettings> options,
    MembershipService membership) : ControllerBase
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
        var isAdminEmail = string.Equals(email, _settings.AdminEmail.Trim(), StringComparison.OrdinalIgnoreCase);
        // A missing extra credential can prompt the administrator without consuming
        // the code. Supplied credentials share the same attempt budget and error.
        if (isAdminEmail && string.IsNullOrEmpty(dto.AdminPassword))
            return Unauthorized(new { error = "رمز مدیریت را وارد کنید.", needAdminPassword = true });
        var extraCredentialValid = !isAdminEmail || System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(dto.AdminPassword ?? ""),
            System.Text.Encoding.UTF8.GetBytes(_settings.AdminPassword));
        if (!await emailOtp.VerifyOtpAsync(email, dto.Code, extraCredentialValid))
            return Unauthorized(new { error = "کد ورود یا اطلاعات حساب نادرست است یا اعتبار کد به پایان رسیده است." });

        await using var transaction=await db.Database.BeginTransactionAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.DeletedAt==null && u.Email == email);
        if(user?.DeletedAt!=null || user?.SuspendedAt!=null)return Unauthorized(new {code="account_unavailable"});
        if (user == null)
        {
            user = new AppUser
            {
                Email = email,
                Role = isAdminEmail ? UserRole.Admin : UserRole.User,
                CanRelaySms = true,
                ForwardEmailAddress = email
            };
            db.Users.Add(user);
        }
        else if (isAdminEmail)
        {
            user.Role = UserRole.Admin;
            user.CanRelaySms = true;
        }

        await membership.VerifyEmail(user,independentLogin:true);
        membership.Log(user.Id, "signed_in", (dto.Client ?? "web") == "web" ? "web" : "phone");
        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        object? devicePayload = null;
        var client = (dto.Client ?? "web").Trim().ToLowerInvariant();
        if (client is "phone" or "android" or "device")
        {
            var device = await UpsertPhoneDeviceAsync(user.Id, dto);
            await db.SaveChangesAsync();
            devicePayload = new
            {
                device.Id,
                device.Name,
                device.DeviceToken,
                device.PhoneNumber,
                device.LastIpAddress,
                device.Platform,
                device.DeviceModel,
                device.AppVersion,
                device.IsOnline,
                device.LastSeenAt
            };
        }

        await transaction.CommitAsync();
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
                user.ForwardEmailAddress,
                subscription = await membership.Access(user.Id)
            },
            device = devicePayload
        });
    }

    private static string NewDeviceToken() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');

    private string? ClientIp()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return forwarded.Split(',')[0].Trim();
        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    private async Task<Device> UpsertPhoneDeviceAsync(Guid userId, VerifyOtpDto dto)
    {
        var installId = string.IsNullOrWhiteSpace(dto.InstallationId)
            ? null
            : dto.InstallationId.Trim();
        var name = string.IsNullOrWhiteSpace(dto.DeviceName) ? "Iran phone" : dto.DeviceName.Trim();
        var ip = ClientIp();
        var platform = string.IsNullOrWhiteSpace(dto.Platform) ? "android" : dto.Platform.Trim();
        var model = string.IsNullOrWhiteSpace(dto.DeviceModel) ? null : dto.DeviceModel.Trim();
        var appVersion = string.IsNullOrWhiteSpace(dto.AppVersion) ? null : dto.AppVersion.Trim();
        if (!string.IsNullOrWhiteSpace(model) &&
            (string.IsNullOrWhiteSpace(dto.DeviceName) || dto.DeviceName.Trim() == "Iran phone"))
        {
            name = model;
        }

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
                LastIpAddress = ip,
                Platform = platform,
                DeviceModel = model,
                AppVersion = appVersion,
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
            device.LastIpAddress = ip;
            device.Platform = platform;
            if (model != null) device.DeviceModel = model;
            if (appVersion != null) device.AppVersion = appVersion;
            device.IsOnline = true;
            device.LastSeenAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        return device;
    }
}
