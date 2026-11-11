using Azure;
using Azure.Communication.Email;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldSense.Api.Services;

public class EmailOtpService(
    AppDbContext db,
    IOptions<AppSettings> options,
    ILogger<EmailOtpService> logger)
{
    private readonly AppSettings _settings = options.Value;

    public async Task<(bool ok, string detail, string? debugCode)> SendLoginOtpAsync(string email)
    {
        email = email.Trim().ToLowerInvariant();
        if (!email.Contains('@')) return (false, "Invalid email", null);

        var code = Random.Shared.Next(100000, 999999).ToString();
        var challenge = new OtpChallenge
        {
            Email = email,
            CodeHash = BCrypt.Net.BCrypt.HashPassword(code),
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };
        db.OtpChallenges.Add(challenge);
        await db.SaveChangesAsync();

        var subject = "FieldSense login code";
        var body = $"Your FieldSense login code is {code}. It expires in 10 minutes.";

        if (_settings.AcsMock || string.IsNullOrWhiteSpace(_settings.AcsConnectionString))
        {
            logger.LogWarning("ACS mock OTP for {Email}: {Code}", email, code);
            return (true, "OTP sent (mock — check server logs)", code);
        }

        try
        {
            var client = new EmailClient(_settings.AcsConnectionString);
            var message = new EmailMessage(
                senderAddress: _settings.AcsSenderAddress,
                content: new EmailContent(subject) { PlainText = body, Html = $"<p>{body}</p>" },
                recipients: new EmailRecipients(new[] { new EmailAddress(email) }));
            await client.SendAsync(WaitUntil.Started, message);
            return (true, "OTP sent via Azure ACS", null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ACS send failed");
            return (false, "Failed to send email via ACS", null);
        }
    }

    public async Task<bool> VerifyOtpAsync(string email, string code)
    {
        email = email.Trim().ToLowerInvariant();
        code = code.Trim();
        // Dev / mock ACS: always accept 000000 after a request-otp for that email.
        if ((_settings.AcsMock || string.IsNullOrWhiteSpace(_settings.AcsConnectionString)) &&
            code == "000000")
        {
            var any = await db.OtpChallenges.AnyAsync(c =>
                c.Email == email && !c.Consumed && c.ExpiresAt > DateTime.UtcNow);
            if (any)
            {
                var open = await db.OtpChallenges
                    .Where(c => c.Email == email && !c.Consumed && c.ExpiresAt > DateTime.UtcNow)
                    .ToListAsync();
                foreach (var c in open) c.Consumed = true;
                await db.SaveChangesAsync();
                return true;
            }
        }

        var now = DateTime.UtcNow;
        var challenges = await db.OtpChallenges
            .Where(c => c.Email == email && !c.Consumed && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt)
            .Take(5)
            .ToListAsync();

        foreach (var c in challenges)
        {
            if (!BCrypt.Net.BCrypt.Verify(code, c.CodeHash)) continue;
            c.Consumed = true;
            await db.SaveChangesAsync();
            return true;
        }
        return false;
    }

    public async Task ForwardSmsEmailAsync(AppUser user, string from, string body)
    {
        if (!user.ForwardEmailEnabled) return;
        var to = string.IsNullOrWhiteSpace(user.ForwardEmailAddress) ? user.Email : user.ForwardEmailAddress!;
        var subject = $"SMS from {from}";
        var text = $"From: {from}\n\n{body}";

        if (_settings.AcsMock || string.IsNullOrWhiteSpace(_settings.AcsConnectionString))
        {
            logger.LogInformation("Mock forward SMS email to {To}: {Subject}", to, subject);
            return;
        }

        var client = new EmailClient(_settings.AcsConnectionString);
        var message = new EmailMessage(
            senderAddress: _settings.AcsSenderAddress,
            content: new EmailContent(subject) { PlainText = text },
            recipients: new EmailRecipients(new[] { new EmailAddress(to) }));
        await client.SendAsync(WaitUntil.Started, message);
    }
}
