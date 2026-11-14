using System.Security.Claims;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Controllers;

public record PromoCodeDto(string Code, string? Description, string Product, string DiscountType, int DiscountPercent,
    long? AmountOffToman, long? AmountOffUsdCents, int? MaxUses, int? MaxUsesPerUser, DateTime? StartsAt, DateTime? ExpiresAt, bool IsActive);

[ApiController]
[Authorize(Policy = "Owner")]
[Route("api/admin/promos")]
public class AdminPromoController(AppDbContext db, PromoCodeService promos, MembershipService membership) : ControllerBase
{
    private Guid ActorId => Guid.Parse(User.FindFirstValue("sub")!);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var codes = await db.PromoCodes.AsNoTracking().OrderByDescending(p => p.CreatedAt).ToListAsync();
        var totals = await db.PaymentOrders.AsNoTracking().Where(p => p.PromoCodeId != null && p.PaidAt != null && p.Status != "refunded")
            .GroupBy(p => new { p.PromoCodeId, p.Currency })
            .Select(g => new { g.Key.PromoCodeId, g.Key.Currency, Discount = g.Sum(p => p.DiscountMinor), Paid = g.Sum(p => p.AmountMinor) }).ToListAsync();
        var rows = new List<object>();
        foreach (var c in codes)
        {
            var usage = await promos.UsageOf(c.Id, null);
            var mine = totals.Where(t => t.PromoCodeId == c.Id).ToList();
            rows.Add(new {
                c.Id, c.Code, c.Description, c.Product, c.DiscountType, c.DiscountPercent, c.AmountOffToman, c.AmountOffUsdCents,
                c.MaxUses, c.MaxUsesPerUser, c.StartsAt, c.ExpiresAt, c.IsActive, c.CreatedAt, c.UpdatedAt,
                used = usage.Paid, holding = usage.Holding,
                discountToman = mine.Where(t => t.Currency == "IRT").Sum(t => t.Discount), discountUsdCents = mine.Where(t => t.Currency == "USD").Sum(t => t.Discount),
                status = !c.IsActive ? "inactive" : c.ExpiresAt != null && c.ExpiresAt <= DateTime.UtcNow ? "expired"
                    : c.StartsAt != null && c.StartsAt > DateTime.UtcNow ? "scheduled" : c.MaxUses != null && usage.Paid >= c.MaxUses ? "used_up" : "active"
            });
        }
        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Create(PromoCodeDto dto)
    {
        var code = PromoCodeService.Normalize(dto.Code);
        var error = Validate(dto, code);
        if (error != null) return BadRequest(new { code = "invalid_promo", error });
        if (await db.PromoCodes.AnyAsync(p => p.Code == code)) return Conflict(new { code = "promo_exists", error = "این کد تخفیف قبلاً ساخته شده است." });
        var promo = new PromoCode { Code = code! };
        Fill(promo, dto);
        db.PromoCodes.Add(promo);
        membership.Log(ActorId, "promo_created", promo.Code, ActorId);
        await db.SaveChangesAsync();
        return Ok(promo);
    }

    /// <summary>Changes take effect for new checkouts; orders already created keep the price they were given.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, PromoCodeDto dto)
    {
        var promo = await db.PromoCodes.FindAsync(id);
        if (promo == null) return NotFound();
        var code = PromoCodeService.Normalize(dto.Code);
        var error = Validate(dto, code);
        if (error != null) return BadRequest(new { code = "invalid_promo", error });
        if (code != promo.Code)
        {
            // Renaming is allowed only before anyone has used the code, so order history keeps matching it.
            if (await db.PaymentOrders.AnyAsync(p => p.PromoCodeId == id)) return BadRequest(new { code = "promo_in_use", error = "کدی که استفاده شده را نمی‌توان تغییر نام داد؛ کد تازه بسازید." });
            if (await db.PromoCodes.AnyAsync(p => p.Code == code && p.Id != id)) return Conflict(new { code = "promo_exists", error = "این کد تخفیف قبلاً ساخته شده است." });
            promo.Code = code!;
        }
        Fill(promo, dto);
        promo.UpdatedAt = DateTime.UtcNow;
        membership.Log(ActorId, "promo_updated", promo.Code, ActorId);
        await db.SaveChangesAsync();
        return Ok(promo);
    }

    private static void Fill(PromoCode promo, PromoCodeDto dto)
    {
        promo.Description = (dto.Description ?? "").Trim();
        promo.Product = dto.Product; promo.DiscountType = dto.DiscountType;
        promo.DiscountPercent = dto.DiscountType == "percent" ? dto.DiscountPercent : 0;
        promo.AmountOffToman = dto.DiscountType == "fixed" ? dto.AmountOffToman : null;
        promo.AmountOffUsdCents = dto.DiscountType == "fixed" ? dto.AmountOffUsdCents : null;
        promo.MaxUses = dto.MaxUses; promo.MaxUsesPerUser = dto.MaxUsesPerUser;
        promo.StartsAt = dto.StartsAt?.ToUniversalTime(); promo.ExpiresAt = dto.ExpiresAt?.ToUniversalTime();
        promo.IsActive = dto.IsActive;
    }

    private static string? Validate(PromoCodeDto dto, string? code)
    {
        if (!PromoCodeService.ValidCode(code)) return "کد باید ۳ تا ۳۲ حرف انگلیسی، عدد، خط تیره یا زیرخط باشد.";
        if ((dto.Description ?? "").Length > 200) return "توضیح حداکثر ۲۰۰ نویسه است.";
        if (dto.Product is not ("all" or "sms" or "vpn")) return "سرویس کد را انتخاب کنید.";
        if (dto.DiscountType == "percent") { if (dto.DiscountPercent is < 1 or > 100) return "درصد تخفیف باید بین ۱ تا ۱۰۰ باشد."; }
        else if (dto.DiscountType == "fixed")
        {
            if (dto.AmountOffToman is null && dto.AmountOffUsdCents is null) return "مبلغ تخفیف تومانی یا دلاری را وارد کنید.";
            if (dto.AmountOffToman is not null and (< 1 or > 1_000_000_000)) return "مبلغ تخفیف تومانی معتبر نیست.";
            if (dto.AmountOffUsdCents is not null and (< 1 or > 99_999_999)) return "مبلغ تخفیف دلاری معتبر نیست.";
        }
        else return "نوع تخفیف را انتخاب کنید.";
        if (dto.MaxUses is < 1 or > 1_000_000) return "حداکثر تعداد استفاده باید بین ۱ تا ۱٬۰۰۰٬۰۰۰ باشد.";
        if (dto.MaxUsesPerUser is < 1 or > 1000) return "حداکثر استفادهٔ هر کاربر باید بین ۱ تا ۱۰۰۰ باشد.";
        if (dto.StartsAt != null && dto.ExpiresAt != null && dto.ExpiresAt <= dto.StartsAt) return "تاریخ پایان باید بعد از تاریخ شروع باشد.";
        return null;
    }
}
