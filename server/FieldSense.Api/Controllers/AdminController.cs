using System.Security.Claims;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Controllers;
public record PlanDto(string Code, string Name, int DurationDays, int MessageLimit, bool SendEnabled, bool ReceiveEnabled,
    long? PriceUsdCents = null, long? PriceToman = null, bool IsPurchasable = false, int EmailForwardLimit = 0);
public record AssignPlanDto(Guid PlanId);
public record FeaturesDto(bool? SendEnabled, bool? ReceiveEnabled);

[ApiController]
[Authorize(Policy = "Owner")]
[Route("api/admin")]
public class AdminController(AppDbContext db, MembershipService membership) : ControllerBase
{
    private Guid ActorId => Guid.Parse(User.FindFirstValue("sub")!);
    [HttpGet("users")]
    public async Task<IActionResult> Users()
    {
        var users = await db.Users.AsNoTracking().Where(u=>u.DeletedAt==null).OrderBy(u => u.Email).Select(u => new {
            u.Id, u.Email, u.Username,u.SuspendedAt,emailVerified=u.EmailVerifiedAt!=null, role = u.Role.ToString(), u.CreatedAt, u.LastLoginAt,
            deviceCount = u.Devices.Count,
            received = u.Messages.Count(m => m.Direction == "inbound" && m.DeletedAt==null),
            sent = u.Messages.Count(m => m.Direction == "outbound" && m.Status == "sent" && m.DeletedAt==null),
            queued = u.Messages.Count(m => m.Status == "queued"), failed = u.Messages.Count(m => m.Status == "failed")
        }).ToListAsync();
        var subscriptions = await db.Subscriptions.AsNoTracking().Include(s => s.Plan).Include(s => s.User).Where(s => s.IsCurrent).ToListAsync();
        return Ok(users.Select(u => new { u.Id, u.Email,u.Username,u.SuspendedAt,u.emailVerified, u.role, u.CreatedAt, u.LastLoginAt,
            u.deviceCount, u.received, u.sent, u.queued, u.failed,
            subscription = MembershipService.View(subscriptions.Single(s => s.UserId == u.Id)) }));
    }
    [HttpGet("plans")]
    public async Task<IActionResult> Plans() => Ok(await db.Plans.AsNoTracking().Where(p => p.IsActive && p.Code != VpnService.PlanCode).OrderBy(p => p.Code).ToListAsync());
    [HttpPost("plans")]
    public async Task<IActionResult> CreatePlan(PlanDto dto)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(dto.Code ?? "", "^[a-z0-9-]{2,30}$") ||
            string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Length > 80 ||
            dto.DurationDays is < 1 or > 3650 || dto.MessageLimit is < 1 or > 1000000 || !ValidPrices(dto) || dto.EmailForwardLimit is < 0 or > 1000000)
            return BadRequest(new { error = "نام، مدت و ظرفیت طرح را بررسی کنید." });
        if (dto.Code == VpnService.PlanCode || await db.Plans.AnyAsync(p => p.Code == dto.Code)) return Conflict(new { error = "این شناسه طرح قبلاً ثبت شده است." });
        var plan = new MembershipPlan { Code = dto.Code!, Name = dto.Name.Trim(), DurationDays = dto.DurationDays,
            MessageLimit = dto.MessageLimit, EmailForwardLimit = dto.EmailForwardLimit, SendEnabled = dto.SendEnabled, ReceiveEnabled = dto.ReceiveEnabled,
            PriceUsdCents = dto.PriceUsdCents, PriceToman = dto.PriceToman, IsPurchasable = dto.IsPurchasable };
        db.Plans.Add(plan); membership.Log(ActorId, "plan_created", plan.Name, ActorId);
        await db.SaveChangesAsync(); return Ok(plan);
    }
    private static bool ValidPrices(PlanDto dto) =>
        (dto.PriceUsdCents == null || dto.PriceUsdCents is >= 50 and <= 99999999) &&
        (dto.PriceToman == null || dto.PriceToman is >= 1 and <= 1000000000000) &&
        (!dto.IsPurchasable || dto.PriceUsdCents != null || dto.PriceToman != null);

    [HttpPut("plans/{id:guid}")]
    public async Task<IActionResult> UpdatePlan(Guid id, PlanDto dto)
    {
        var plan = await db.Plans.FindAsync(id);
        if (plan == null) return NotFound();
        if (plan.Code == "trial" || plan.Code == VpnService.PlanCode || dto.Code != plan.Code || string.IsNullOrWhiteSpace(dto.Name) || dto.Name.Length > 80 ||
            dto.DurationDays is < 1 or > 3650 || dto.MessageLimit is < 1 or > 1000000 || !ValidPrices(dto) || dto.EmailForwardLimit is < 0 or > 1000000)
            return BadRequest(new { error = "اطلاعات طرح یا قیمت‌ها معتبر نیست.", code = "invalid_plan" });
        plan.Name = dto.Name.Trim(); plan.DurationDays = dto.DurationDays; plan.MessageLimit = dto.MessageLimit; plan.EmailForwardLimit = dto.EmailForwardLimit;
        plan.SendEnabled = dto.SendEnabled; plan.ReceiveEnabled = dto.ReceiveEnabled;
        plan.PriceUsdCents = dto.PriceUsdCents; plan.PriceToman = dto.PriceToman; plan.IsPurchasable = dto.IsPurchasable;
        membership.Log(ActorId, "plan_updated", plan.Name, ActorId);
        await db.SaveChangesAsync(); return Ok(plan);
    }
    [HttpPut("users/{id:guid}/subscription")]
    public async Task<IActionResult> AssignPlan(Guid id, AssignPlanDto dto)
    {
        var user = await db.Users.SingleOrDefaultAsync(u=>u.Id==id && u.DeletedAt==null);
        var plan = await db.Plans.SingleOrDefaultAsync(p => p.Id == dto.PlanId && p.IsActive && p.Code != VpnService.PlanCode);
        if (user == null || plan == null) return NotFound();
        user.SendOverride = null; user.ReceiveOverride = null;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Subscriptions.Where(s => s.UserId == id && s.IsCurrent)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.IsCurrent, false));
        var now = DateTime.UtcNow;
        db.Subscriptions.Add(new Subscription { UserId = id, PlanId = plan.Id,
            StartsAt = now, ExpiresAt = now.AddDays(plan.DurationDays), MessageLimit = plan.MessageLimit, EmailForwardLimit = plan.EmailForwardLimit,
            SendEnabled = plan.SendEnabled, ReceiveEnabled = plan.ReceiveEnabled });
        membership.Log(id, "subscription_assigned", plan.Name, ActorId);
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return Ok(await membership.Access(id));
    }
    [HttpPut("users/{id:guid}/features")]
    public async Task<IActionResult> Features(Guid id, FeaturesDto dto)
    {
        var subscription = await db.Subscriptions.Include(s => s.Plan).SingleOrDefaultAsync(s => s.UserId == id && s.IsCurrent && s.User.DeletedAt==null);
        if (subscription == null) return NotFound();
        var user = await db.Users.SingleAsync(u => u.Id == id);
        user.SendOverride = dto.SendEnabled; user.ReceiveOverride = dto.ReceiveEnabled;
        subscription.SendEnabled = dto.SendEnabled ?? subscription.Plan.SendEnabled;
        subscription.ReceiveEnabled = dto.ReceiveEnabled ?? subscription.Plan.ReceiveEnabled;
        membership.Log(id, "features_updated", $"send={dto.SendEnabled}; receive={dto.ReceiveEnabled}", ActorId);
        await db.SaveChangesAsync(); return Ok(await membership.Access(id));
    }
    [HttpGet("activities")]
    public async Task<IActionResult> Activities([FromQuery] Guid? userId, [FromQuery] int take = 100)
    {
        var query = db.Activities.AsNoTracking().AsQueryable();
        if (userId != null) query = query.Where(a => a.UserId == userId);
        return Ok(await query.OrderByDescending(a => a.CreatedAt).Take(Math.Clamp(take, 1, 500))
            .Join(db.Users, a => a.UserId, u => u.Id, (a, u) => new { a.Id, a.UserId, u.Email, a.Action, a.Detail, a.CreatedAt, a.ActorId }).ToListAsync());
    }
    [HttpGet("stats")]
    public async Task<IActionResult> Stats() => Ok(new {
        users = await db.Users.CountAsync(u=>u.DeletedAt==null), devices = await db.Devices.CountAsync(),
        received = await db.Messages.CountAsync(m => m.Direction == "inbound" && m.DeletedAt==null), sent = await db.Messages.CountAsync(m => m.Status == "sent"),
        queued = await db.OutboundJobs.CountAsync(j => j.Status == "queued"), failed = await db.OutboundJobs.CountAsync(j => j.Status == "failed"),
        activeSubscriptions = await db.Subscriptions.CountAsync(s => s.User.DeletedAt==null && s.User.SuspendedAt==null && s.User.EmailVerifiedAt!=null && s.IsCurrent && s.ExpiresAt > DateTime.UtcNow && s.UsedMessages < s.MessageLimit)
    });

    [HttpGet("email-status")]
    public async Task<IActionResult> EmailStatus()=>Ok(await db.EmailDispatches.AsNoTracking().OrderByDescending(e=>e.CreatedAt).Take(100)
        .Select(e=>new {e.Id,e.Kind,e.Status,e.ErrorCode,e.Attempts,e.PollAttempts,e.ConsecutiveFailures,e.CreatedAt,e.AcceptedAt,e.CompletedAt,e.NextAttemptAt}).ToListAsync());

    [HttpGet("payments")]
    public async Task<IActionResult> Payments() => Ok(await db.PaymentOrders.AsNoTracking()
        .OrderByDescending(p => p.CreatedAt).Take(200)
        .Join(db.Users, p => p.UserId, u => u.Id, (p, u) => new {
            p.Id, u.Email, p.PlanName, p.Provider, p.Currency, p.AmountMinor, p.RefundedMinor,
            p.Status, p.CreatedAt, p.PaidAt, p.FulfilledAt }).ToListAsync());
}
