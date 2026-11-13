using Azure;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
class Fake : IMailTransport {
 public Dictionary<Guid,int> Starts=new();public Dictionary<Guid,MailProgress> States=new();
 public HashSet<Guid> Throttle=new(),Ambiguous=new(),Reject=new();
 public Task Start(Guid id,MailContent c,CancellationToken ct) {
  Starts[id]=Starts.GetValueOrDefault(id)+1;
  if(Throttle.Remove(id))throw new RequestFailedException(429,"fixture throttle");
  if(Reject.Contains(id))throw new RequestFailedException(400,"fixture rejection");
  States[id]=new(false,false);
  if(Ambiguous.Remove(id)){States[id]=new(true,true);throw new TimeoutException("ambiguous fixture");}
  return Task.CompletedTask;
 }
 public Task<MailProgress> Check(Guid id,CancellationToken ct) {
  if(!States.TryGetValue(id,out var state))throw new RequestFailedException(404,"fixture unknown");
  return Task.FromResult(state);
 }
}
class Program {
 static void Check(bool value,string why){if(!value)throw new Exception(why);}
 static async Task Main() {
  var path=Path.Combine(Path.GetTempPath(),"darvazeh-outbox-"+Guid.NewGuid()+".db");var fake=new Fake();
  var services=new ServiceCollection().AddLogging().Configure<AppSettings>(o=>o.JwtKey="disposable-email-encryption-key-"+new string('x',40))
   .AddDbContext<AppDbContext>(o=>o.UseSqlite("Data Source="+path)).AddScoped<EmailOutbox>().AddSingleton<IMailTransport>(fake).BuildServiceProvider();
  async Task<EmailDispatch> Read(Guid id){using var s=services.CreateScope();return await s.ServiceProvider.GetRequiredService<AppDbContext>().EmailDispatches.AsNoTracking().SingleAsync(x=>x.Id==id);}
  async Task Due(){using var s=services.CreateScope();await s.ServiceProvider.GetRequiredService<AppDbContext>().EmailDispatches.ExecuteUpdateAsync(u=>u.SetProperty(x=>x.NextAttemptAt,DateTime.UtcNow.AddMinutes(-1)));}
  async Task Run(string kind){using var s=services.CreateScope();await EmailQueueWorker.Process(s.ServiceProvider,kind,CancellationToken.None);}
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
  } finally {await services.DisposeAsync();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);}
 }
}
