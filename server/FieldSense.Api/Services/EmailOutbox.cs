using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Communication.Email;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace FieldSense.Api.Services;
public record MailContent(string To,string Subject,string Text,string Html);
public record MailProgress(bool Completed,bool Succeeded);
public interface IMailTransport {
    Task Start(Guid id,MailContent content,CancellationToken ct);
    Task<MailProgress> Check(Guid id,CancellationToken ct);
}
public class MailTransport(IOptions<AppSettings> options) : IMailTransport {
    private readonly Lazy<EmailClient> client=new(()=> {
        var opt=new EmailClientOptions();opt.Retry.MaxRetries=2;opt.Retry.Delay=TimeSpan.FromSeconds(1);opt.Retry.MaxDelay=TimeSpan.FromSeconds(5);opt.Retry.NetworkTimeout=TimeSpan.FromSeconds(15);
        return new EmailClient(options.Value.AcsConnectionString,opt);
    });
    public async Task Start(Guid id,MailContent content,CancellationToken ct) {
        await client.Value.SendAsync(WaitUntil.Started,new EmailMessage(senderAddress:options.Value.AcsSenderAddress,
            content:new EmailContent(content.Subject){PlainText=content.Text,Html=content.Html},recipients:new EmailRecipients(new[]{new EmailAddress(content.To)})),id,ct);
    }
    public async Task<MailProgress> Check(Guid id,CancellationToken ct) {
        var operation=new EmailSendOperation(id.ToString(),client.Value);await operation.UpdateStatusAsync(ct);
        return new(operation.HasCompleted,operation.HasCompleted && operation.HasValue && operation.Value.Status==EmailSendStatus.Succeeded);
    }
}
public class EmailOutbox(AppDbContext db,IOptions<AppSettings> options) {
    private byte[] Key=>SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.JwtKey+"|darvazeh-mail-outbox-v1"));
    public string Protect(MailContent content) {
        var plain=JsonSerializer.SerializeToUtf8Bytes(content);var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];var cipher=new byte[plain.Length];
        using var aes=new AesGcm(Key,16);aes.Encrypt(nonce,plain,cipher,tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }
    public MailContent Unprotect(string payload) {
        var data=Convert.FromBase64String(payload);var plain=new byte[data.Length-28];using var aes=new AesGcm(Key,16);
        aes.Decrypt(data.AsSpan(0,12),data.AsSpan(28),data.AsSpan(12,16),plain);return JsonSerializer.Deserialize<MailContent>(plain)!;
    }
    public void Enqueue(string kind,MailContent content,Guid userId,Guid? messageId=null,Guid? subscriptionId=null,OtpChallenge? challenge=null) {
        db.EmailDispatches.Add(new EmailDispatch {Kind=kind,ProtectedPayload=Protect(content),UserId=userId,MessageId=messageId,
            SubscriptionId=subscriptionId,ChallengeId=challenge?.Id,ExpiresAt=challenge?.ExpiresAt??DateTime.UtcNow.AddHours(1)});
    }
}
public class EmailQueueWorker(IServiceScopeFactory scopes,ILogger<EmailQueueWorker> logger) : BackgroundService {
    protected override Task ExecuteAsync(CancellationToken ct)=>Task.WhenAll(Loop("otp",ct),Loop("forward",ct));
    private async Task Loop(string kind,CancellationToken ct) {
        while(!ct.IsCancellationRequested) {
            try {using var scope=scopes.CreateScope();await Process(scope.ServiceProvider,kind,ct);}
            catch(Exception e) when(!ct.IsCancellationRequested){logger.LogWarning("Email worker retry: {Type}",e.GetType().Name);}
            try{await Task.Delay(1000,ct);}catch(OperationCanceledException){break;}
        }
    }
    public static async Task Process(IServiceProvider services,string kind,CancellationToken ct) {
        var db=services.GetRequiredService<AppDbContext>();var now=DateTime.UtcNow;
        var row=await db.EmailDispatches.AsNoTracking().Where(e=>e.Kind==kind && (e.Status=="queued" || e.Status=="submitting" || e.Status=="accepted") && e.NextAttemptAt<=now)
            .OrderBy(e=>e.CreatedAt).FirstOrDefaultAsync(ct);
        if(row==null)return;
        if(await db.EmailDispatches.Where(e=>e.Id==row.Id && e.NextAttemptAt<=now).ExecuteUpdateAsync(u=>u.SetProperty(e=>e.NextAttemptAt,now.AddSeconds(45)),ct)!=1)return;
        row=await db.EmailDispatches.SingleAsync(e=>e.Id==row.Id,ct);
        var transport=services.GetRequiredService<IMailTransport>();var logger=services.GetRequiredService<ILogger<EmailQueueWorker>>();
        async Task Finish(string status,bool release) {
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            row.Status=status;row.CompletedAt=DateTime.UtcNow;row.ProtectedPayload="";
            if(row.MessageId!=null) {
                var message=await db.Messages.SingleOrDefaultAsync(m=>m.Id==row.MessageId,ct);
                if(message!=null){message.EmailForwardStatus=status;message.EmailForwarded=status=="submitted";}
                if(release && row.SubscriptionId!=null)await db.Subscriptions.Where(s=>s.Id==row.SubscriptionId && s.UsedEmailForwards>0)
                    .ExecuteUpdateAsync(u=>u.SetProperty(s=>s.UsedEmailForwards,s=>s.UsedEmailForwards-1),ct);
            }
            if(status!="submitted" && row.ChallengeId!=null)await db.OtpChallenges.Where(c=>c.Id==row.ChallengeId).ExecuteUpdateAsync(u=>u.SetProperty(c=>c.Consumed,true),ct);
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
            logger.LogInformation("Email {Dispatch} {Kind} {Status}; elapsedMs={Elapsed}",row.Id,row.Kind,row.Status,(DateTime.UtcNow-row.CreatedAt).TotalMilliseconds);
        }
        if(row.UserId!=Guid.Empty && await db.Users.AnyAsync(u=>u.Id==row.UserId && (u.DeletedAt!=null || u.SuspendedAt!=null),ct)){await Finish("cancelled",!row.ProviderStarted&&row.Attempts==0);return;}
        if(row.ExpiresAt<=now || (row.ChallengeId!=null && await db.OtpChallenges.AnyAsync(c=>c.Id==row.ChallengeId && c.Consumed,ct))) {
            await Finish(row.ProviderStarted||row.Attempts>0?"unknown":"expired",!row.ProviderStarted&&row.Attempts==0);return;
        }
        try {
            if(!row.ProviderStarted) {
                // Check a previous ambiguous send before resubmitting with the same operation ID.
                if(row.Attempts>0) {
                    try {var progress=await transport.Check(row.Id,ct);row.ProviderStarted=true;row.AcceptedAt??=DateTime.UtcNow;
                        if(progress.Completed){await Finish(progress.Succeeded?"submitted":"failed",!progress.Succeeded);return;}}
                    catch(RequestFailedException e) when(e.Status==404) { }
                }
                if(!row.ProviderStarted) {
                    row.Attempts++;row.Status="submitting";await db.SaveChangesAsync(ct);
                    var timer=Stopwatch.StartNew();await transport.Start(row.Id,services.GetRequiredService<EmailOutbox>().Unprotect(row.ProtectedPayload),ct);
                    row.ProviderStarted=true;row.AcceptedAt=DateTime.UtcNow;
                    logger.LogInformation("Email {Dispatch} {Kind} accepted; queueMs={Queue}; submitMs={Submit}",row.Id,row.Kind,(row.AcceptedAt.Value-row.CreatedAt).TotalMilliseconds,timer.ElapsedMilliseconds);
                }
                row.Status="accepted";row.ProtectedPayload="";
            }
            var status=await transport.Check(row.Id,ct);
            if(status.Completed){await Finish(status.Succeeded?"submitted":"failed",!status.Succeeded);return;}
            row.Status="accepted";row.NextAttemptAt=DateTime.UtcNow.AddSeconds(5);row.ErrorCode=null;
        }
        catch(RequestFailedException e) {
            row.ErrorCode="provider_http_"+e.Status;
            logger.LogWarning("Email {Dispatch} {Kind} provider HTTP {Status}; attempt={Attempt}",row.Id,row.Kind,e.Status,row.Attempts);
            if(e.Status is >=400 and <500 && e.Status is not (408 or 409 or 429)) {await Finish("failed",true);return;}
            row.NextAttemptAt=DateTime.UtcNow.AddSeconds(Math.Min(60,Math.Pow(2,Math.Min(5,row.Attempts))*2));
        }
        catch(Exception e) when(!ct.IsCancellationRequested) {
            row.ErrorCode=e is CryptographicException?"payload_unavailable":"transport_uncertain";
            logger.LogWarning("Email {Dispatch} {Kind} will retry: {Type}",row.Id,row.Kind,e.GetType().Name);
            if(e is CryptographicException){await Finish("failed",true);return;}
            row.NextAttemptAt=DateTime.UtcNow.AddSeconds(15);
        }
        await db.SaveChangesAsync(ct);
    }
}
