using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;
namespace FieldSense.Api.Services;
public class DeviceHealthService(AppDbContext db)
{
    public async Task Check(CancellationToken ct=default) {
        var now=DateTime.UtcNow;var stale=now.AddMinutes(-3);
        var devices=await db.Devices.Include(d=>d.User).ToListAsync(ct);
        var open=await db.DeviceAlerts.Where(a=>a.ResolvedAt==null).ToListAsync(ct);
        foreach(var d in devices) {
            var monitored=d.RevokedAt==null && d.AdminPausedAt==null && d.User.SuspendedAt==null && d.User.DeletedAt==null;
            d.IsOnline=monitored && d.LastSeenAt>stale;
            var wanted=new HashSet<string>();
            if(monitored) {
                if((d.LastSeenAt??d.CreatedAt)<=stale) wanted.Add("offline");
                if(d.BatteryLevel<=20 && d.IsCharging!=true) wanted.Add(d.BatteryLevel<=10?"battery_critical":"battery_low");
                if(d.NetworkValidated==false)wanted.Add("network_unverified");
                if(d.SmsPermissionsGranted==false)wanted.Add("sms_permission");
                if(d.BatteryOptimizationExempt==false)wanted.Add("battery_optimization");
            }
            foreach(var a in open.Where(a=>a.DeviceId==d.Id))if(!wanted.Contains(a.Kind))a.ResolvedAt=now;
            foreach(var kind in wanted)if(!open.Any(a=>a.DeviceId==d.Id && a.Kind==kind))
                db.DeviceAlerts.Add(new DeviceAlert {UserId=d.UserId,DeviceId=d.Id,DeviceName=d.DeviceModel??d.Name,Kind=kind});
        }
        foreach(var a in open.Where(a=>!devices.Any(d=>d.Id==a.DeviceId)))a.ResolvedAt=now;
        await db.SaveChangesAsync(ct);
    }
}
public class DeviceHealthMonitor(IServiceScopeFactory scopes, ILogger<DeviceHealthMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        try {
            while(await timer.WaitForNextTickAsync(stoppingToken)) {
                try {using var scope=scopes.CreateScope();await scope.ServiceProvider.GetRequiredService<DeviceHealthService>().Check(stoppingToken);}
                catch(Exception e) when(!stoppingToken.IsCancellationRequested){logger.LogWarning(e,"Device health check will retry");}
            }
        } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
    }
}
