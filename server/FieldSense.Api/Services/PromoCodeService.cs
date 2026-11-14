using System.Text.RegularExpressions;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Services;

/// <summary>
/// Promo codes for SMS and VPN checkout. The discount is always computed here, on the server, from the plan price.
/// A use is a paid order with the code; an open checkout from the last hour also holds a place, so a limited code
/// cannot be over-sold by many buyers opening checkouts at once.
/// </summary>
public class PromoCodeService(AppDbContext db)
{
    public const int MaxCodeLength = 32;
    private static readonly TimeSpan HoldOpenCheckout = TimeSpan.FromHours(1);

    public static string? Normalize(string? code)
    {
        var value = (code ?? "").Trim().ToUpperInvariant();
        return value.Length == 0 ? null : value;
    }
    public static bool ValidCode(string? code) => code != null && Regex.IsMatch(code, "^[A-Z0-9_-]{3,32}$");

    public record Usage(int Paid, int Holding, int UserPaid);
    public record Price(long Original, long Discount, long Final);

    /// <summary>Paid uses of a code, open checkouts holding a place (one per other member), and this member's paid uses.</summary>
    public async Task<Usage> UsageOf(Guid promoId, Guid? userId)
    {
        var since = DateTime.UtcNow - HoldOpenCheckout;
        var paid = await db.PaymentOrders.CountAsync(p => p.PromoCodeId == promoId && p.PaidAt != null && p.Status != "refunded");
        var holding = await db.PaymentOrders.Where(p => p.PromoCodeId == promoId && p.PaidAt == null &&
                (p.Status == "created" || p.Status == "checkout_open") && p.CreatedAt > since && (userId == null || p.UserId != userId))
            .Select(p => p.UserId).Distinct().CountAsync();
        var userPaid = userId == null ? 0 : await db.PaymentOrders.CountAsync(p => p.PromoCodeId == promoId && p.UserId == userId &&
            p.PaidAt != null && p.Status != "refunded");
        return new Usage(paid, holding, userPaid);
    }

    /// <summary>Finds an active code this member may use for the product, or throws a BillingException the buyer can read.</summary>
    public async Task<PromoCode> Usable(string? rawCode, Guid userId, string product)
    {
        var code = Normalize(rawCode);
        if (!ValidCode(code)) throw new BillingException("promo_invalid", "کد تخفیف معتبر نیست.");
        var promo = await db.PromoCodes.AsNoTracking().SingleOrDefaultAsync(p => p.Code == code);
        if (promo == null || !promo.IsActive) throw new BillingException("promo_invalid", "کد تخفیف معتبر نیست.");
        var now = DateTime.UtcNow;
        if (promo.StartsAt != null && promo.StartsAt > now) throw new BillingException("promo_not_started", "زمان استفاده از این کد تخفیف هنوز نرسیده است.");
        if (promo.ExpiresAt != null && promo.ExpiresAt <= now) throw new BillingException("promo_expired", "مهلت استفاده از این کد تخفیف تمام شده است.");
        if (promo.Product != "all" && promo.Product != product)
            throw new BillingException("promo_wrong_product", product == "vpn" ? "این کد تخفیف برای فیلترشکن نیست." : "این کد تخفیف برای سرویس پیامک‌رسان نیست.");
        var usage = await UsageOf(promo.Id, userId);
        if (promo.MaxUsesPerUser != null && usage.UserPaid >= promo.MaxUsesPerUser)
            throw new BillingException("promo_user_limit", "شما قبلاً از این کد تخفیف استفاده کرده‌اید.");
        if (promo.MaxUses != null && usage.Paid + usage.Holding >= promo.MaxUses)
            throw new BillingException("promo_used_up", "ظرفیت این کد تخفیف تمام شده است.");
        return promo;
    }

    /// <summary>
    /// The price after the code. A fixed code without an amount for this currency does not apply. A discounted price
    /// above zero is never below the provider's minimum (Ezpay 1,000 toman, Stripe 50 cents); zero means free.
    /// </summary>
    public static Price Apply(PromoCode promo, string currency, long amount)
    {
        long discount;
        if (promo.DiscountType == "percent")
            discount = (long)Math.Round(amount * (decimal)promo.DiscountPercent / 100m, MidpointRounding.AwayFromZero);
        else
        {
            var off = currency == "IRT" ? promo.AmountOffToman : promo.AmountOffUsdCents;
            if (off is not > 0) throw new BillingException("promo_wrong_currency",
                currency == "IRT" ? "این کد تخفیف برای پرداخت تومانی نیست." : "این کد تخفیف برای پرداخت دلاری نیست.");
            discount = off.Value;
        }
        discount = Math.Clamp(discount, 0, amount);
        var final = amount - discount;
        var floor = currency == "IRT" ? BillingService.MinToman : 50;
        if (final > 0 && final < floor) final = Math.Min(floor, amount);
        return new Price(amount, amount - final, final);
    }

    /// <summary>Prices with the code for every plan of the product the member can buy, for showing before checkout.</summary>
    public async Task<object> Quote(string? rawCode, Guid userId, string product)
    {
        var promo = await Usable(rawCode, userId, product);
        var plans = await db.Plans.AsNoTracking().Where(p => p.IsActive && p.IsPurchasable && p.Code != "trial" &&
            (product == "vpn" ? p.Code == VpnService.PlanCode : p.Code != VpnService.PlanCode)).ToListAsync();
        object? Line(PromoCode code, string currency, long? amount)
        {
            if (amount is not > 0) return null;
            try { var price = Apply(code, currency, amount.Value); return new { original = price.Original, discount = price.Discount, final = price.Final }; }
            catch (BillingException) { return null; }
        }
        return new {
            code = promo.Code, description = promo.Description, discountType = promo.DiscountType, discountPercent = promo.DiscountPercent,
            expiresAt = promo.ExpiresAt,
            plans = plans.Select(p => new { planId = p.Id, usd = Line(promo, "USD", p.PriceUsdCents), toman = Line(promo, "IRT", p.PriceToman) })
        };
    }
}
