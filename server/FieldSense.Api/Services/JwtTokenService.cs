using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FieldSense.Api.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FieldSense.Api.Services;

public class JwtTokenService(IOptions<AppSettings> options)
{
    private readonly AppSettings _settings = options.Value;

    public string CreateToken(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("auth_version", user.AuthVersion.ToString()),
            new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("can_relay", user.CanRelaySms ? "1" : "0")
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "fieldsense",
            audience: "fieldsense",
            claims: claims,
            expires: DateTime.UtcNow.AddDays(14),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
