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
public record MailProgress(bool Completed,bool Succeeded,TimeSpan? RetryAfter=null);
public interface IMailTransport {
    Task<TimeSpan?> Start(Guid id,MailContent content,CancellationToken ct);
    Task<MailProgress> Check(Guid id,CancellationToken ct);
}
public class MailTransport(IOptions<AppSettings> options) : IMailTransport {
    private readonly Lazy<EmailClient> client=new(()=> {
        var opt=new EmailClientOptions();opt.Diagnostics.IsLoggingContentEnabled=false;opt.Retry.MaxRetries=0;opt.Retry.Delay=TimeSpan.FromSeconds(1);opt.Retry.MaxDelay=TimeSpan.FromSeconds(5);opt.Retry.NetworkTimeout=TimeSpan.FromSeconds(15);
        return new EmailClient(options.Value.AcsConnectionString,opt);
    });
    public async Task<TimeSpan?> Start(Guid id,MailContent content,CancellationToken ct) {
        var operation=await client.Value.SendAsync(WaitUntil.Started,new EmailMessage(senderAddress:options.Value.AcsSenderAddress,
            content:new EmailContent(content.Subject){PlainText=content.Text,Html=content.Html},recipients:new EmailRecipients(new[]{new EmailAddress(content.To)})),id,ct);
        return EmailRequestBudget.RetryAfter(operation.GetRawResponse(),DateTime.UtcNow);
    }
    public async Task<MailProgress> Check(Guid id,CancellationToken ct) {
        var operation=new EmailSendOperation(id.ToString(),client.Value);var response=await operation.UpdateStatusAsync(ct);
        return new(operation.HasCompleted,operation.HasCompleted && operation.HasValue && operation.Value.Status==EmailSendStatus.Succeeded,EmailRequestBudget.RetryAfter(response,DateTime.UtcNow));
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
        var failures=0;
        while(!ct.IsCancellationRequested) {
            var delay=TimeSpan.FromSeconds(2);
            try {using var scope=scopes.CreateScope();await Process(scope.ServiceProvider,kind,ct);failures=0;}
            catch(Exception e) when(!ct.IsCancellationRequested){delay=EmailRequestBudget.FailureDelay(++failures);logger.LogWarning("Email worker retry: {Type}; delaySeconds={Delay}",e.GetType().Name,delay.TotalSeconds);}
            try{await Task.Delay(delay,ct);}catch(OperationCanceledException){break;}
        }
    }
    public static async Task Process(IServiceProvider services,string kind,CancellationToken ct) {
        var db=services.GetRequiredService<AppDbContext>();var clock=services.GetRequiredService<TimeProvider>();
        var budget=services.GetRequiredService<EmailRequestBudget>();var now=clock.GetUtcNow().UtcDateTime;
        var row=await db.EmailDispatches.AsNoTracking().Where(e=>e.Kind==kind && (e.Status=="queued" || e.Status=="submitting" || e.Status=="accepted") && e.NextAttemptAt<=now)
            .OrderBy(e=>e.NextAttemptAt).ThenBy(e=>e.CreatedAt).FirstOrDefaultAsync(ct);
        if(row==null)return;
        if(await db.EmailDispatches.Where(e=>e.Id==row.Id && e.NextAttemptAt<=now).ExecuteUpdateAsync(u=>u.SetProperty(e=>e.NextAttemptAt,now.AddSeconds(45)),ct)!=1)return;
        row=await db.EmailDispatches.SingleAsync(e=>e.Id==row.Id,ct);
        var transport=services.GetRequiredService<IMailTransport>();var logger=services.GetRequiredService<ILogger<EmailQueueWorker>>();
        async Task Finish(string status,bool release) {
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            row.Status=status;row.CompletedAt=clock.GetUtcNow().UtcDateTime;row.ProtectedPayload="";
            if(row.MessageId!=null) {
                var message=await db.Messages.SingleOrDefaultAsync(m=>m.Id==row.MessageId,ct);
                if(message!=null){message.EmailForwardStatus=status;message.EmailForwarded=status=="submitted";}
                if(release && row.SubscriptionId!=null)await db.Subscriptions.Where(s=>s.Id==row.SubscriptionId && s.UsedEmailForwards>0)
                    .ExecuteUpdateAsync(u=>u.SetProperty(s=>s.UsedEmailForwards,s=>s.UsedEmailForwards-1),ct);
            }
            if(status!="submitted" && row.ChallengeId!=null)await db.OtpChallenges.Where(c=>c.Id==row.ChallengeId).ExecuteUpdateAsync(u=>u.SetProperty(c=>c.Consumed,true),ct);
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
            logger.LogInformation("Email {Dispatch} {Kind} {Status}; elapsedMs={Elapsed}",row.Id,row.Kind,row.Status,(clock.GetUtcNow().UtcDateTime-row.CreatedAt).TotalMilliseconds);
        }
        if(row.UserId!=Guid.Empty && await db.Users.AnyAsync(u=>u.Id==row.UserId && (u.DeletedAt!=null || u.SuspendedAt!=null),ct)){await Finish("cancelled",!row.ProviderStarted&&row.Attempts==0);return;}
        if(row.ExpiresAt<=now || (row.ChallengeId!=null && await db.OtpChallenges.AnyAsync(c=>c.Id==row.ChallengeId && c.Consumed,ct))) {
            await Finish(row.ProviderStarted||row.Attempts>0?"unknown":"expired",!row.ProviderStarted&&row.Attempts==0);return;
        }
        DateTime BeforeExpiry(DateTime due)=>due<row.ExpiresAt?due:row.ExpiresAt;
        var operationName="status";
        async Task<bool> Allowed(string operation) {
            operationName=operation;
            var due=await budget.Reserve(operation,ct);
            if(due==null)return true;
            row.NextAttemptAt=BeforeExpiry(due.Value.AddMilliseconds(Random.Shared.Next(100,1000)));
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Email {Dispatch} {Kind} deferred; operation={Operation}; reason=shared_budget_or_cooldown; nextAt={NextAt}",row.Id,kind,operation,row.NextAttemptAt);
            return false;
        }
        async Task<MailProgress> Poll() {
            row.PollAttempts++;
            await db.SaveChangesAsync(ct);
            var timer=Stopwatch.StartNew();
            var progress=await transport.Check(row.Id,ct);
            row.ConsecutiveFailures=0;row.ErrorCode=null;
            logger.LogInformation("Email {Dispatch} {Kind} status checked; poll={Poll}; completed={Completed}; succeeded={Succeeded}; elapsedMs={Elapsed}; retryAfterSeconds={Retry}",
                row.Id,kind,row.PollAttempts,progress.Completed,progress.Succeeded,timer.ElapsedMilliseconds,progress.RetryAfter?.TotalSeconds);
            return progress;
        }
        void Pending(TimeSpan? hint) {
            row.Status="accepted";row.ProtectedPayload="";row.ErrorCode=null;row.ConsecutiveFailures=0;
            row.NextAttemptAt=BeforeExpiry(clock.GetUtcNow().UtcDateTime+EmailRequestBudget.PollDelay(row.PollAttempts,hint));
        }
        try {
            if(!row.ProviderStarted) {
                // Recover ambiguous submission with the same operation ID, never send again after acceptance.
                if(row.Attempts>0) {
                    if(!await Allowed("status"))return;
                    try {
                        var progress=await Poll();row.ProviderStarted=true;row.AcceptedAt??=clock.GetUtcNow().UtcDateTime;
                        if(progress.Completed){await Finish(progress.Succeeded?"submitted":"failed",!progress.Succeeded);return;}
                        Pending(progress.RetryAfter);await db.SaveChangesAsync(ct);return;
                    }
                    catch(RequestFailedException e) when(e.Status==404) { }
                }
                if(!await Allowed("send"))return;
                row.Attempts++;row.Status="submitting";await db.SaveChangesAsync(ct);
                var timer=Stopwatch.StartNew();
                var hint=await transport.Start(row.Id,services.GetRequiredService<EmailOutbox>().Unprotect(row.ProtectedPayload),ct);
                row.ProviderStarted=true;row.AcceptedAt=clock.GetUtcNow().UtcDateTime;
                logger.LogInformation("Email {Dispatch} {Kind} accepted; queueMs={Queue}; submitMs={Submit}; retryAfterSeconds={Retry}",row.Id,row.Kind,(row.AcceptedAt.Value-row.CreatedAt).TotalMilliseconds,timer.ElapsedMilliseconds,hint?.TotalSeconds);
                // Do not immediately poll after submitting: allow the provider time to process.
                Pending(hint);
            } else {
                if(!await Allowed("status"))return;
                var status=await Poll();
                if(status.Completed){await Finish(status.Succeeded?"submitted":"failed",!status.Succeeded);return;}
                Pending(status.RetryAfter);
            }
        }
        catch(RequestFailedException e) {
            row.ErrorCode="provider_http_"+e.Status;row.ConsecutiveFailures++;
            var hint=EmailRequestBudget.RetryAfter(e.GetRawResponse(),clock.GetUtcNow().UtcDateTime);
            var delay=EmailRequestBudget.FailureDelay(row.ConsecutiveFailures,hint);
            var retryAt=clock.GetUtcNow().UtcDateTime+delay;
            row.NextAttemptAt=BeforeExpiry(retryAt);
            // Status endpoint 404 after acceptance is transient, not proof delivery failed.
            if(e.Status is 429 or 503)await budget.Cooldown(retryAt,ct);
            logger.LogWarning("Email {Dispatch} {Kind} provider HTTP {Status}; operation={Operation}; sendAttempts={Attempt}; polls={Polls}; consecutiveFailures={Failures}; retryAfterSeconds={Retry}; nextAt={NextAt}; providerCode={Code}",
                row.Id,row.Kind,e.Status,operationName,row.Attempts,row.PollAttempts,row.ConsecutiveFailures,hint?.TotalSeconds,row.NextAttemptAt,SafeCode(e.ErrorCode));
            if(e.Status is >=400 and <500 && e.Status is not (404 or 408 or 409 or 429)) {await Finish("failed",true);return;}
        }
        catch(Exception e) when(!ct.IsCancellationRequested) {
            row.ErrorCode=e is CryptographicException?"payload_unavailable":"transport_uncertain";row.ConsecutiveFailures++;
            logger.LogWarning("Email {Dispatch} {Kind} will retry; operation={Operation}; exceptionType={Type}; consecutiveFailures={Failures}",row.Id,row.Kind,operationName,e.GetType().Name,row.ConsecutiveFailures);
            if(e is CryptographicException){await Finish("failed",true);return;}
            row.NextAttemptAt=BeforeExpiry(clock.GetUtcNow().UtcDateTime+EmailRequestBudget.FailureDelay(row.ConsecutiveFailures));
        }
        await db.SaveChangesAsync(ct);
    }
    private static string? SafeCode(string? code)=>code!=null&&code.Length<=80&&code.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_')?code:null;
}
