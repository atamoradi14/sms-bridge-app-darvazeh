using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldSense.Api.Controllers;

public record RequestOtpDto(string Email);
public record VerifyOtpDto(string Email, string Code, string? AdminPassword);

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
            // Only returned when ACS is mocked for local/dev.
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
            }
        });
    }
}
