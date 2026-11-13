using System.Net;
using System.Security.Cryptography;
using Azure;
using Azure.Communication.Email;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldSense.Api.Services;
public class EmailOtpService(AppDbContext db, IOptions<AppSettings> options, EmailOutbox outbox)
{
    private readonly AppSettings _settings = options.Value;
    public static string LoginHtml(string code) => $$"""
        <!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0;background:#f5f0e7;font-family:Tahoma,Arial,sans-serif;color:#263e3a">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:36px 16px">
        <table role="presentation" width="100%" style="max-width:480px;background:#fffdf8;border:1px solid #e5dac7;border-radius:16px" cellpadding="0" cellspacing="0">
        <tr><td style="padding:28px 32px;border-bottom:1px solid #e5dac7"><div style="color:#a47a3d;font-size:13px;letter-spacing:3px" dir="ltr">DARVAZEH</div><h1 style="font-size:28px;margin:10px 0 0;color:#174c44">دروازه</h1></td></tr>
        <tr><td style="padding:30px 32px"><h2 style="font-size:20px;margin:0 0 14px">کد ورود شما</h2>
        <p style="font-size:15px;line-height:1.9">برای ورود به دروازه، کد زیر را در برنامه وارد کنید.</p>
        <div dir="ltr" style="background:#f5f0e7;border:1px solid #d9c5a2;border-radius:12px;padding:22px 8px;text-align:center;font-size:36px;font-weight:bold;letter-spacing:10px;color:#174c44">{{WebUtility.HtmlEncode(code)}}</div>
        <p style="font-size:13px;line-height:1.9;color:#68766e">این کد تا ۱۰ دقیقه معتبر است. آن را با دیگران به اشتراک نگذارید.</p>
        <div dir="ltr" style="border-top:1px solid #e5dac7;margin-top:24px;padding-top:20px;text-align:left;color:#68766e;font-size:13px;line-height:1.7"><strong style="color:#263e3a">Your Darvazeh sign-in code</strong><br>Enter the code above to sign in. It expires in 10 minutes.<br>If you didn’t request this, you can ignore this email.</div>
        </td></tr></table><p style="font-size:12px;color:#68766e">دروازه ای برای ارتباطات شما</p></td></tr></table></body></html>
        """;
    public async Task<(bool ok, string detail, string? debugCode)> SendLoginOtpAsync(string email)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$") || email.Length > 254)
            return (false, "نشانی ایمیل را بررسی کنید.", null);
        var since = DateTime.UtcNow.AddMinutes(-15);
        if (await db.OtpChallenges.CountAsync(c => c.Email == email && c.CreatedAt > since) >= 5)
            return (false, "چند دقیقه دیگر دوباره درخواست کد بدهید.", null);
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var challenge = new OtpChallenge { Email = email, CodeHash = BCrypt.Net.BCrypt.HashPassword(code), ExpiresAt = DateTime.UtcNow.AddMinutes(10) };
        db.OtpChallenges.Add(challenge); await db.SaveChangesAsync();
        if (_settings.AcsMock)
            return (true, "کد ورود آماده است.", code);
        if (string.IsNullOrWhiteSpace(_settings.AcsConnectionString))
            return (false, "ارسال کد موقتاً در دسترس نیست. کمی بعد دوباره تلاش کنید.", null);
        outbox.Enqueue("otp",new MailContent(email,"Darvazeh | کد ورود شما",
            $"Darvazeh · دروازه\n\nYour sign-in code / کد ورود: {code}\nExpires in 10 minutes.",LoginHtml(code)),Guid.Empty,challenge:challenge);
        await db.SaveChangesAsync();
        return (true,"کد ورود در صف ارسال است.",null);
    }
    public async Task<bool> VerifyOtpAsync(string email, string code, bool additionalCredentialValid = true)
    {
        email = email.Trim().ToLowerInvariant(); code = (code ?? "").Trim();
        var now = DateTime.UtcNow;
        var challenge = await db.OtpChallenges.AsNoTracking().Where(c => c.Email == email && !c.Consumed && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync();
        if (challenge == null || challenge.Attempts >= 5) return false;
        var attempted = await db.OtpChallenges.Where(c => c.Id == challenge.Id && !c.Consumed && c.Attempts < 5 && c.ExpiresAt > now)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Attempts, c => c.Attempts + 1));
        if (attempted != 1) return false;
        var matches = (_settings.AcsMock && code == "000000") || BCrypt.Net.BCrypt.Verify(code, challenge.CodeHash);
        if (!matches || !additionalCredentialValid) return false;
        // Conditional consumption makes a code single-use even under concurrent verifies.
        var changed = await db.OtpChallenges.Where(c => c.Id == challenge.Id && !c.Consumed)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Consumed, true));
        return changed == 1;
    }
    public void QueueSmsEmail(AppUser user,SmsMessage message,Guid subscriptionId) {
        if(!user.ForwardEmailEnabled)return;
        if(_settings.AcsMock){message.EmailForwarded=true;message.EmailForwardStatus="submitted";return;}
        var to=string.IsNullOrWhiteSpace(user.ForwardEmailAddress)?user.Email:user.ForwardEmailAddress!;
        var health=$"Battery / باتری: {message.BatteryLevel?.ToString()??"—"}% · {(message.IsCharging==true?"Charging / در حال شارژ":"")} · {message.NetworkType??"—"}";
        var text=$"Darvazeh · دروازه\n\n{message.PeerNumber}\n\n{message.Body}\n\n{health}";
        var html=$"<div dir='rtl' style='font-family:Tahoma;line-height:1.9'><h2>دروازه</h2><p>{WebUtility.HtmlEncode(message.PeerNumber)}</p><p style='white-space:pre-wrap'>{WebUtility.HtmlEncode(message.Body)}</p><hr><p>{WebUtility.HtmlEncode(health)}</p></div>";
        outbox.Enqueue("forward",new MailContent(to,"Darvazeh | پیام تازه",text,html),user.Id,message.Id,subscriptionId);
        message.EmailForwardStatus="queued";
    }
}
