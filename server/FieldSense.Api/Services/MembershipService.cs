using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Services;

public record AccountAccess(string PlanCode, string PlanName, DateTime StartsAt, DateTime ExpiresAt,
    int MessageLimit, int UsedMessages, int RemainingMessages, bool Active, bool SendEnabled,
    bool ReceiveEnabled, bool CanSend, bool CanReceive, int EmailForwardLimit, int UsedEmailForwards, int RemainingEmailForwards, bool CanForwardEmail);

public class MembershipService(AppDbContext db)
{
    public static string TrialKey(string email) {
        var parts=email.Trim().ToLowerInvariant().Split('@');
        if(parts.Length==2 && (parts[1]=="gmail.com" || parts[1]=="googlemail.com"))
            email=parts[0].Split('+')[0].Replace(".","")+"@gmail.com";
        else email=email.Trim().ToLowerInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(email)));
    }
    public static AccountAccess View(Subscription subscription, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var active = subscription.User?.EmailVerifiedAt != null && subscription.User.DeletedAt==null && subscription.User.SuspendedAt==null && subscription.IsCurrent && subscription.StartsAt <= now && subscription.ExpiresAt > now;
        var remaining = Math.Max(0, subscription.MessageLimit - subscription.UsedMessages);
        var sendEnabled = subscription.User?.SendOverride ?? subscription.SendEnabled;
        var receiveEnabled = subscription.User?.ReceiveOverride ?? subscription.ReceiveEnabled;
        return new(subscription.Plan.Code, subscription.Plan.Name, subscription.StartsAt, subscription.ExpiresAt,
            subscription.MessageLimit, subscription.UsedMessages, remaining, active,
            sendEnabled, receiveEnabled,
            active && remaining > 0 && sendEnabled,
            active && remaining > 0 && receiveEnabled, subscription.EmailForwardLimit, subscription.UsedEmailForwards,
            Math.Max(0, subscription.EmailForwardLimit - subscription.UsedEmailForwards),
            active && receiveEnabled && subscription.UsedEmailForwards < subscription.EmailForwardLimit);
    }

    private Task<Subscription> ReadCurrent(Guid userId) => db.Subscriptions.AsNoTracking()
        .Include(s => s.Plan).Include(s => s.User).SingleAsync(s => s.UserId == userId && s.IsCurrent);

    public async Task<Subscription> Current(Guid userId)
    {
        var current = await ReadCurrent(userId);
        bool CanStart(Subscription s) => s.Plan.Code == "trial" || !View(s).Active || s.UsedMessages >= s.MessageLimit;
        if (current.User.EmailVerifiedAt==null || current.User.DeletedAt!=null || current.User.SuspendedAt!=null || !CanStart(current) || !await db.PaymentOrders.AnyAsync(p => p.UserId == userId && p.Status == "paid_queued" && p.Product == "sms"))
            return current;
        var transaction = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            current = await ReadCurrent(userId);
            var order = CanStart(current) ? await db.PaymentOrders.Where(p => p.UserId == userId && p.Status == "paid_queued" && p.Product == "sms")
                .OrderBy(p => p.PaidAt).ThenBy(p => p.Id).FirstOrDefaultAsync() : null;
            if (order != null)
            {
                var user = await db.Users.SingleAsync(u => u.Id == userId);
                var now = DateTime.UtcNow;
                await db.Subscriptions.Where(s => s.UserId == userId && s.IsCurrent)
                    .ExecuteUpdateAsync(update => update.SetProperty(s => s.IsCurrent, false));
                db.Subscriptions.Add(new Subscription { UserId = userId, PlanId = order.PlanId,
                    StartsAt = now, ExpiresAt = now.AddDays(order.DurationDays), MessageLimit = order.MessageLimit, EmailForwardLimit = order.EmailForwardLimit,
                    SendEnabled = user.SendOverride ?? order.SendEnabled,
                    ReceiveEnabled = user.ReceiveOverride ?? order.ReceiveEnabled, PaymentOrderId = order.Id });
                order.Status = "paid_active"; order.FulfilledAt = now;
                Log(userId, "paid_membership_activated", order.PlanName);
                await db.SaveChangesAsync();
                current = await ReadCurrent(userId);
            }
            if (transaction != null) await transaction.CommitAsync();
            return current;
        }
        finally { if (transaction != null) await transaction.DisposeAsync(); }
    }

    public async Task<AccountAccess> Access(Guid userId) => View(await Current(userId));

    public async Task StartTrial(AppUser user)
    {
        if (await db.Subscriptions.AnyAsync(s=>s.UserId==user.Id) || db.Subscriptions.Local.Any(s=>s.UserId==user.Id)) return;
        var plan = await db.Plans.SingleAsync(p => p.Code == "trial");
        var verified = user.EmailVerifiedAt != null;
        var now = DateTime.UtcNow;
        if (verified) user.TrialStartedAt ??= now;
        db.Subscriptions.Add(new Subscription { UserId = user.Id, PlanId = plan.Id,
            StartsAt = now, ExpiresAt = verified ? now.AddDays(3) : now, MessageLimit = verified ? 20 : 0, EmailForwardLimit = verified ? 20 : 0,
            SendEnabled = verified, ReceiveEnabled = verified });
        Log(user.Id, verified ? "trial_started" : "account_created", verified ? "3 days · 20 messages" : "Awaiting email verification");
    }

    public async Task VerifyEmail(AppUser user, bool independentLogin = false)
    {
        var transaction = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync() : null;
        try {
            var now=DateTime.UtcNow;
            if(db.Entry(user).State==EntityState.Added)await StartTrial(user);
            if(user.EmailVerifiedAt==null) {
                user.EmailVerifiedAt=now;
                // Passwords/sessions created by an unproven registrant must not survive
                // the mailbox owner's first independent Google/OTP sign-in.
                if(independentLogin){user.PasswordHash=null;user.Username=null;user.AuthVersion++;user.ForwardEmailEnabled=false;user.ForwardEmailAddress=user.Email;user.SendOverride=null;user.ReceiveOverride=null;}
                Log(user.Id,"email_verified");
            }
            if(user.TrialStartedAt==null) {
                user.TrialStartedAt=now;
                var key=TrialKey(user.Email);
                var claimed=await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO TrialClaims (Key,UserId) VALUES ({key},{user.Id.ToString().ToUpperInvariant()})");
                var pending=db.Subscriptions.Local.SingleOrDefault(s=>s.UserId==user.Id && s.IsCurrent) ?? await db.Subscriptions.Include(s=>s.Plan).SingleOrDefaultAsync(s=>s.UserId==user.Id && s.IsCurrent);
                if(pending==null) { await StartTrial(user); pending=db.Subscriptions.Local.Single(s=>s.UserId==user.Id); }
                if(claimed==1 && pending.PlanId==await db.Plans.Where(p=>p.Code=="trial").Select(p=>p.Id).SingleAsync()) {
                    pending.StartsAt=now;pending.ExpiresAt=now.AddDays(3);pending.MessageLimit=20;pending.EmailForwardLimit=20;
                    pending.SendEnabled=true;pending.ReceiveEnabled=true;
                    Log(user.Id,"trial_started","3 days · 20 messages");
                } else if(claimed==0) { pending.ExpiresAt=now;pending.MessageLimit=0;pending.EmailForwardLimit=0;pending.SendEnabled=false;pending.ReceiveEnabled=false;Log(user.Id,"trial_unavailable","Email identity has already used a trial"); }
            }
            await db.SaveChangesAsync();
            if(transaction!=null)await transaction.CommitAsync();
        } finally {if(transaction!=null)await transaction.DisposeAsync();}
    }


    // Called inside the inbound message transaction; each email reservation belongs
    // to the same period as the message, independent of the overall SMS allowance.
    public async Task<Guid?> ReserveEmail(AppUser user, SmsMessage message)
    {
        if (!user.ForwardEmailEnabled) return null;
        var periodId=db.UsageEntries.Local.Single(u=>u.MessageId==message.Id).SubscriptionId;
        var subscription=await db.Subscriptions.AsNoTracking().SingleAsync(s=>s.Id==periodId);
        var now = DateTime.UtcNow;
        var changed = await db.Subscriptions.Where(s => s.Id == subscription.Id && s.IsCurrent && s.ExpiresAt > now &&
            s.UsedEmailForwards < s.EmailForwardLimit).ExecuteUpdateAsync(u => u.SetProperty(s => s.UsedEmailForwards, s => s.UsedEmailForwards + 1));
        message.EmailForwardStatus = changed == 1 ? "pending" : "limit_reached";
        return changed == 1 ? subscription.Id : null;
    }
    public void Log(Guid userId, string action, string detail = "", Guid? actor = null) =>
        db.Activities.Add(new AccountActivity { UserId = userId, ActorId = actor, Action = action, Detail = detail });

    // Caller owns a SQLite write transaction. Reservation, message and ledger entry
    // commit together; rejected requests and retries cannot consume another unit.
    public async Task<string?> Reserve(Guid userId, string direction, SmsMessage message)
    {
        var subscription = await Current(userId);
        if(subscription.User.EmailVerifiedAt==null)return "email_verification_required";
        var access = View(subscription);
        if (!access.Active) return "subscription_expired";
        if (direction == "outbound" ? !access.SendEnabled : !access.ReceiveEnabled) return "feature_disabled";
        var now = DateTime.UtcNow;
        var count = await db.Subscriptions.Where(s => s.Id == subscription.Id && s.IsCurrent &&
            s.ExpiresAt > now && s.UsedMessages < s.MessageLimit)
            .ExecuteUpdateAsync(updates => updates.SetProperty(s => s.UsedMessages, s => s.UsedMessages + 1));
        if (count != 1) return "message_limit_reached";
        db.UsageEntries.Add(new UsageEntry { UserId = userId, SubscriptionId = subscription.Id,
            MessageId = message.Id, Direction = direction });
        Log(userId, direction == "outbound" ? "message_queued" : "message_received");
        return null;
    }
}
