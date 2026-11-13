using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Net.Mail;
namespace FieldSense.Api.Controllers;
public record AdminSecurityDto(bool AdminRequireOtp,string Password);
public record AdminAccountDto(string Email,string? Password=null,string? ConfirmPassword=null,string? Username=null);
public record SuspendAccountDto(bool Suspended);
public record DeviceConnectionDto(bool Connected);
[ApiController,Authorize(Policy="Owner"),Route("api/admin")]
public class AdminAccountsController(AppDbContext db,MembershipService membership,IOptions<AppSettings> options):ControllerBase {
    private Guid Actor=>Guid.Parse(User.FindFirstValue("sub")!);
    private static bool ValidEmail(string email)=>email.Length<=254&&MailAddress.TryCreate(email,out var p)&&p.Address==email&&email.Contains('.');
    private static bool ValidUsername(string? name)=>string.IsNullOrEmpty(name)||System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-z0-9][a-z0-9._-]{2,29}$");
    [HttpGet("security")]
    public async Task<IActionResult> Security()=>Ok(await db.SecuritySettings.AsNoTracking().SingleAsync(s=>s.Id==1));
    [HttpPut("security"),Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("password-auth")]
    public async Task<IActionResult> Security(AdminSecurityDto dto) {
        var actor=await db.Users.SingleAsync(u=>u.Id==Actor);
        if(actor.PasswordLockedUntil>DateTime.UtcNow || !PasswordCredentials.Matches(actor,dto.Password,options.Value)) {
            actor.FailedPasswordAttempts++;if(actor.FailedPasswordAttempts>=5)actor.PasswordLockedUntil=DateTime.UtcNow.AddMinutes(15);
            await db.SaveChangesAsync();return Unauthorized(new {code="invalid_credentials"});
        }
        var setting=await db.SecuritySettings.SingleAsync(s=>s.Id==1);setting.AdminRequireOtp=dto.AdminRequireOtp;
        actor.FailedPasswordAttempts=0;actor.PasswordLockedUntil=null;
        membership.Log(Actor,"admin_login_policy_updated",dto.AdminRequireOtp?"OTP required":"Password login",Actor);
        await db.SaveChangesAsync();return Ok(new {setting.AdminRequireOtp});
    }
    [HttpPost("users")]
    public async Task<IActionResult> Create(AdminAccountDto dto) {
        var email=(dto.Email??"").Trim().ToLowerInvariant();var username=dto.Username?.Trim().ToLowerInvariant();
        if(!ValidEmail(email)||!ValidUsername(username))return BadRequest(new {code="invalid_account"});
        var problem=PasswordPolicy.Error(dto.Password,dto.ConfirmPassword,email,username);if(problem!=null)return BadRequest(new {code=problem});
        if(email==options.Value.AdminEmail.Trim().ToLowerInvariant() || await db.Users.AnyAsync(u=>u.DeletedAt==null && (u.Email==email || (username!=null && username!="" && u.Username==username))))return Conflict(new {code="account_unavailable"});
        await using var transaction=await db.Database.BeginTransactionAsync();
        var user=new AppUser {Email=email,Username=string.IsNullOrEmpty(username)?null:username,PasswordHash=PasswordCredentials.Hash(dto.Password!),ForwardEmailAddress=email,CanRelaySms=true};
        db.Users.Add(user);await membership.StartTrial(user);membership.Log(user.Id,"account_created_by_admin","Awaiting email verification",Actor);
        try{await db.SaveChangesAsync();await transaction.CommitAsync();}catch(DbUpdateException){return Conflict(new {code="account_unavailable"});}
        return Ok(new {user.Id,user.Email});
    }
    [HttpPut("users/{id:guid}")]
    public async Task<IActionResult> Modify(Guid id,AdminAccountDto dto) {
        await using var transaction=await db.Database.BeginTransactionAsync();
        var user=await db.Users.SingleOrDefaultAsync(u=>u.Id==id && u.DeletedAt==null);if(user==null)return NotFound();
        if(user.Role==UserRole.Admin)return StatusCode(403,new {code="protected_owner"});
        var email=(dto.Email??"").Trim().ToLowerInvariant();var username=dto.Username?.Trim().ToLowerInvariant();
        if(!ValidEmail(email)||!ValidUsername(username))return BadRequest(new {code="invalid_account"});
        if(await db.Users.AnyAsync(u=>u.DeletedAt==null && u.Id!=id && (u.Email==email || (username!=null && username!="" && u.Username==username))))return Conflict(new {code="account_unavailable"});
        if(!string.IsNullOrEmpty(dto.Password)) {
            var problem=PasswordPolicy.Error(dto.Password,dto.ConfirmPassword,email,username);if(problem!=null)return BadRequest(new {code=problem});
            user.PasswordHash=PasswordCredentials.Hash(dto.Password);user.FailedPasswordAttempts=0;user.PasswordLockedUntil=null;
        }
        if(user.Email!=email) {
            await db.OtpChallenges.Where(c=>c.Email==user.Email).ExecuteUpdateAsync(u=>u.SetProperty(c=>c.Consumed,true));
            await db.DeviceLinks.Where(r=>r.Email==user.Email).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Status,"revoked"));
            user.Email=email;user.EmailVerifiedAt=null;user.GoogleSubject=null;user.ForwardEmailEnabled=false;user.ForwardEmailAddress=email;
            foreach(var d in await db.Devices.Where(d=>d.UserId==id).ToListAsync()){d.RevokedAt=DateTime.UtcNow;d.IsOnline=false;d.DeviceToken=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");}
            await CancelJobs(id,null);
        }
        if(username!=null)user.Username=username==""?null:username;
        user.AuthVersion++;membership.Log(id,"account_modified","Profile updated",Actor);
        try{await db.SaveChangesAsync();await transaction.CommitAsync();}catch(DbUpdateException){return Conflict(new {code="account_unavailable"});}
        return Ok(new {ok=true});
    }
    private async Task CancelJobs(Guid userId,Guid? deviceId) {
        var jobs=db.OutboundJobs.Where(j=>j.UserId==userId && j.Status=="queued");if(deviceId!=null)jobs=jobs.Where(j=>j.DeviceId==deviceId);
        var ids=await jobs.Select(j=>j.SmsMessageId).ToListAsync();
        await jobs.ExecuteUpdateAsync(u=>u.SetProperty(j=>j.Status,"cancelled").SetProperty(j=>j.CompletedAt,DateTime.UtcNow));
        await db.Messages.Where(m=>ids.Contains(m.Id)).ExecuteUpdateAsync(u=>u.SetProperty(m=>m.Status,"cancelled"));
    }
    [HttpPut("users/{id:guid}/suspension")]
    public async Task<IActionResult> Suspend(Guid id,SuspendAccountDto dto) {
        await using var transaction=await db.Database.BeginTransactionAsync();
        var user=await db.Users.SingleOrDefaultAsync(u=>u.Id==id && u.DeletedAt==null);if(user==null)return NotFound();
        if(user.Role==UserRole.Admin)return StatusCode(403,new {code="protected_owner"});
        if(dto.Suspended!=(user.SuspendedAt!=null)){user.SuspendedAt=dto.Suspended?DateTime.UtcNow:null;user.AuthVersion++;}
        if(dto.Suspended){await CancelJobs(id,null);await db.Devices.Where(d=>d.UserId==id).ExecuteUpdateAsync(u=>u.SetProperty(d=>d.IsOnline,false));}
        membership.Log(id,dto.Suspended?"account_suspended":"account_resumed","",Actor);
        await db.SaveChangesAsync();await transaction.CommitAsync();return Ok(new {ok=true});
    }
    [HttpDelete("users/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id) {
        await using var transaction=await db.Database.BeginTransactionAsync();
        var user=await db.Users.SingleOrDefaultAsync(u=>u.Id==id && u.DeletedAt==null);if(user==null)return NotFound();
        if(user.Role==UserRole.Admin)return StatusCode(403,new {code="protected_owner"});
        user.DeletedAt=DateTime.UtcNow;user.SuspendedAt=user.DeletedAt;user.AuthVersion++;user.PasswordHash=null;user.GoogleSubject=null;user.ForwardEmailEnabled=false;
        await CancelJobs(id,null);
        foreach(var d in await db.Devices.Where(d=>d.UserId==id).ToListAsync()){d.RevokedAt=DateTime.UtcNow;d.IsOnline=false;d.DeviceToken=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");}
        await db.DeviceLinks.Where(r=>r.Email==user.Email).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.Status,"revoked"));
        await db.OtpChallenges.Where(c=>c.Email==user.Email).ExecuteUpdateAsync(u=>u.SetProperty(c=>c.Consumed,true));
        await db.Messages.Where(m=>m.UserId==id).ExecuteUpdateAsync(u=>u.SetProperty(m=>m.DeletedAt,DateTime.UtcNow).SetProperty(m=>m.Body,"").SetProperty(m=>m.PeerNumber,""));
        await db.OutboundJobs.Where(j=>j.UserId==id).ExecuteUpdateAsync(u=>u.SetProperty(j=>j.Body,"").SetProperty(j=>j.ToNumber,"").SetProperty(j=>j.Error,(string?)null));
        await db.EmailDispatches.Where(e=>e.UserId==id).ExecuteUpdateAsync(u=>u.SetProperty(e=>e.ProtectedPayload,"").SetProperty(e=>e.Status,"cancelled"));
        membership.Log(id,"account_deleted","Account closed; billing/audit and quota records retained",Actor);
        await db.SaveChangesAsync();await transaction.CommitAsync();return Ok(new {ok=true});
    }
    [HttpGet("users/{id:guid}/devices")]
    public async Task<IActionResult> Devices(Guid id)=>Ok(await db.Devices.AsNoTracking().Where(d=>d.UserId==id).Select(d=>new {d.Id,d.Name,d.LastSeenAt,d.BatteryLevel,d.NetworkType,d.RevokedAt,d.AdminPausedAt}).ToListAsync());
    [HttpPut("users/{id:guid}/devices/{deviceId:guid}/connection")]
    public async Task<IActionResult> Connect(Guid id,Guid deviceId,DeviceConnectionDto dto) {
        await using var transaction=await db.Database.BeginTransactionAsync();
        var device=await db.Devices.Include(d=>d.User).SingleOrDefaultAsync(d=>d.Id==deviceId && d.UserId==id);if(device==null)return NotFound();
        if(device.User.DeletedAt!=null || (dto.Connected && (device.RevokedAt!=null || device.User.SuspendedAt!=null || device.User.EmailVerifiedAt==null)))return Conflict(new {code="approval_required"});
        device.AdminPausedAt=dto.Connected?null:DateTime.UtcNow;device.IsOnline=false;
        if(!dto.Connected)await CancelJobs(id,deviceId);
        membership.Log(id,dto.Connected?"device_resumed":"device_paused",device.Name,Actor);
        await db.SaveChangesAsync();await transaction.CommitAsync();return Ok(new {ok=true});
    }
    [HttpGet("users/{id:guid}/messages/page")]
    public async Task<IActionResult> Messages(Guid id,string? search=null,string? direction=null,int page=1,int pageSize=20)=>Ok(await MessageHistory.Page(db,id,search,direction,page,pageSize));
    [HttpPost("users/{id:guid}/messages/delete")]
    public async Task<IActionResult> DeleteMessages(Guid id,DeleteMessagesDto dto) {
        if(dto.Ids==null)return BadRequest();
        await using var transaction=await db.Database.BeginTransactionAsync();var problem=await MessageHistory.Delete(db,id,dto.Ids);
        if(problem!=null)return StatusCode(problem=="not_found"?404:409,new {code=problem});
        membership.Log(id,"messages_deleted",dto.Ids.Length.ToString(),Actor);await db.SaveChangesAsync();await transaction.CommitAsync();return Ok(new {ok=true});
    }
}
