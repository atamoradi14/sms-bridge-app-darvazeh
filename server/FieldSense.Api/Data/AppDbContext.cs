using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<SecuritySettings> SecuritySettings => Set<SecuritySettings>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<SmsMessage> Messages => Set<SmsMessage>();
    public DbSet<OutboundJob> OutboundJobs => Set<OutboundJob>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<MembershipPlan> Plans => Set<MembershipPlan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<UsageEntry> UsageEntries => Set<UsageEntry>();
    public DbSet<AccountActivity> Activities => Set<AccountActivity>();
    public DbSet<PaymentOrder> PaymentOrders => Set<PaymentOrder>();
    public DbSet<PaymentEventReceipt> PaymentEvents => Set<PaymentEventReceipt>();

    public DbSet<GoogleLoginFlow> GoogleLoginFlows => Set<GoogleLoginFlow>();
    public DbSet<EmailDispatch> EmailDispatches => Set<EmailDispatch>();
    public DbSet<DeviceLinkRequest> DeviceLinks => Set<DeviceLinkRequest>();
    public DbSet<DeviceAlert> DeviceAlerts => Set<DeviceAlert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceLinkRequest>().HasIndex(r => new { r.Email, r.CreatedAt });
        modelBuilder.Entity<DeviceAlert>().HasIndex(a => new { a.DeviceId, a.Kind }).IsUnique().HasFilter("ResolvedAt IS NULL");
        modelBuilder.Entity<AppUser>().HasIndex(u => u.GoogleSubject).IsUnique();
        modelBuilder.Entity<AppUser>().HasIndex(u => u.Username).IsUnique().HasFilter("DeletedAt IS NULL");
        modelBuilder.Entity<EmailDispatch>().HasIndex(e=>new {e.Kind,e.NextAttemptAt});
        modelBuilder.Entity<EmailDispatch>().HasIndex(e=>e.MessageId).IsUnique();
        modelBuilder.Entity<AppUser>().HasIndex(u => u.Email).IsUnique().HasFilter("DeletedAt IS NULL");
        modelBuilder.Entity<MembershipPlan>().HasIndex(p => p.Code).IsUnique();
        modelBuilder.Entity<PaymentOrder>().HasIndex(p => new { p.UserId, p.RequestKey }).IsUnique();
        modelBuilder.Entity<PaymentOrder>().HasIndex(p => p.ProviderSessionId).IsUnique();
        modelBuilder.Entity<PaymentOrder>().HasIndex(p => p.ProviderPaymentId).IsUnique();
        modelBuilder.Entity<Subscription>().HasIndex(s => s.PaymentOrderId).IsUnique();
        modelBuilder.Entity<Subscription>().HasIndex(s => s.UserId).IsUnique().HasFilter("IsCurrent = 1");
        modelBuilder.Entity<UsageEntry>().HasIndex(u => u.MessageId).IsUnique();
        modelBuilder.Entity<AccountActivity>().HasIndex(a => new { a.UserId, a.CreatedAt });
        modelBuilder.Entity<SmsMessage>().HasIndex(m => new { m.DeviceId, m.ClientMessageId }).IsUnique();
        modelBuilder.Entity<Device>().HasIndex(d => d.DeviceToken).IsUnique();
        modelBuilder.Entity<Device>()
            .HasIndex(d => new { d.UserId, d.InstallationId });
        modelBuilder.Entity<Device>()
            .HasOne(d => d.User).WithMany(u => u.Devices).HasForeignKey(d => d.UserId);
        modelBuilder.Entity<SmsMessage>()
            .HasOne(m => m.User).WithMany(u => u.Messages).HasForeignKey(m => m.UserId);
    }
}
