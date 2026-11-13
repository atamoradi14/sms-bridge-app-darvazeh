using System.Security.Cryptography;
using System.Text;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
namespace FieldSense.Api.Controllers;
public record GoogleExchangeDto(Guid FlowId,string Code);
[ApiController,Route("api/auth/google"),EnableRateLimiting("password-auth"),ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public class GoogleAuthController(AppDbContext db,IGoogleIdentityProvider google,IOptions<GoogleSettings> options,IOptions<AppSettings> app,
    JwtTokenService jwt,MembershipService membership,ILogger<GoogleAuthController> logger) : ControllerBase {
    private GoogleSettings Settings=>options.Value;
    private static string Secret()=>Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string input)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    private static string Cookie(Guid id)=>"darvazeh.google."+id.ToString("N");
    private bool BrowserMatches(GoogleLoginFlow flow)=>Request.Cookies.TryGetValue(Cookie(flow.Id),out var secret) && secret.Length==43 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)),Encoding.ASCII.GetBytes(flow.BrowserSecretHash));
    private CookieOptions CookieOptions()=>new(){HttpOnly=true,Secure=!HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment(),SameSite=SameSiteMode.Lax,Path="/api/auth/google",MaxAge=TimeSpan.FromMinutes(10),IsEssential=true};
    private IActionResult Failure(string code) { logger.LogWarning("Google sign-in declined: {Reason}",code);return Redirect(Settings.PortalOrigin.TrimEnd('/')+"/#google_error="+Uri.EscapeDataString(code)); }
    [HttpGet("options")]
    public IActionResult Options()=>Ok(new {googleEnabled=Settings.Configured});
    [HttpGet("start")]
    public async Task<IActionResult> Start() {
        if(!Settings.Configured)return StatusCode(503,new {error="ورود گوگل فعلاً در دسترس نیست.",code="google_not_configured"});
        var state=Secret();var browser=Secret();var verifier=Secret();
        var flow=new GoogleLoginFlow {StateHash=Hash(state),BrowserSecretHash=Hash(browser),Nonce=Secret(),PkceVerifier=verifier};
        // Keep expired correlation material bounded without touching active flows.
        await db.GoogleLoginFlows.Where(f=>f.ExpiresAt<DateTime.UtcNow.AddDays(-1)).ExecuteDeleteAsync();
        db.GoogleLoginFlows.Add(flow);await db.SaveChangesAsync();Response.Cookies.Append(Cookie(flow.Id),browser,CookieOptions());
        Response.Headers["Referrer-Policy"]="no-referrer";
        return Redirect(google.AuthorizationUrl(flow.Id.ToString("N")+"."+state,flow.Nonce,Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))));
    }
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string? state,string? code,string? error,CancellationToken ct) {
        if(!Settings.Configured)return Failure("unavailable");
        var parts=(state??"").Split('.');
        if(parts.Length!=2 || parts[1].Length!=43 || !Guid.TryParseExact(parts[0],"N",out var id))return Failure("invalid_state");
        var flow=await db.GoogleLoginFlows.SingleOrDefaultAsync(f=>f.Id==id,ct);
        if(flow==null || flow.Consumed || flow.ExpiresAt<=DateTime.UtcNow || !BrowserMatches(flow) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(parts[1])),Encoding.ASCII.GetBytes(flow.StateHash)))return Failure("invalid_state");
        if(await db.GoogleLoginFlows.Where(f=>f.Id==id && !f.Consumed && f.ExpiresAt>DateTime.UtcNow)
            .ExecuteUpdateAsync(u=>u.SetProperty(f=>f.Consumed,true),ct)!=1)return Failure("invalid_state");
        if(error!=null)return Failure("cancelled");
        if(string.IsNullOrEmpty(code) || code.Length>4096)return Failure("invalid_response");
        try {
            var identity=await google.Exchange(code,flow.PkceVerifier,flow.Nonce,ct);
            if(!identity.Authoritative)return Failure("verify_email");
            if(string.Equals(identity.Email,app.Value.AdminEmail.Trim(),StringComparison.OrdinalIgnoreCase))return Failure("admin_password_required");
            await using var transaction=await db.Database.BeginTransactionAsync(ct);
            var user=await db.Users.SingleOrDefaultAsync(u=>u.DeletedAt==null && u.GoogleSubject==identity.Subject,ct);
            if(user==null)user=await db.Users.SingleOrDefaultAsync(u=>u.DeletedAt==null && u.Email==identity.Email,ct);
            if(user?.SuspendedAt!=null)return Failure("account_suspended");
            if(user?.Role==UserRole.Admin)return Failure("admin_password_required");
            if(user!=null && ((user.GoogleSubject!=null && user.GoogleSubject!=identity.Subject) || user.Email!=identity.Email))return Failure("account_conflict");
            if(user==null) {
                user=new AppUser {Email=identity.Email,GoogleSubject=identity.Subject,CanRelaySms=true,ForwardEmailAddress=identity.Email};
                db.Users.Add(user);
            }else user.GoogleSubject=identity.Subject;
            await membership.VerifyEmail(user,independentLogin:true);
            user.LastLoginAt=DateTime.UtcNow;membership.Log(user.Id,"signed_in","google");
            var result=Secret();flow.UserId=user.Id;flow.AuthVersion=user.AuthVersion;flow.ResultCodeHash=Hash(result);flow.ResultExpiresAt=DateTime.UtcNow.AddMinutes(1);
            flow.Consumed=true;flow.PkceVerifier="";flow.Nonce="";
            await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
            Response.Headers["Referrer-Policy"]="no-referrer";
            // A one-use, browser-bound code crosses the redirect; never place an app JWT in a URL.
            return Redirect(Settings.PortalOrigin.TrimEnd('/')+"/#google_flow="+flow.Id+"&google_code="+result);
        }catch(Exception e) when(e is SecurityTokenException or HttpRequestException or DbUpdateException or System.Text.Json.JsonException or TaskCanceledException) {
            logger.LogWarning("Google sign-in failed: {Type}",e.GetType().Name);return Failure("signin_failed");
        }
    }
    [HttpPost("exchange")]
    public async Task<IActionResult> Exchange(GoogleExchangeDto dto) {
        if(!Settings.Configured)return StatusCode(503,new {code="google_not_configured"});
        var flow=await db.GoogleLoginFlows.AsNoTracking().SingleOrDefaultAsync(f=>f.Id==dto.FlowId);
        if(flow==null || dto.Code?.Length!=43 || !flow.Consumed || flow.ResultCodeHash==null || flow.ResultExpiresAt==null || flow.ResultExpiresAt<=DateTime.UtcNow || !BrowserMatches(flow) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(dto.Code)),Encoding.ASCII.GetBytes(flow.ResultCodeHash)))return Unauthorized(new {code="invalid_google_exchange"});
        var user=await db.Users.SingleOrDefaultAsync(u=>u.Id==flow.UserId && u.AuthVersion==flow.AuthVersion && u.Role!=UserRole.Admin && u.DeletedAt==null && u.SuspendedAt==null);
        if(user==null)return Unauthorized(new {code="invalid_google_exchange"});
        if(await db.GoogleLoginFlows.Where(f=>f.Id==dto.FlowId && f.ResultCodeHash==flow.ResultCodeHash && f.ResultExpiresAt>DateTime.UtcNow)
            .ExecuteUpdateAsync(u=>u.SetProperty(f=>f.ResultCodeHash,(string?)null))!=1)return Unauthorized(new {code="invalid_google_exchange"});
        Response.Cookies.Delete(Cookie(flow.Id),CookieOptions());
        return Ok(new {token=jwt.CreateToken(user)});
    }
}
