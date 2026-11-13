using Azure;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
class HeaderResponse(int status, string retry) : Response {
 public override int Status=>status;
 public override string ReasonPhrase=>"fixture";
 public override Stream? ContentStream {get;set;}
 public override string ClientRequestId {get;set;}="fixture";
 public override void Dispose(){}
 protected override bool ContainsHeader(string name)=>name.Equals("Retry-After",StringComparison.OrdinalIgnoreCase);
 protected override bool TryGetHeader(string name,[System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value){value=ContainsHeader(name)?retry:null;return value!=null;}
 protected override bool TryGetHeaderValues(string name,[System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IEnumerable<string>? values){values=ContainsHeader(name)?new[]{retry}:null;return values!=null;}
 protected override IEnumerable<Azure.Core.HttpHeader> EnumerateHeaders()=>new[]{new Azure.Core.HttpHeader("Retry-After",retry)};
}
class Fake : IMailTransport {
 public Dictionary<Guid,int> Starts=new();public Dictionary<Guid,MailProgress> States=new();
 public HashSet<Guid> Throttle=new(),Ambiguous=new(),Reject=new();
 public Dictionary<Guid,int> Checks=new(),StatusThrottles=new();
 public Task<TimeSpan?> Start(Guid id,MailContent c,CancellationToken ct) {
  Starts[id]=Starts.GetValueOrDefault(id)+1;
  if(Throttle.Remove(id))throw new RequestFailedException(429,"fixture throttle");
  if(Reject.Contains(id))throw new RequestFailedException(400,"fixture rejection");
  States[id]=new(false,false);
  if(Ambiguous.Remove(id)){States[id]=new(true,true);throw new TimeoutException("ambiguous fixture");}
  return Task.FromResult<TimeSpan?>(null);
 }
 public Task<MailProgress> Check(Guid id,CancellationToken ct) {
  Checks[id]=Checks.GetValueOrDefault(id)+1;
  if(StatusThrottles.GetValueOrDefault(id)>0){StatusThrottles[id]--;throw new RequestFailedException(new HeaderResponse(429,"120"));}
  if(!States.TryGetValue(id,out var state))throw new RequestFailedException(404,"fixture unknown");
  return Task.FromResult(state);
 }
}
class Clock : TimeProvider { public DateTimeOffset Now=DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>Now; }
class Program {
 static void Check(bool value,string why){if(!value)throw new Exception(why);}
 static async Task Main() {
  var path=Path.Combine(Path.GetTempPath(),"darvazeh-outbox-"+Guid.NewGuid()+".db");var fake=new Fake();
  var clock=new Clock();
  var services=new ServiceCollection().AddSingleton<TimeProvider>(clock).AddScoped<EmailRequestBudget>().AddLogging().Configure<AppSettings>(o=>o.JwtKey="disposable-email-encryption-key-"+new string('x',40))
   .AddDbContext<AppDbContext>(o=>o.UseSqlite("Data Source="+path)).AddScoped<EmailOutbox>().AddSingleton<IMailTransport>(fake).BuildServiceProvider();
  async Task<EmailDispatch> Read(Guid id){using var s=services.CreateScope();return await s.ServiceProvider.GetRequiredService<AppDbContext>().EmailDispatches.AsNoTracking().SingleAsync(x=>x.Id==id);}
  async Task Due(){clock.Now=clock.Now.AddSeconds(40);using var s=services.CreateScope();await s.ServiceProvider.GetRequiredService<AppDbContext>().EmailDispatches.ExecuteUpdateAsync(u=>u.SetProperty(x=>x.NextAttemptAt,DateTime.UtcNow.AddMinutes(-1)));}
  async Task Run(string kind){clock.Now=(clock.Now>DateTimeOffset.UtcNow?clock.Now:DateTimeOffset.UtcNow).AddSeconds(1);using var s=services.CreateScope();await EmailQueueWorker.Process(s.ServiceProvider,kind,CancellationToken.None);}
  Guid id,expired,ambiguous,rejected,subId;
  try {
   using(var scope=services.CreateScope()) {
    var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();await SchemaUpgrade.Apply(db,"fixture@example.com");
    var outbox=scope.ServiceProvider.GetRequiredService<EmailOutbox>();
    var challenge=new OtpChallenge {Email="fixture@example.com",CodeHash="fixture-hash",ExpiresAt=DateTime.UtcNow.AddMinutes(10)};db.OtpChallenges.Add(challenge);
    outbox.Enqueue("otp",new("fixture@example.com","OTP","sensitive-code-654321","private html"),Guid.Empty,challenge:challenge);
    await db.SaveChangesAsync();var row=await db.EmailDispatches.SingleAsync();id=row.Id;
    Check(!row.ProtectedPayload.Contains("654321"),"OTP is not encrypted");Check(outbox.Unprotect(row.ProtectedPayload).Text=="sensitive-code-654321","payload recovery");
    fake.Throttle.Add(id);
   }
   await Run("otp");Check((await Read(id)).Status=="submitting" && fake.Starts[id]==1,"throttled message lost");
   await Due();await Run("otp");Check((await Read(id)).Status=="accepted" && (await Read(id)).ProtectedPayload=="","accepted payload not removed");
   Check(fake.Checks.GetValueOrDefault(id)==1,"unexpected immediate/duplicate status check");
   await Due();await Run("otp");Check(fake.Starts[id]==2,"polling resubmitted an accepted message");
   fake.States[id]=new(true,true);await Due();await Run("otp");Check((await Read(id)).Status=="submitted","submission completion missing");
   Console.WriteLine("PASS: encrypted durable payload; 429 retry; stable operation ID; accepted-status polling without resend; terminal payload deletion");
   using(var scope=services.CreateScope()) {
    var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();var outbox=scope.ServiceProvider.GetRequiredService<EmailOutbox>();
    var c=new OtpChallenge{Email="fixture@example.com",CodeHash="fixture-hash",ExpiresAt=DateTime.UtcNow.AddMinutes(-1)};db.OtpChallenges.Add(c);
    outbox.Enqueue("otp",new("fixture@example.com","expired","old code",""),Guid.Empty,challenge:c);await db.SaveChangesAsync();expired=(await db.EmailDispatches.SingleAsync(x=>x.ChallengeId==c.Id)).Id;
   }
   await Run("otp");Check((await Read(expired)).Status=="expired" && !fake.Starts.ContainsKey(expired),"expired OTP sent");
   using(var scope=services.CreateScope()) {
    var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();var outbox=scope.ServiceProvider.GetRequiredService<EmailOutbox>();
    outbox.Enqueue("otp",new("fixture@example.com","ambiguous","private code",""),Guid.Empty);await db.SaveChangesAsync();ambiguous=(await db.EmailDispatches.OrderByDescending(x=>x.CreatedAt).FirstAsync()).Id;fake.Ambiguous.Add(ambiguous);
   }
   await Run("otp");await Due();await Run("otp");Check((await Read(ambiguous)).Status=="submitted" && fake.Starts[ambiguous]==1,"ambiguous submission duplicated");
   Console.WriteLine("PASS: expired OTP not sent; ambiguous timeout recovers by operation lookup rather than duplicate sending");
   using(var scope=services.CreateScope()) {
    var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();var outbox=scope.ServiceProvider.GetRequiredService<EmailOutbox>();
    var user=await db.Users.FirstAsync();var sub=await db.Subscriptions.FirstAsync();sub.UsedEmailForwards=1;subId=sub.Id;
    var message=new SmsMessage {UserId=user.Id,EmailForwardStatus="queued"};db.Messages.Add(message);
    outbox.Enqueue("forward",new(user.Email,"SMS","private SMS",""),user.Id,message.Id,sub.Id);await db.SaveChangesAsync();rejected=(await db.EmailDispatches.SingleAsync(x=>x.MessageId==message.Id)).Id;fake.Reject.Add(rejected);
   }
   await Run("forward");Check((await Read(rejected)).Status=="failed","rejection not terminal");
   using(var scope=services.CreateScope()) {var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();Check((await db.Subscriptions.SingleAsync(x=>x.Id==subId)).UsedEmailForwards==0,"email reservation not released");}
   await Due();await Run("forward");Check(fake.Starts[rejected]==1,"terminal failure retried");
   Console.WriteLine("PASS: permanent rejection releases forwarding reservation exactly once; status persists across scopes");

   Guid throttled,waiting;
   using(var scope=services.CreateScope()) {
    var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();var outbox=scope.ServiceProvider.GetRequiredService<EmailOutbox>();
    outbox.Enqueue("otp",new("fixture@example.com","status throttle","private",""),Guid.Empty);await db.SaveChangesAsync();
    throttled=(await db.EmailDispatches.OrderByDescending(x=>x.CreatedAt).FirstAsync()).Id;
   }
   await Due();await Run("otp");Check(fake.Starts[throttled]==1 && fake.Checks.GetValueOrDefault(throttled)==0,"send should not immediately poll");
   fake.StatusThrottles[throttled]=2;
   await Due();await Run("otp");var first=await Read(throttled);
   Check(first.ConsecutiveFailures==1 && first.ErrorCode=="provider_http_429" && first.NextAttemptAt>=clock.Now.UtcDateTime.AddSeconds(120),"status Retry-After ignored");
   using(var scope=services.CreateScope()) {
    var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();var outbox=scope.ServiceProvider.GetRequiredService<EmailOutbox>();
    outbox.Enqueue("forward",new("fixture@example.com","cooldown","private",""),Guid.Empty);await db.SaveChangesAsync();waiting=(await db.EmailDispatches.OrderByDescending(x=>x.CreatedAt).FirstAsync()).Id;
   }
   await Run("forward");Check(!fake.Starts.ContainsKey(waiting),"status throttling did not block the other queue");
   await Run("otp");Check(fake.Checks[throttled]==1,"status retried before cooldown");
   clock.Now=new DateTimeOffset(first.NextAttemptAt,TimeSpan.Zero).AddSeconds(1);
   await Run("otp");var second=await Read(throttled);Check(second.ConsecutiveFailures==2 && second.PollAttempts==2,"status failures not persisted/increased");
   clock.Now=new DateTimeOffset(second.NextAttemptAt,TimeSpan.Zero).AddSeconds(1);fake.States[throttled]=new(true,true);
   await Run("otp");Check((await Read(throttled)).Status=="submitted" && fake.Starts[throttled]==1,"throttled accepted mail resent");
   Check(EmailRequestBudget.FailureDelay(3)>EmailRequestBudget.FailureDelay(2),"failure backoff does not grow");
   Check(EmailRequestBudget.PollDelay(4)>=TimeSpan.FromSeconds(120),"running operations do not slow down");
   Check(EmailRequestBudget.RetryAfter(new HeaderResponse(200,"180"),clock.Now.UtcDateTime)==TimeSpan.FromSeconds(180),"delta Retry-After parse");
   var date=clock.Now.UtcDateTime.AddMinutes(5).ToString("R");Check(EmailRequestBudget.RetryAfter(new HeaderResponse(200,date),clock.Now.UtcDateTime)>TimeSpan.FromSeconds(298),"date Retry-After parse");
   Check(EmailRequestBudget.RetryAfter(new HeaderResponse(200,"bad"),clock.Now.UtcDateTime)==null,"malformed Retry-After");
   Console.WriteLine("PASS: status 429 Retry-After; persisted failure count; cross-queue cooldown; no early retry or duplicate sends; adaptive backoff; header parsing");
   async Task<DateTime?> Reserve(string operation){using var scope=services.CreateScope();return await scope.ServiceProvider.GetRequiredService<EmailRequestBudget>().Reserve(operation,CancellationToken.None);}
   using(var scope=services.CreateScope()) {
    var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();await db.EmailProviderCalls.ExecuteDeleteAsync();await db.EmailProviderControls.ExecuteUpdateAsync(u=>u.SetProperty(x=>x.CooldownUntil,DateTime.MinValue));
    await SchemaUpgrade.Apply(db,"fixture@example.com");
   }
   for(var i=0;i<25;i++){Check(await Reserve("send")==null,"send budget rejected valid slot");clock.Now=clock.Now.AddSeconds(2);}
   Check(await Reserve("send")>clock.Now.UtcDateTime,"send minute budget exceeded");
   clock.Now=clock.Now.AddMinutes(2);
   for(var i=0;i<180;i++){Check(await Reserve("status")==null,"status budget rejected valid slot");clock.Now=clock.Now.AddSeconds(2);}
   var blockedUntil=await Reserve("status");Check(blockedUntil>clock.Now.UtcDateTime.AddMinutes(50),"hourly status limit not enforced across new scopes");
   Check(await Reserve("status")==blockedUntil,"budget did not survive new service instance");
   clock.Now=new DateTimeOffset(blockedUntil!.Value,TimeSpan.Zero).AddSeconds(1);Check(await Reserve("status")==null,"rolling window did not recover");
   clock.Now=clock.Now.AddSeconds(3);
   var concurrent=await Task.WhenAll(Reserve("status"),Reserve("status"));Check(concurrent.Count(x=>x==null)==1,"concurrent queue reservations exceeded minimum spacing");
   Console.WriteLine("PASS: rolling send/minute and status/hour budgets; durable reservations; schema idempotence; hour-window recovery");
  } finally {await services.DisposeAsync();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
 }
}
