using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldSense.Api.Services;

public class VpnException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public record VpnView(Guid Id, string Status, string ServerCode, string ServerName, string ServerNameFa, string? SubscriptionUrl,
    bool TrialGranted, DateTime? PaidUntil, DateTime? PeriodStart, DateTime? PeriodEnd, long LimitBytes, long? LimitOverrideBytes,
    long UsedUp, long UsedDown, long UsedTotal, long RemainingBytes, long LifetimeUp, long LifetimeDown,
    bool Online, int ActiveIps, int IpLimit, DateTime? LastOnlineAt, bool PanelEnabled, DateTime? SyncedAt,
    DateTime? SuspendedAt, string? SuspendedBy, string? SuspendReason, bool Pending, string? LastError, DateTime CreatedAt, DateTime? DeletedAt);

/// <summary>
/// Sells and manages one-user VPN access on the configured 3x-ui servers. Darvazeh is the source of truth for
/// paid periods, quota and suspension; each change is written to the panel, which enforces quota, expiry and IP limit.
/// Panel writes happen outside database transactions and are retried by <see cref="VpnWorker"/>.
/// </summary>
public class VpnService(AppDbContext db, XuiClient xui, IOptions<VpnSettings> options, MembershipService membership, ILogger<VpnService> logger)
{
    public const string PlanCode = "vpn-iran";
    private static readonly SemaphoreSlim PanelLock = new(1, 1);
    private VpnSettings Settings => options.Value;

    public async Task<VpnPolicy> Policy() => await db.VpnPolicies.SingleOrDefaultAsync(p => p.Id == 1) ?? new VpnPolicy();
    public Task<MembershipPlan?> Plan() => db.Plans.SingleOrDefaultAsync(p => p.Code == PlanCode);

    public static string Status(VpnClient c, DateTime now, bool userBlocked = false)
    {
        if (c.DeletedAt != null) return "revoked";
        if (c.SuspendedAt != null || userBlocked) return "suspended";
        var used = c.UsedUp + c.UsedDown;
        if (c.PaidUntil > now) return used >= c.LimitBytes ? "depleted" : "active";
        if (c.PaidUntil != null) return "expired";
        if (!c.TrialGranted || c.LimitBytes <= 0) return "awaiting_payment";
        if (c.PeriodEnd != null && c.PeriodEnd <= now) return "trial_ended";
        return used >= c.LimitBytes ? "trial_used" : "trial";
    }

    public VpnView View(VpnClient c, VpnPolicy policy, bool userBlocked = false)
    {
        var server = Settings.Servers.FirstOrDefault(s => s.Code == c.ServerCode);
        var used = c.UsedUp + c.UsedDown;
        return new(c.Id, Status(c, DateTime.UtcNow, userBlocked), c.ServerCode, server?.Name ?? c.ServerCode, server?.NameFa ?? server?.Name ?? c.ServerCode,
            c.DeletedAt == null && server != null ? server.SubscriptionUrl(c.SubId) : null,
            c.TrialGranted, c.PaidUntil, c.PeriodStart, c.PeriodEnd, c.LimitBytes, c.LimitOverrideBytes,
            c.UsedUp, c.UsedDown, used, Math.Max(0, c.LimitBytes - used), c.LifetimeUp + c.UsedUp, c.LifetimeDown + c.UsedDown,
            c.Online, c.ActiveIps, policy.IpLimit, c.LastOnlineAt, c.PanelEnabled, c.SyncedAt,
            c.SuspendedAt, c.SuspendedBy, c.SuspendReason, c.NeedsPush, c.LastError, c.CreatedAt, c.DeletedAt);
    }

    public Task<VpnClient?> Current(Guid userId) => db.VpnClients.SingleOrDefaultAsync(c => c.UserId == userId && c.DeletedAt == null);

    private static string NewSubId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>Creates the member's single configuration. The first configuration per email identity includes the free trial allowance.</summary>
    public async Task<VpnClient> Request(Guid userId, string serverCode)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        if (user.EmailVerifiedAt == null) throw new VpnException("email_verification_required", "ابتدا ایمیل خود را تأیید کنید.", 403);
        var policy = await Policy();
        var existing = await Current(userId);
        if (existing != null) return existing;
        if (!policy.Enabled) throw new VpnException("vpn_closed", "فروش VPN در حال حاضر بسته است.", 409);
        var server = Settings.Find(serverCode) ?? throw new VpnException("vpn_server_unavailable", "این سرور در دسترس نیست.", 409);
        var key = MembershipService.TrialKey(user.Email);
        var trialUsed = await db.VpnClients.AnyAsync(c => c.TrialKey == key && c.TrialGranted);
        var now = DateTime.UtcNow;
        var client = new VpnClient { UserId = userId, TrialKey = key, ServerCode = server.Code, SubId = NewSubId(),
            TrialGranted = !trialUsed && policy.TrialBytes > 0, NeedsPush = true };
        client.ClientEmail = "dz-" + client.Id.ToString("N")[..12];
        if (client.TrialGranted)
        {
            client.LimitBytes = policy.TrialBytes;
            client.PeriodStart = now;
            client.PeriodEnd = policy.TrialDays > 0 ? now.AddDays(policy.TrialDays) : null;
        }
        db.VpnClients.Add(client);
        membership.Log(userId, "vpn_requested", client.TrialGranted ? $"{server.Name} · trial {policy.TrialBytes} bytes" : $"{server.Name} · awaiting payment");
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // A concurrent request created it first.
            db.ChangeTracker.Clear();
            return await Current(userId) ?? throw new VpnException("vpn_request_failed", "درخواست انجام نشد. دوباره تلاش کنید.", 409);
        }
        await TryPush(client.Id);
        return await db.VpnClients.AsNoTracking().SingleAsync(c => c.Id == client.Id);
    }

    /// <summary>Adds paid days. Starts a fresh period (with a full quota) when no paid period is running or the current one is used up.</summary>
    private void Grant(VpnClient c, VpnPolicy policy, int days, DateTime now)
    {
        var running = c.PaidUntil > now;
        var depleted = running && c.UsedUp + c.UsedDown >= c.LimitBytes;
        if (running && !depleted) { c.PaidUntil = c.PaidUntil!.Value.AddDays(days); }
        else
        {
            c.PaidUntil = (running ? c.PaidUntil!.Value : now).AddDays(days);
            c.PeriodStart = now;
            var end = now.AddDays(Math.Min(days, policy.PeriodDays));
            c.PeriodEnd = end < c.PaidUntil ? end : c.PaidUntil;
            c.LimitBytes = c.LimitOverrideBytes ?? policy.MonthlyBytes;
            c.ResetPending = true;
        }
        c.NeedsPush = true;
    }

    /// <summary>Fulfils paid VPN orders. Database only: safe inside the caller's payment transaction. Call <see cref="TryPushUser"/> after commit.</summary>
    public async Task ApplyPayments(Guid userId)
    {
        var orders = await db.PaymentOrders.Where(p => p.UserId == userId && p.Product == "vpn" && p.Status == "paid_queued")
            .OrderBy(p => p.PaidAt).ThenBy(p => p.Id).ToListAsync();
        if (orders.Count == 0) return;
        var policy = await Policy();
        var client = await Current(userId);
        if (client == null)
        {
            // Checkout requires a configuration, but never lose a payment: create one without a trial.
            var server = Settings.Available.FirstOrDefault();
            if (server == null) return; // Stays paid_queued until a server is configured.
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            client = new VpnClient { UserId = userId, TrialKey = MembershipService.TrialKey(user.Email), ServerCode = server.Code, SubId = NewSubId() };
            client.ClientEmail = "dz-" + client.Id.ToString("N")[..12];
            db.VpnClients.Add(client);
        }
        var now = DateTime.UtcNow;
        foreach (var order in orders)
        {
            Grant(client, policy, order.DurationDays, now);
            order.Status = "paid_active"; order.FulfilledAt = now;
            membership.Log(userId, "vpn_period_paid", $"{order.PlanName} · until {client.PaidUntil:yyyy-MM-dd}");
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Removes a refunded order's days. Database only; the worker writes it to the panel.</summary>
    public async Task Refund(PaymentOrder order)
    {
        var client = await Current(order.UserId);
        if (client?.PaidUntil == null || order.FulfilledAt == null) return;
        var now = DateTime.UtcNow;
        var until = client.PaidUntil.Value.AddDays(-order.DurationDays);
        client.PaidUntil = until > now ? until : now;
        if (client.PeriodEnd > client.PaidUntil) client.PeriodEnd = client.PaidUntil;
        client.NeedsPush = true;
        membership.Log(order.UserId, "vpn_period_refunded", order.PlanName);
    }

    public async Task<VpnClient> SetSuspended(Guid clientId, bool suspended, string by, string? reason, Guid? actor)
    {
        var c = await db.VpnClients.SingleOrDefaultAsync(x => x.Id == clientId && x.DeletedAt == null) ?? throw new VpnException("not_found", "یافت نشد.", 404);
        if (!suspended && c.SuspendedBy == "admin" && by == "user")
            throw new VpnException("vpn_admin_suspended", "دسترسی شما توسط مدیر متوقف شده است.", 403);
        if (suspended) { c.SuspendedAt ??= DateTime.UtcNow; c.SuspendedBy = by; c.SuspendReason = reason?.Trim() is { Length: > 0 } r ? r[..Math.Min(r.Length, 200)] : null; }
        else { c.SuspendedAt = null; c.SuspendedBy = null; c.SuspendReason = null; }
        c.NeedsPush = true;
        membership.Log(c.UserId, suspended ? "vpn_suspended" : "vpn_resumed", by, actor);
        await db.SaveChangesAsync();
        await TryPush(c.Id);
        return c;
    }

    public async Task<VpnClient> Rotate(Guid clientId, Guid? actor)
    {
        var c = await db.VpnClients.SingleOrDefaultAsync(x => x.Id == clientId && x.DeletedAt == null) ?? throw new VpnException("not_found", "یافت نشد.", 404);
        // A new UUID and subscription ID revoke every previously shared link and imported profile.
        c.SubId = NewSubId(); c.ClientUuid = Guid.NewGuid().ToString(); c.NeedsPush = true;
        membership.Log(c.UserId, "vpn_link_rotated", "", actor);
        await db.SaveChangesAsync();
        await TryPush(c.Id, rotateUuid: true);
        return c;
    }

    public async Task<VpnClient> AdminExtend(Guid clientId, int days, Guid actor)
    {
        var c = await db.VpnClients.SingleOrDefaultAsync(x => x.Id == clientId && x.DeletedAt == null) ?? throw new VpnException("not_found", "یافت نشد.", 404);
        var policy = await Policy(); var now = DateTime.UtcNow;
        if (days > 0) Grant(c, policy, days, now);
        else if (c.PaidUntil != null)
        {
            // Shorten the paid time; never into the past.
            var until = c.PaidUntil.Value.AddDays(days);
            c.PaidUntil = until > now ? until : now;
            if (c.PeriodEnd > c.PaidUntil) c.PeriodEnd = c.PaidUntil;
            c.NeedsPush = true;
        }
        membership.Log(c.UserId, "vpn_period_adjusted", $"{days} days", actor);
        await db.SaveChangesAsync(); await TryPush(c.Id);
        return c;
    }

    public async Task<VpnClient> AdminSetLimit(Guid clientId, long? monthlyBytes, long? currentBytes, Guid actor)
    {
        var c = await db.VpnClients.SingleOrDefaultAsync(x => x.Id == clientId && x.DeletedAt == null) ?? throw new VpnException("not_found", "یافت نشد.", 404);
        c.LimitOverrideBytes = monthlyBytes;
        if (currentBytes != null) c.LimitBytes = currentBytes.Value;
        c.NeedsPush = true;
        membership.Log(c.UserId, "vpn_limit_changed", $"monthly={monthlyBytes?.ToString() ?? "default"}; current={c.LimitBytes}", actor);
        await db.SaveChangesAsync(); await TryPush(c.Id);
        return c;
    }

    public async Task<VpnClient> AdminResetTraffic(Guid clientId, Guid actor)
    {
        var c = await db.VpnClients.SingleOrDefaultAsync(x => x.Id == clientId && x.DeletedAt == null) ?? throw new VpnException("not_found", "یافت نشد.", 404);
        c.ResetPending = true; c.NeedsPush = true;
        membership.Log(c.UserId, "vpn_traffic_reset", "", actor);
        await db.SaveChangesAsync(); await TryPush(c.Id);
        return c;
    }

    /// <summary>Deletes the panel client (its links stop working) and closes the record. The trial is not granted again.</summary>
    public async Task Revoke(Guid clientId, Guid? actor, string reason)
    {
        var c = await db.VpnClients.SingleOrDefaultAsync(x => x.Id == clientId && x.DeletedAt == null) ?? throw new VpnException("not_found", "یافت نشد.", 404);
        var server = Settings.Servers.FirstOrDefault(s => s.Code == c.ServerCode);
        if (server is { Configured: true })
        {
            await PanelLock.WaitAsync();
            try { await RefreshTraffic(server, c, default); await xui.Delete(server, c.ClientEmail); }
            catch (XuiException e) { throw new VpnException("vpn_panel_error", "سرور VPN پاسخ نداد: " + e.Message, 502); }
            finally { PanelLock.Release(); }
        }
        c.DeletedAt = DateTime.UtcNow; c.Online = false; c.ActiveIps = 0; c.NeedsPush = false;
        membership.Log(c.UserId, "vpn_revoked", reason, actor);
        await db.SaveChangesAsync();
    }

    /// <summary>Best-effort panel write; failures are stored and retried by the worker.</summary>
    public async Task<bool> TryPush(Guid clientId, bool rotateUuid = false)
    {
        try { await Push(clientId, rotateUuid, default); return true; }
        catch (Exception e) when (e is XuiException or VpnException)
        {
            logger.LogWarning("VPN panel update failed for {Client}: {Error}", clientId, e.Message);
            return false;
        }
    }

    public async Task TryPushUser(Guid userId)
    {
        var id = await db.VpnClients.Where(c => c.UserId == userId && c.DeletedAt == null && c.NeedsPush).Select(c => (Guid?)c.Id).SingleOrDefaultAsync();
        if (id != null) await TryPush(id.Value);
    }

    private async Task RefreshTraffic(VpnServerOptions server, VpnClient c, CancellationToken ct)
    {
        var t = await xui.GetTraffic(server, c.ClientEmail, ct);
        if (t == null) return;
        c.UsedUp = t.Up; c.UsedDown = t.Down; c.PanelEnabled = t.Enable;
        if (t.LastOnline > 0) c.LastOnlineAt = DateTimeOffset.FromUnixTimeMilliseconds(t.LastOnline).UtcDateTime;
        c.SyncedAt = DateTime.UtcNow;
    }

    /// <summary>Writes the desired client state to the panel: creates or re-attaches it if missing, updates limits, resets a new period's counters.</summary>
    public async Task Push(Guid clientId, bool rotateUuid, CancellationToken ct)
    {
        await PanelLock.WaitAsync(ct);
        try
        {
            var c = await db.VpnClients.SingleAsync(x => x.Id == clientId, ct);
            if (c.DeletedAt != null) return;
            var server = Settings.Find(c.ServerCode) ?? throw new VpnException("vpn_server_unavailable", "server not configured", 503);
            var policy = await Policy();
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == c.UserId, ct);
            var now = DateTime.UtcNow;
            var blocked = user.DeletedAt != null || user.SuspendedAt != null;
            if (c.ResetPending)
            {
                await RefreshTraffic(server, c, ct);
                c.LifetimeUp += c.UsedUp; c.LifetimeDown += c.UsedDown; c.UsedUp = 0; c.UsedDown = 0;
            }
            var status = Status(c, now, blocked);
            var enable = status is "active" or "trial" && c.LimitBytes > 0;
            // 0 means unlimited in 3x-ui, so a disabled client still carries a tiny positive quota.
            var total = c.LimitBytes > 0 ? c.LimitBytes : 1;
            long expiry = c.PaidUntil != null ? new DateTimeOffset(c.PaidUntil.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()
                : c.PeriodEnd != null ? new DateTimeOffset(c.PeriodEnd.Value, TimeSpan.Zero).ToUnixTimeMilliseconds() : 0;
            var current = await xui.GetClient(server, c.ClientEmail, ct);
            if (current?["client"] is not JsonObject record)
            {
                await xui.Add(server, new JsonObject {
                    ["id"] = c.ClientUuid, ["email"] = c.ClientEmail, ["subId"] = c.SubId, ["enable"] = enable,
                    ["flow"] = server.Flow, ["totalGB"] = total, ["expiryTime"] = expiry, ["limitIp"] = policy.IpLimit,
                    ["comment"] = "darvazeh:" + c.UserId.ToString("N") }, server.Inbounds, ct);
            }
            else
            {
                var attached = (current["inboundIds"] as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0).ToHashSet() ?? [];
                var missing = server.Inbounds.Where(i => !attached.Contains(i)).ToArray();
                if (missing.Length > 0) await xui.Attach(server, c.ClientEmail, missing, ct);
                var w = XuiClient.WriteModel(record);
                if (rotateUuid || string.IsNullOrEmpty(w["id"]?.ToString())) w["id"] = c.ClientUuid;
                else c.ClientUuid = w["id"]!.ToString();
                w["subId"] = c.SubId; w["enable"] = enable; w["totalGB"] = total; w["expiryTime"] = expiry; w["limitIp"] = policy.IpLimit;
                if (string.IsNullOrEmpty(w["flow"]?.ToString())) w["flow"] = server.Flow;
                await xui.Update(server, c.ClientEmail, w, ct);
            }
            if (c.ResetPending) { await xui.ResetTraffic(server, c.ClientEmail, ct); c.ResetPending = false; }
            // resetTraffic re-enables a client, so write the enable flag again when it must stay off.
            if (!enable && (await xui.GetTraffic(server, c.ClientEmail, ct))?.Enable == true)
            {
                var again = await xui.GetClient(server, c.ClientEmail, ct);
                if (again?["client"] is JsonObject r) { var w = XuiClient.WriteModel(r); w["enable"] = false; await xui.Update(server, c.ClientEmail, w, ct); }
            }
            await RefreshTraffic(server, c, ct);
            c.NeedsPush = false; c.LastError = null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is XuiException or VpnException)
        {
            await RecordError(clientId, e.Message);
            throw;
        }
        finally { PanelLock.Release(); }
    }

    private async Task RecordError(Guid clientId, string message)
    {
        db.ChangeTracker.Clear();
        var text = message.Length > 300 ? message[..300] : message;
        await db.VpnClients.Where(x => x.Id == clientId).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastError, text));
    }

    /// <summary>Periodic reconciliation: rolls monthly periods, revokes deleted members, retries pending writes and reads usage/online state.</summary>
    public async Task Sync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var policy = await Policy();
        var clients = await db.VpnClients.Where(c => c.DeletedAt == null).ToListAsync(ct);
        var deletedUsers = await db.Users.Where(u => u.DeletedAt != null).Select(u => u.Id).ToListAsync(ct);
        var suspendedUsers = await db.Users.Where(u => u.SuspendedAt != null).Select(u => u.Id).ToListAsync(ct);
        foreach (var c in clients)
        {
            if (deletedUsers.Contains(c.UserId))
            {
                try { await Revoke(c.Id, null, "account deleted"); } catch (VpnException e) { logger.LogWarning("VPN revoke failed: {Error}", e.Message); }
                continue;
            }
            // Monthly roll-over inside a longer paid time: new period, full quota, counters reset.
            if (c.PaidUntil > now && c.PeriodEnd != null && c.PeriodEnd <= now)
            {
                c.PeriodStart = c.PeriodEnd;
                var end = c.PeriodStart.Value.AddDays(policy.PeriodDays);
                c.PeriodEnd = end < c.PaidUntil ? end : c.PaidUntil;
                c.LimitBytes = c.LimitOverrideBytes ?? policy.MonthlyBytes;
                c.ResetPending = true; c.NeedsPush = true;
                membership.Log(c.UserId, "vpn_period_started", $"until {c.PeriodEnd:yyyy-MM-dd}");
            }
            // Keep the panel's enabled flag in step with suspension of the whole Darvazeh account.
            var status = Status(c, now, suspendedUsers.Contains(c.UserId));
            var shouldBeOn = status is "active" or "trial";
            if (c.SyncedAt != null && shouldBeOn != c.PanelEnabled && status != "expired") c.NeedsPush = true;
        }
        await db.SaveChangesAsync(ct);
        foreach (var c in clients.Where(c => c.DeletedAt == null && c.NeedsPush))
        {
            ct.ThrowIfCancellationRequested();
            await TryPush(c.Id);
        }
        db.ChangeTracker.Clear();
        foreach (var server in Settings.Available)
        {
            HashSet<string> online;
            try { online = await xui.Onlines(server, ct); }
            catch (XuiException e) { logger.LogWarning("VPN server {Server} unavailable: {Error}", server.Code, e.Message); continue; }
            var list = await db.VpnClients.Where(c => c.DeletedAt == null && c.ServerCode == server.Code).ToListAsync(ct);
            foreach (var c in list)
            {
                try
                {
                    await RefreshTraffic(server, c, ct);
                    c.Online = online.Contains(c.ClientEmail);
                    c.ActiveIps = c.Online ? await xui.IpCount(server, c.ClientEmail, ct) : 0;
                    if (c.Online) c.LastOnlineAt = DateTime.UtcNow;
                }
                catch (XuiException e) { c.LastError = e.Message.Length > 300 ? e.Message[..300] : e.Message; }
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Reads one member's usage now (used when they open the VPN page).</summary>
    public async Task Refresh(VpnClient c, CancellationToken ct = default)
    {
        var server = Settings.Find(c.ServerCode);
        if (server == null) return;
        try
        {
            if (c.NeedsPush) { await TryPush(c.Id); await db.Entry(c).ReloadAsync(ct); }
            await RefreshTraffic(server, c, ct);
            c.Online = (await xui.Onlines(server, ct)).Contains(c.ClientEmail);
            c.ActiveIps = c.Online ? await xui.IpCount(server, c.ClientEmail, ct) : 0;
            await db.SaveChangesAsync(ct);
        }
        catch (XuiException e) { logger.LogWarning("VPN refresh failed: {Error}", e.Message); }
    }

    public async Task<object> ServerStatus(CancellationToken ct = default)
    {
        var result = new List<object>();
        foreach (var s in Settings.Servers)
        {
            if (!s.Configured) { result.Add(new { s.Code, s.Name, configured = false, reachable = false, error = "not_configured", inbounds = Array.Empty<object>(), online = 0 }); continue; }
            try
            {
                var inbounds = (await xui.Inbounds(s, ct)).OfType<JsonObject>().Select(i => new {
                    id = (int?)i["id"] ?? 0, remark = i["remark"]?.ToString(), protocol = i["protocol"]?.ToString(),
                    port = (int?)i["port"] ?? 0, enable = (bool?)i["enable"] ?? false }).ToList();
                var online = await xui.Onlines(s, ct);
                result.Add(new { s.Code, s.Name, configured = true, reachable = true, error = (string?)null,
                    inbounds = inbounds.Select(i => new { i.id, i.remark, i.protocol, i.port, i.enable, used = s.Inbounds.Contains(i.id) }),
                    missingInbounds = s.Inbounds.Where(id => inbounds.All(i => i.id != id)), online = online.Count });
            }
            catch (XuiException e) { result.Add(new { s.Code, s.Name, configured = true, reachable = false, error = e.Message, inbounds = Array.Empty<object>(), online = 0 }); }
        }
        return result;
    }
}

public class VpnWorker(IServiceScopeFactory scopes, IOptions<VpnSettings> settings, ILogger<VpnWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.Available.Any()) { logger.LogInformation("No VPN server configured; VPN sync is idle."); }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        do
        {
            if (!settings.Value.Available.Any()) continue;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<VpnService>().Sync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "VPN sync failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
