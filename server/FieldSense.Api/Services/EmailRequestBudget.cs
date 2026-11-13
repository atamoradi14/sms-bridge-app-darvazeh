using System.Globalization;
using Azure;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Services;

// Durable rolling windows shared by both queues and preserved across restarts.
// Headroom below ACS custom-domain defaults: sends 30/min,100/h; status 60/min,200/h.
public class EmailRequestBudget(AppDbContext db, TimeProvider clock)
{
    public async Task<DateTime?> Reserve(string operation, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Take the SQLite write lock before reading a budget, including across instances.
        await db.Database.ExecuteSqlRawAsync("UPDATE EmailProviderControls SET Id=Id WHERE Id=1", ct);
        var control = await db.EmailProviderControls.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        DateTime? due = control.CooldownUntil > now ? control.CooldownUntil : null;
        var cutoff = now.AddHours(-1);
        await db.EmailProviderCalls.Where(x => x.CreatedAt <= cutoff).ExecuteDeleteAsync(ct);
        var calls = await db.EmailProviderCalls.AsNoTracking().Where(x => x.Operation == operation)
            .OrderBy(x => x.CreatedAt).Select(x => x.CreatedAt).ToListAsync(ct);
        var minute = calls.Where(x => x > now.AddMinutes(-1)).ToList();
        void After(DateTime value) { if (value > now && (due == null || value > due)) due = value; }
        if (calls.Count != 0) After(calls[^1].AddSeconds(2));
        var perMinute = operation == "send" ? 25 : 50;
        var perHour = operation == "send" ? 90 : 180;
        if (minute.Count >= perMinute) After(minute[minute.Count - perMinute].AddMinutes(1));
        if (calls.Count >= perHour) After(calls[calls.Count - perHour].AddHours(1));
        if (due == null) {
            db.EmailProviderCalls.Add(new EmailProviderCall { Operation = operation, CreatedAt = now });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return due;
    }

    public Task Cooldown(DateTime until, CancellationToken ct) => db.EmailProviderControls
        .Where(x => x.Id == 1 && x.CooldownUntil < until)
        .ExecuteUpdateAsync(u => u.SetProperty(x => x.CooldownUntil, until), ct);

    public static TimeSpan? RetryAfter(Response? response, DateTime now)
    {
        if (response == null) return null;
        if (response.Headers.TryGetValue("retry-after-ms", out var ms) && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0)
            return TimeSpan.FromMilliseconds(Math.Min(n, TimeSpan.MaxValue.TotalMilliseconds / 2));
        if (!response.Headers.TryGetValue("Retry-After", out var raw)) return null;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && double.IsFinite(n) && n >= 0)
            return TimeSpan.FromSeconds(Math.Min(n, TimeSpan.MaxValue.TotalSeconds / 2));
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? TimeSpan.FromSeconds(Math.Max(0, (date.UtcDateTime - now).TotalSeconds)) : null;
    }

    public static TimeSpan PollDelay(int polls, TimeSpan? hint = null) => TimeSpan.FromSeconds(
        Math.Max(hint?.TotalSeconds ?? 0, Math.Min(120, 15 * Math.Pow(2, Math.Min(3, Math.Max(0, polls - 1)))) + Random.Shared.NextDouble() * 3));
    public static TimeSpan FailureDelay(int failures, TimeSpan? hint = null) => TimeSpan.FromSeconds(
        Math.Max(hint?.TotalSeconds ?? 0, Math.Min(300, 15 * Math.Pow(2, Math.Min(5, Math.Max(0, failures - 1))))) + Random.Shared.NextDouble() * 5);
}
