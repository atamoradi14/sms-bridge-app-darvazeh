using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace FieldSense.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<SmsMessage> Messages => Set<SmsMessage>();
    public DbSet<OutboundJob> OutboundJobs => Set<OutboundJob>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
        modelBuilder.Entity<Device>().HasIndex(d => d.DeviceToken).IsUnique();
        modelBuilder.Entity<Device>()
            .HasIndex(d => new { d.UserId, d.InstallationId });
        modelBuilder.Entity<Device>()
            .HasOne(d => d.User).WithMany(u => u.Devices).HasForeignKey(d => d.UserId);
        modelBuilder.Entity<SmsMessage>()
            .HasOne(m => m.User).WithMany(u => u.Messages).HasForeignKey(m => m.UserId);
    }
}
