using System.Security.Claims;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FieldSense.Api.Controllers;

public record VpnRequestDto(string? ServerCode);
public record VpnPolicyDto(bool Enabled, long TrialBytes, int TrialDays, long MonthlyBytes, int PeriodDays, int IpLimit,
    long? PriceUsdCents, long? PriceToman);
public record VpnSuspendDto(bool Suspended, string? Reason);
public record VpnExtendDto(int Days);
public record VpnLimitDto(long? MonthlyBytes, long? CurrentBytes);

/// <summary>Member VPN: request a configuration, see usage, pause/resume and replace the link. Payment uses /api/billing/checkout with the returned plan id.</summary>
[ApiController, Authorize, Route("api/vpn")]
public class VpnController(VpnService vpn, AppDbContext db, IOptions<VpnSettings> settings, IOptions<BillingSettings> billing, IWebHostEnvironment environment, PaymentWalletService wallet) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue("sub")!);
    private ObjectResult Fail(VpnException e) => StatusCode(e.Status, new { error = e.Message, code = e.Code });

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] bool refresh = false)
    {
        var policy = await vpn.Policy();
        var plan = await vpn.Plan();
        var client = await vpn.Current(UserId);
        // Reading the panel costs requests; only on an explicit refresh or a pending change, at most every 3 seconds.
        if (client != null && (client.NeedsPush || refresh) && (client.SyncedAt == null || client.SyncedAt < DateTime.UtcNow.AddSeconds(-3) || client.NeedsPush))
            await vpn.Refresh(client);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == UserId);
        var key = MembershipService.TrialKey(user.Email);
        var trialUsed = await db.VpnClients.AnyAsync(c => c.TrialKey == key && c.TrialGranted);
        return Ok(new {
            policy = new { policy.Enabled, policy.TrialBytes, policy.TrialDays, policy.MonthlyBytes, policy.PeriodDays, policy.IpLimit },
            plan = plan == null ? null : new { plan.Id, plan.Name, plan.DurationDays, plan.PriceUsdCents, plan.PriceToman, plan.IsPurchasable },
            servers = settings.Value.Available.Select(s => new { s.Code, s.Name, nameFa = s.NameFa == "" ? s.Name : s.NameFa }),
            trialAvailable = !trialUsed && policy.TrialBytes > 0,
            stripeAvailable = billing.Value.Configured,
            tomanAvailable = billing.Value.EzpayConfiguredFor(environment.IsDevelopment()) && await wallet.Offered(),
            account = client == null ? null : vpn.View(client, policy, user.SuspendedAt != null),
            orders = await db.PaymentOrders.AsNoTracking().Where(p => p.UserId == UserId && p.Product == "vpn")
                .OrderByDescending(p => p.CreatedAt).Take(50).Select(p => new { p.Id, p.PlanName, p.DurationDays, p.Currency, p.AmountMinor,
                    p.RefundedMinor, p.Status, p.CreatedAt, p.PaidAt, p.FulfilledAt }).ToListAsync()
        });
    }

    [HttpPost("request")]
    public async Task<IActionResult> RequestConfiguration(VpnRequestDto dto)
    {
        try
        {
            var code = dto.ServerCode ?? settings.Value.Available.FirstOrDefault()?.Code ?? "";
            var client = await vpn.Request(UserId, code);
            return Ok(vpn.View(client, await vpn.Policy()));
        }
        catch (VpnException e) { return Fail(e); }
    }

    [HttpPost("suspension")]
    public async Task<IActionResult> Suspension(VpnSuspendDto dto)
    {
        var client = await vpn.Current(UserId);
        if (client == null) return NotFound(new { error = "پیکربندی VPN ندارید.", code = "vpn_not_found" });
        try { return Ok(vpn.View(await vpn.SetSuspended(client.Id, dto.Suspended, "user", null, UserId), await vpn.Policy())); }
        catch (VpnException e) { return Fail(e); }
    }

    [HttpPost("rotate")]
    public async Task<IActionResult> Rotate()
    {
        var client = await vpn.Current(UserId);
        if (client == null) return NotFound(new { error = "پیکربندی VPN ندارید.", code = "vpn_not_found" });
        if (await db.Activities.CountAsync(a => a.UserId == UserId && a.Action == "vpn_link_rotated" && a.CreatedAt > DateTime.UtcNow.AddDays(-1)) >= 5)
            return StatusCode(429, new { error = "امروز چند بار پیوند را عوض کرده‌اید. فردا دوباره تلاش کنید.", code = "vpn_rotate_limit" });
        try { return Ok(vpn.View(await vpn.Rotate(client.Id, UserId), await vpn.Policy())); }
        catch (VpnException e) { return Fail(e); }
    }
}

[ApiController, Authorize(Policy = "Owner"), Route("api/admin/vpn")]
public class AdminVpnController(VpnService vpn, AppDbContext db, IOptions<VpnSettings> settings) : ControllerBase
{
    private Guid ActorId => Guid.Parse(User.FindFirstValue("sub")!);
    private ObjectResult Fail(VpnException e) => StatusCode(e.Status, new { error = e.Message, code = e.Code });
    private const long MB = 1024 * 1024;

    [HttpGet]
    public async Task<IActionResult> Overview()
    {
        var policy = await vpn.Policy(); var plan = await vpn.Plan(); var now = DateTime.UtcNow;
        var clients = await db.VpnClients.AsNoTracking().Where(c => c.DeletedAt == null).ToListAsync();
        var paidOrders = await db.PaymentOrders.AsNoTracking().Where(p => p.Product == "vpn" && p.PaidAt != null).ToListAsync();
        return Ok(new {
            policy, plan = plan == null ? null : new { plan.Id, plan.Name, plan.PriceUsdCents, plan.PriceToman, plan.IsPurchasable },
            stats = new {
                total = clients.Count,
                active = clients.Count(c => VpnService.Status(c, now) == "active"),
                trial = clients.Count(c => VpnService.Status(c, now) == "trial"),
                expired = clients.Count(c => VpnService.Status(c, now) is "expired" or "depleted" or "trial_used" or "trial_ended"),
                suspended = clients.Count(c => c.SuspendedAt != null),
                online = clients.Count(c => c.Online),
                overIpLimit = clients.Count(c => c.ActiveIps > policy.IpLimit),
                trafficBytes = clients.Sum(c => c.UsedUp + c.UsedDown + c.LifetimeUp + c.LifetimeDown),
                revenueUsdCents = paidOrders.Where(p => p.Currency == "USD").Sum(p => p.AmountMinor - p.RefundedMinor),
                revenueToman = paidOrders.Where(p => p.Currency == "IRT").Sum(p => p.AmountMinor - p.RefundedMinor)
            },
            servers = settings.Value.Servers.Select(s => new { s.Code, s.Name, configured = s.Configured, inbounds = s.Inbounds })
        });
    }

    [HttpGet("servers")]
    public async Task<IActionResult> Servers(CancellationToken ct) => Ok(await vpn.ServerStatus(ct));

    [HttpPut("policy")]
    public async Task<IActionResult> UpdatePolicy(VpnPolicyDto dto)
    {
        if (dto.TrialBytes is < 0 or > 1024L * 1024 * MB || dto.MonthlyBytes is < MB or > 10L * 1024 * 1024 * MB ||
            dto.TrialDays is < 0 or > 365 || dto.PeriodDays is < 1 or > 365 || dto.IpLimit is < 1 or > 10 ||
            (dto.PriceUsdCents != null && dto.PriceUsdCents is < 50 or > 99999999) ||
            (dto.PriceToman != null && dto.PriceToman is < BillingService.MinToman or > BillingService.MaxToman) ||
            (dto.TrialBytes is > 0 and < MB))
            return BadRequest(new { error = "مقادیر طرح VPN معتبر نیست.", code = "invalid_vpn_policy" });
        var policy = await db.VpnPolicies.SingleOrDefaultAsync(p => p.Id == 1);
        if (policy == null) { policy = new VpnPolicy(); db.VpnPolicies.Add(policy); }
        var ipChanged = policy.IpLimit != dto.IpLimit;
        policy.Enabled = dto.Enabled; policy.TrialBytes = dto.TrialBytes; policy.TrialDays = dto.TrialDays;
        policy.MonthlyBytes = dto.MonthlyBytes; policy.PeriodDays = dto.PeriodDays; policy.IpLimit = dto.IpLimit; policy.UpdatedAt = DateTime.UtcNow;
        var plan = await db.Plans.SingleAsync(p => p.Code == VpnService.PlanCode);
        plan.DurationDays = dto.PeriodDays; plan.PriceUsdCents = dto.PriceUsdCents; plan.PriceToman = dto.PriceToman;
        plan.IsPurchasable = dto.Enabled && (dto.PriceUsdCents != null || dto.PriceToman != null);
        plan.Name = $"Iran VPN · {dto.PeriodDays} days";
        // A new IP limit applies to existing clients on their next panel write.
        if (ipChanged) await db.VpnClients.Where(c => c.DeletedAt == null).ExecuteUpdateAsync(u => u.SetProperty(c => c.NeedsPush, true));
        db.Activities.Add(new AccountActivity { UserId = ActorId, ActorId = ActorId, Action = "vpn_policy_updated",
            Detail = $"trial={dto.TrialBytes}; monthly={dto.MonthlyBytes}; usd={dto.PriceUsdCents}; toman={dto.PriceToman}" });
        await db.SaveChangesAsync();
        return Ok(policy);
    }

    [HttpGet("accounts")]
    public async Task<IActionResult> Accounts([FromQuery] bool includeRevoked = false)
    {
        var policy = await vpn.Policy();
        var rows = await db.VpnClients.AsNoTracking().Where(c => includeRevoked || c.DeletedAt == null).OrderByDescending(c => c.CreatedAt).Take(1000)
            .Join(db.Users, c => c.UserId, u => u.Id, (c, u) => new { c, u.Email, u.SuspendedAt }).ToListAsync();
        return Ok(rows.Select(r => new { email = r.Email, clientEmail = r.c.ClientEmail, account = vpn.View(r.c, policy, r.SuspendedAt != null) }));
    }

    private async Task<IActionResult> Act(Func<Task<VpnClient>> action)
    {
        try { var c = await action(); return Ok(vpn.View(await db.VpnClients.AsNoTracking().SingleAsync(x => x.Id == c.Id), await vpn.Policy())); }
        catch (VpnException e) { return Fail(e); }
    }

    [HttpPost("accounts/{id:guid}/suspension")]
    public Task<IActionResult> Suspend(Guid id, VpnSuspendDto dto) => Act(() => vpn.SetSuspended(id, dto.Suspended, "admin", dto.Reason, ActorId));

    [HttpPost("accounts/{id:guid}/extend")]
    public Task<IActionResult> Extend(Guid id, VpnExtendDto dto) => dto.Days is 0 or < -3650 or > 3650
        ? Task.FromResult<IActionResult>(BadRequest(new { error = "تعداد روز معتبر نیست.", code = "invalid_days" }))
        : Act(() => vpn.AdminExtend(id, dto.Days, ActorId));

    [HttpPost("accounts/{id:guid}/limit")]
    public Task<IActionResult> Limit(Guid id, VpnLimitDto dto) =>
        dto.MonthlyBytes is < MB or > 10L * 1024 * 1024 * MB || dto.CurrentBytes is < MB or > 10L * 1024 * 1024 * MB
        ? Task.FromResult<IActionResult>(BadRequest(new { error = "حجم معتبر نیست (حداقل ۱ مگابایت).", code = "invalid_limit" }))
        : Act(() => vpn.AdminSetLimit(id, dto.MonthlyBytes, dto.CurrentBytes, ActorId));

    [HttpPost("accounts/{id:guid}/reset-traffic")]
    public Task<IActionResult> ResetTraffic(Guid id) => Act(() => vpn.AdminResetTraffic(id, ActorId));

    [HttpPost("accounts/{id:guid}/rotate")]
    public Task<IActionResult> Rotate(Guid id) => Act(() => vpn.Rotate(id, ActorId));

    [HttpPost("accounts/{id:guid}/sync")]
    public async Task<IActionResult> Sync(Guid id)
    {
        var c = await db.VpnClients.SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null);
        if (c == null) return NotFound();
        c.NeedsPush = true; await db.SaveChangesAsync();
        await vpn.Refresh(c);
        return Ok(vpn.View(await db.VpnClients.AsNoTracking().SingleAsync(x => x.Id == id), await vpn.Policy()));
    }

    [HttpDelete("accounts/{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        try { await vpn.Revoke(id, ActorId, "admin"); return Ok(new { revoked = true }); }
        catch (VpnException e) { return Fail(e); }
    }
}
