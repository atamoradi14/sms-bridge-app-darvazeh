using FieldSense.Api.Data;
using Microsoft.EntityFrameworkCore;
namespace FieldSense.Api.Services;
public static class MessageHistory {
    public static async Task<object> Page(AppDbContext db,Guid userId,string? search,string? direction,int page,int pageSize) {
        pageSize=Math.Clamp(pageSize,10,100);page=Math.Max(1,page);
        var query=db.Messages.AsNoTracking().Where(m=>m.UserId==userId && m.DeletedAt==null);
        if(direction is "inbound" or "outbound")query=query.Where(m=>m.Direction==direction);
        var q=(search??"").Trim().ToLowerInvariant();if(q.Length>200)q=q[..200];
        if(q!="")query=query.Where(m=>m.Body.ToLower().Contains(q)||m.PeerNumber.ToLower().Contains(q));
        var total=await query.CountAsync();var pages=Math.Max(1,(int)Math.Ceiling(total/(double)pageSize));page=Math.Min(page,pages);
        var items=await query.OrderByDescending(m=>m.CreatedAt).ThenByDescending(m=>m.Id).Skip((page-1)*pageSize).Take(pageSize)
            .Select(m=>new {m.Id,m.Direction,m.PeerNumber,m.Body,m.Status,m.EmailForwarded,m.EmailForwardStatus,m.BatteryLevel,m.IsCharging,m.NetworkType,m.CreatedAt}).ToListAsync();
        return new {items,total,page,pageSize,totalPages=pages};
    }
    // Preserve ledger/dedup identifiers. Removing history never refunds usage or
    // sends a previously deleted incoming message twice on a device retry.
    public static async Task<string?> Delete(AppDbContext db,Guid userId,Guid[] ids) {
        if(ids.Length is <1 or >100)return "invalid_selection";
        var rows=await db.Messages.Where(m=>m.UserId==userId && ids.Contains(m.Id) && m.DeletedAt==null).ToListAsync();
        if(rows.Count!=ids.Distinct().Count())return "not_found";
        if(rows.Any(m=>m.Status=="queued"))return "message_in_progress";
        var now=DateTime.UtcNow;
        foreach(var message in rows){message.DeletedAt=now;message.Body="";message.PeerNumber="";}
        await db.OutboundJobs.Where(j=>j.UserId==userId && ids.Contains(j.SmsMessageId)).ExecuteUpdateAsync(u=>u.SetProperty(j=>j.Body,"").SetProperty(j=>j.ToNumber,"").SetProperty(j=>j.Error,(string?)null));
        await db.EmailDispatches.Where(e=>e.UserId==userId && e.MessageId!=null && ids.Contains(e.MessageId.Value) && (e.Status=="queued"||e.Status=="submitting"||e.Status=="accepted"))
            .ExecuteUpdateAsync(u=>u.SetProperty(e=>e.Status,"cancelled").SetProperty(e=>e.ProtectedPayload,"").SetProperty(e=>e.CompletedAt,now));
        await db.SaveChangesAsync();return null;
    }
}
