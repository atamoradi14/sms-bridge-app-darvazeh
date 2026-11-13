using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Mail;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace FieldSense.Api.Controllers;
public record PasswordSignupDto(string Email,string Password,string? Code=null,string? ConfirmPassword=null,string? Username=null);
public record PasswordSigninDto(string Identity,string Password,string? AdminPassword=null,string? Code=null);
public record PasswordResetDto(string Email,string Password,string Code,string? Username=null,string? AdminPassword=null,string? ConfirmPassword=null);
public record ProfilePasswordDto(string Password,string ConfirmPassword,string? CurrentPassword=null);
public record VerifyAccountEmailDto(string Code);
[ApiController, Route("api/auth/password"), EnableRateLimiting("password-auth")]
public class PasswordAuthController(AppDbContext db,EmailOtpService email,JwtTokenService jwt,MembershipService membership,IOptions<AppSettings> options) : ControllerBase
{
    private static readonly string DummyHash=BCrypt.Net.BCrypt.HashPassword("disposable-timing-normalizer",12);
    private static string Digest(string password)=>Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
    private static string Normalize(string? value)=>(value??"").Trim().ToLowerInvariant();
    private static bool ValidUsername(string username)=>Regex.IsMatch(username,"^[a-z0-9][a-z0-9._-]{2,29}$");
    private static bool ValidEmail(string value)=>value.Length<=254 && MailAddress.TryCreate(value,out var p) && p.Address==value && value.Contains('.');
    private bool AdminCredential(string address,string? password)=>address!=Normalize(options.Value.AdminEmail) ||
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password??""),Encoding.UTF8.GetBytes(options.Value.AdminPassword));
    private IActionResult Invalid()=>Unauthorized(new {error="نام کاربری، رمز یا کد ورود معتبر نیست.",code="invalid_credentials"});
    private async Task<IActionResult> SignedIn(AppUser user) {
        user.LastLoginAt=DateTime.UtcNow;user.FailedPasswordAttempts=0;user.PasswordLockedUntil=null;
        membership.Log(user.Id,"signed_in","password");await db.SaveChangesAsync();
        return Ok(new {token=jwt.CreateToken(user),user=new {user.Id,user.Email,user.Username,role=user.Role.ToString(),user.ForwardEmailEnabled,user.ForwardEmailAddress,subscription=await membership.Access(user.Id)}});
    }
    [HttpPost("signup"),EnableRateLimiting("signup")]
    public async Task<IActionResult> Signup(PasswordSignupDto dto) {
        var address=Normalize(dto.Email);var username=Normalize(dto.Username);
        var passwordError=PasswordPolicy.Error(dto.Password,dto.ConfirmPassword,address,username);
        if(passwordError!=null)return BadRequest(new {error="رمز باید ۹ تا ۱۲۸ نویسه، غیررایج و با تکرار آن یکسان باشد.",code=passwordError});
        if(!ValidEmail(address)||(username!=""&&!ValidUsername(username)))return BadRequest(new {error="ایمیل یا نام کاربری معتبر نیست.",code="invalid_signup"});
        if(address==Normalize(options.Value.AdminEmail) || await db.Users.AnyAsync(u=>u.DeletedAt==null && (u.Email==address || (username!="" && u.Username==username))))return Conflict(new {error="این حساب یا نام کاربری در دسترس نیست. وارد حساب شوید یا رمز را بازنشانی کنید.",code="account_unavailable"});
        await using var transaction=await db.Database.BeginTransactionAsync();
        var user=new AppUser {Email=address,Username=username==""?null:username,PasswordHash=BCrypt.Net.BCrypt.HashPassword(Digest(dto.Password),12),CanRelaySms=true,ForwardEmailAddress=address};
        db.Users.Add(user);await membership.StartTrial(user);
        try{await db.SaveChangesAsync();await transaction.CommitAsync();}
        catch(DbUpdateException){return Conflict(new {error="این حساب یا نام کاربری در دسترس نیست.",code="account_unavailable"});}
        return await SignedIn(user);
    }
    [HttpPost("signin")]
    public async Task<IActionResult> Signin(PasswordSigninDto dto) {
        var identity=Normalize(dto.Identity);
        if(identity.Length>254 || dto.Password==null || dto.Password.Length>256)return Invalid();
        var user=await db.Users.SingleOrDefaultAsync(u=>u.DeletedAt==null && (u.Email==identity || u.Username==identity));
        var now=DateTime.UtcNow;
        if(user?.PasswordLockedUntil>now)return Invalid();
        var matches=user==null?BCrypt.Net.BCrypt.Verify(Digest(dto.Password),DummyHash):PasswordCredentials.Matches(user,dto.Password,options.Value);
        if(user==null || user.DeletedAt!=null || user.SuspendedAt!=null)return Invalid();
        if(!matches) {
            await db.Users.Where(u=>u.Id==user.Id).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.FailedPasswordAttempts,x=>x.FailedPasswordAttempts+1));
            await db.Users.Where(u=>u.Id==user.Id && u.FailedPasswordAttempts>=5).ExecuteUpdateAsync(u=>u.SetProperty(x=>x.PasswordLockedUntil,now.AddMinutes(15)));
            return Invalid();
        }
        // The configured owner password is also usable during mail-provider outages.
        if(user.Role==UserRole.Admin) {
            if(await db.SecuritySettings.Where(s=>s.Id==1).Select(s=>s.AdminRequireOtp).SingleAsync()) {
            if(string.IsNullOrWhiteSpace(dto.Code))return Unauthorized(new {error="کد ورود مدیر و رمز مدیریت لازم است.",code="admin_otp_required"});
            if(!await email.VerifyOtpAsync(user.Email,dto.Code,true))return Invalid();
            }
            await membership.VerifyEmail(user);
        }
        return await SignedIn(user);
    }
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(PasswordResetDto dto) {
        var address=Normalize(dto.Email);var username=Normalize(dto.Username);
        var passwordError=PasswordPolicy.Error(dto.Password,dto.ConfirmPassword,address,username);
        if(passwordError!=null)return BadRequest(new {error="رمز باید ۹ تا ۱۲۸ نویسه، غیررایج و با تکرار آن یکسان باشد.",code=passwordError});
        if(!ValidEmail(address) || (username!=""&&!ValidUsername(username)))return BadRequest(new {error="ایمیل یا نام کاربری معتبر نیست.",code="invalid_password"});
        var user=await db.Users.SingleOrDefaultAsync(u=>u.DeletedAt==null && u.Email==address);
        if(user==null || user.DeletedAt!=null || user.SuspendedAt!=null || !await email.VerifyOtpAsync(address,dto.Code,AdminCredential(address,dto.AdminPassword)))return Invalid();
        if(username!="" && await db.Users.AnyAsync(u=>u.DeletedAt==null && u.Username==username && u.Id!=user.Id))return Conflict(new {error="نام کاربری در دسترس نیست.",code="username_unavailable"});
        await membership.VerifyEmail(user,independentLogin:true);
        if(username!="")user.Username=username;
        user.PasswordHash=BCrypt.Net.BCrypt.HashPassword(Digest(dto.Password),12);user.AuthVersion++;user.PasswordLockedUntil=null;user.FailedPasswordAttempts=0;
        membership.Log(user.Id,"password_updated");await db.SaveChangesAsync();
        return await SignedIn(user);
    }

    private Guid UserId=>Guid.Parse(User.FindFirstValue("sub")!);
    private bool SessionMatches(AppUser user)=>user.AuthVersion==(int.TryParse(User.FindFirstValue("auth_version"),out var version)?version:0);
    [Authorize,HttpPost("/api/user/verify-email/request")]
    public async Task<IActionResult> RequestVerification() {
        var user=await db.Users.SingleAsync(u=>u.Id==UserId);
        if(user.EmailVerifiedAt!=null)return Ok(new {alreadyVerified=true});
        var result=await email.SendLoginOtpAsync(user.Email);
        return result.ok?Ok(new {ok=true}):BadRequest(new {error=result.detail});
    }
    [Authorize,HttpPost("/api/user/verify-email")]
    public async Task<IActionResult> VerifyAccountEmail(VerifyAccountEmailDto dto) {
        await using var transaction=await db.Database.BeginTransactionAsync();
        var user=await db.Users.SingleAsync(u=>u.Id==UserId);
        if(!SessionMatches(user))return Unauthorized(new {code="session_expired"});
        if(user.EmailVerifiedAt==null && !await email.VerifyOtpAsync(user.Email,dto.Code))return Invalid();
        await membership.VerifyEmail(user);
        await transaction.CommitAsync();
        return Ok(new {ok=true});
    }
    [Authorize,HttpPost("/api/user/password")]
    public async Task<IActionResult> ProfilePassword(ProfilePasswordDto dto) {
        await using var transaction=await db.Database.BeginTransactionAsync();
        var user=await db.Users.SingleAsync(u=>u.Id==UserId);
        if(!SessionMatches(user))return Unauthorized(new {code="session_expired"});
        var problem=PasswordPolicy.Error(dto.Password,dto.ConfirmPassword,user.Email,user.Username);
        if(problem!=null)return BadRequest(new {code=problem});
        if(user.Role==UserRole.Admin && await db.SecuritySettings.Where(s=>s.Id==1).Select(s=>s.AdminRequireOtp).SingleAsync())return Unauthorized(new {code="admin_otp_required"});
        if(user.PasswordLockedUntil>DateTime.UtcNow)return Invalid();
        if(user.PasswordHash!=null || user.Role==UserRole.Admin) {
            if(dto.CurrentPassword==null || dto.CurrentPassword.Length>256 || !PasswordCredentials.Matches(user,dto.CurrentPassword,options.Value)) {
                user.FailedPasswordAttempts++;
                if(user.FailedPasswordAttempts>=5)user.PasswordLockedUntil=DateTime.UtcNow.AddMinutes(15);
                await db.SaveChangesAsync();await transaction.CommitAsync();return Invalid();
            }
        } else if(user.EmailVerifiedAt==null || !long.TryParse(User.FindFirstValue("auth_time"),out var at) || DateTimeOffset.UtcNow.ToUnixTimeSeconds()-at>900)
            return Unauthorized(new {code="recent_signin_required"});
        user.PasswordHash=BCrypt.Net.BCrypt.HashPassword(Digest(dto.Password),12);user.AuthVersion++;
        user.FailedPasswordAttempts=0;user.PasswordLockedUntil=null;
        membership.Log(user.Id,"password_updated");await db.SaveChangesAsync();await transaction.CommitAsync();
        return await SignedIn(user);
    }
}
