using System.Security.Cryptography;
using System.Text;
using FieldSense.Api.Models;
namespace FieldSense.Api.Services;
public static class PasswordCredentials {
    private static string Digest(string value)=>Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Hash(string value)=>BCrypt.Net.BCrypt.HashPassword(Digest(value),12);
    public static bool Matches(AppUser user,string? password,AppSettings settings) {
        if(password==null || password.Length>256)return false;
        var stored=user.PasswordHash!=null && BCrypt.Net.BCrypt.Verify(Digest(password),user.PasswordHash);
        return stored || (user.Role==UserRole.Admin && user.Email.Equals(settings.AdminEmail.Trim(),StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.AdminPassword) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password),Encoding.UTF8.GetBytes(settings.AdminPassword)));
    }
}
