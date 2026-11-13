using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using FieldSense.Api.Controllers;
using FieldSense.Api.Data;
using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
class FixtureGoogle : IGoogleIdentityProvider {
 public Dictionary<string,string> Challenges=new();public int Exchanges;
 public string AuthorizationUrl(string state,string nonce,string challenge){Challenges[nonce]=challenge;return "https://accounts.google.com/o/oauth2/v2/auth?state="+Uri.EscapeDataString(state)+"&nonce="+nonce;}
 public Task<GoogleIdentity> Exchange(string code,string verifier,string nonce,CancellationToken ct){
  Exchanges++;
  if(!Challenges.TryGetValue(nonce,out var challenge)||challenge!=Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))))throw new Exception("PKCE mismatch");
  return code switch {
   "new"=>Task.FromResult(new GoogleIdentity("google-new","new@gmail.com",true)),
   "existing"=>Task.FromResult(new GoogleIdentity("google-existing","existing@gmail.com",true)),
   "wrong-sub"=>Task.FromResult(new GoogleIdentity("different-sub","existing@gmail.com",true)),
   "deleted"=>Task.FromResult(new GoogleIdentity("google-deleted","deleted@gmail.com",true)),
   "suspended"=>Task.FromResult(new GoogleIdentity("google-suspended","suspended@gmail.com",true)),
   "admin"=>Task.FromResult(new GoogleIdentity("google-owner","admin@ndorbit.co.nz",true)),
   "untrusted"=>Task.FromResult(new GoogleIdentity("untrusted","someone@example.com",false)),
   _=>throw new SecurityTokenValidationException("fixture rejected token")
  };
 }
}
class Program {
 static void Check(bool condition,string why){if(!condition)throw new Exception(why);}
 static async Task Main(){
  // Actual JWT signature/issuer/audience/expiry/nonce/verified-email validation with private local RSA keys.
  using var rsa=RSA.Create(2048);var key=new RsaSecurityKey(rsa){KeyId="fixture-key"};var config=new OpenIdConnectConfiguration();config.SigningKeys.Add(key);
  var nonce=Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));var audience="fixture.apps.googleusercontent.com";
  string Token(string issuer="https://accounts.google.com",string? aud=null,string? n=null,bool verified=true,DateTime? expiry=null,SecurityKey? signing=null,string email="fixture@gmail.com",string? azp=null) {
   var claims=new List<Claim>{new("sub","subject-1"),new("email",email),new("email_verified",verified?"true":"false",ClaimValueTypes.Boolean),new("nonce",n??nonce)};
   if(azp!=null)claims.Add(new("azp",azp));
   return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer,aud??audience,claims,notBefore:DateTime.UtcNow.AddHours(-2),expires:expiry??DateTime.UtcNow.AddMinutes(5),signingCredentials:new SigningCredentials(signing??key,SecurityAlgorithms.RsaSha256)));
  }
  Check(GoogleIdentityProvider.Validate(Token(),config,audience,nonce).Authoritative,"valid signed identity rejected");
  foreach(var invalid in new[]{Token(issuer:"https://attacker.example"),Token(aud:"other-client"),Token(n:"wrong nonce"),Token(verified:false),Token(expiry:DateTime.UtcNow.AddMinutes(-5)),Token(azp:"other-client")}) {
   try{GoogleIdentityProvider.Validate(invalid,config,audience,nonce);throw new Exception("invalid identity accepted");}catch(SecurityTokenException){}
  }
  using var otherRsa=RSA.Create(2048);var wrongKey=new RsaSecurityKey(otherRsa){KeyId="fixture-key"};
  try{GoogleIdentityProvider.Validate(Token(signing:wrongKey),config,audience,nonce);throw new Exception("invalid signature accepted");}catch(SecurityTokenException){}
  Check(!GoogleIdentityProvider.Validate(Token(email:"external@example.com"),config,audience,nonce).Authoritative,"non-authoritative external email trusted");
  var actual=new GoogleIdentityProvider(new HttpClient(),Options.Create(new GoogleSettings{ClientId=audience,RedirectUri="https://api.example/api/auth/google/callback"}));
  var authUrl=actual.AuthorizationUrl("state","nonce","challenge");var auth=QueryHelpers.ParseQuery(new Uri(authUrl).Query);
  Check(auth["scope"]=="openid email"&&auth["code_challenge_method"]=="S256"&&auth["state"]=="state","authorization parameters wrong");
  Check(PasswordPolicy.Error("n7!Zq2#R","n7!Zq2#R")=="password_length","eight-character password accepted");
  Check(PasswordPolicy.Error("n7!Zq2#Rt","n7!Zq2#Rt")==null,"valid nine-character password rejected");
  Check(PasswordPolicy.Error("a long memorable passphrase","a long memorable passphrase")==null,"passphrase rejected");
  Check(PasswordPolicy.Error("n7!Zq2#Rt",null)=="password_mismatch","confirmation not required");
  foreach(var weak in new[]{"short","Password1!","12345678","abcdabcd"})Check(PasswordPolicy.Error(weak,weak)!=null,"weak password accepted");
  Console.WriteLine("PASS: signed Google JWT validation, wrong issuer/audience/nonce/signature/expiry/azp/email rejection; PKCE request; password policy");
  var directory=Path.Combine(Path.GetTempPath(),"darvazeh-google-test-"+Guid.NewGuid());var resolved=Path.GetFullPath(directory);Check(Path.GetDirectoryName(resolved)==Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))&&Path.GetFileName(resolved).StartsWith("darvazeh-google-test-"),"Unsafe test directory");Directory.CreateDirectory(resolved);var dbPath=Path.Combine(directory,"app.db");
  var settings=new GoogleSettings {Enabled=true,ClientId=audience,ClientSecret="fixture-secret",RedirectUri="https://api.example/api/auth/google/callback",PortalOrigin="https://portal.example"};
  var fixture=new FixtureGoogle();
  var builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Development"});builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();
  builder.Services.AddDbContext<AppDbContext>(o=>o.UseSqlite("Data Source="+dbPath));
  builder.Services.AddSingleton<IOptions<GoogleSettings>>(Options.Create(settings));
  builder.Services.Configure<AppSettings>(o=>{o.JwtKey="local-google-jwt-key-"+new string('x',48);o.AdminEmail="admin@ndorbit.co.nz";});
  builder.Services.AddScoped<JwtTokenService>();builder.Services.AddScoped<MembershipService>();builder.Services.AddSingleton<IGoogleIdentityProvider>(fixture);
  builder.Services.AddControllers().AddApplicationPart(typeof(GoogleAuthController).Assembly);
  builder.Services.AddRateLimiter(o=>o.AddPolicy("password-auth",_=>RateLimitPartition.GetFixedWindowLimiter("fixture",_=>new(){PermitLimit=1000,Window=TimeSpan.FromMinutes(1),QueueLimit=0})));
  var app=builder.Build();app.UseRateLimiter();app.MapControllers();
  Guid existingId,subscriptionId;
  using(var scope=app.Services.CreateScope()){
   var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();await SchemaUpgrade.Apply(db,"admin@ndorbit.co.nz");
   var user=new AppUser{Email="existing@gmail.com",Username="existing.member",EmailVerifiedAt=DateTime.UtcNow};db.Users.Add(user);await new MembershipService(db).StartTrial(user);await db.SaveChangesAsync();
   foreach(var state in new[]{"deleted","suspended"}) {
    var blocked=new AppUser{Email=state+"@gmail.com",EmailVerifiedAt=DateTime.UtcNow,SuspendedAt=DateTime.UtcNow,DeletedAt=state=="deleted"?DateTime.UtcNow:null};
    db.Users.Add(blocked);await new MembershipService(db).StartTrial(blocked);
    var trialKey=MembershipService.TrialKey(blocked.Email);
    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO TrialClaims(Key,UserId) VALUES ({trialKey},{blocked.Id.ToString().ToUpperInvariant()})");
   }
   await db.SaveChangesAsync();
   var sub=await db.Subscriptions.SingleAsync(s=>s.UserId==user.Id);sub.UsedMessages=7;await db.SaveChangesAsync();existingId=user.Id;subscriptionId=sub.Id;
  }
  await app.StartAsync();var baseUrl=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
  using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,CookieContainer=new CookieContainer()}){BaseAddress=new Uri(baseUrl)};
  using var stranger=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,CookieContainer=new CookieContainer()}){BaseAddress=new Uri(baseUrl)};
  async Task<string> Begin(){var response=await client.GetAsync("/api/auth/google/start");Check(response.StatusCode==HttpStatusCode.Redirect,"start failed");Check(response.Headers.GetValues("Set-Cookie").Any(c=>c.Contains("httponly",StringComparison.OrdinalIgnoreCase)&&c.Contains("samesite=lax",StringComparison.OrdinalIgnoreCase)),"correlation cookie not protected");return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString();}
  async Task<HttpResponseMessage> Callback(string state,string code="new",HttpClient? browser=null)=>await (browser??client).GetAsync("/api/auth/google/callback?state="+Uri.EscapeDataString(state)+"&code="+code);
  static object Result(Uri url){var q=QueryHelpers.ParseQuery(url.Fragment.TrimStart('#'));return new{flowId=q["google_flow"].ToString(),code=q["google_code"].ToString()};}
  try {
   settings.Enabled=false;Check((await client.GetAsync("/api/auth/google/start")).StatusCode==HttpStatusCode.ServiceUnavailable,"unconfigured start accepted");settings.Enabled=true;
   app.Environment.EnvironmentName="Production";
   var secureStart=await client.GetAsync("/api/auth/google/start");Check(secureStart.Headers.GetValues("Set-Cookie").Any(c=>c.Contains("secure",StringComparison.OrdinalIgnoreCase)),"production cookie not Secure");
   app.Environment.EnvironmentName="Development";
   var state=await Begin();var noCookie=await Callback(state,browser:stranger);Check(noCookie.Headers.Location!.Fragment.Contains("invalid_state")&&fixture.Exchanges==0,"cookie mismatch reached provider");
   var wrongState=await Callback(state[..32]+"."+new string('x',43));Check(wrongState.Headers.Location!.Fragment.Contains("invalid_state"),"bad state accepted");
   var callback=await Callback(state);Check(callback.Headers.Location!.Fragment.Contains("google_code="),"callback failed");
   Check(!callback.Headers.Location!.ToString().Contains("eyJ"),"JWT leaked into redirect");var result=Result(callback.Headers.Location!);
   Check((await stranger.PostAsJsonAsync("/api/auth/google/exchange",result)).StatusCode==HttpStatusCode.Unauthorized,"cross-browser exchange accepted");
   var exchange=await client.PostAsJsonAsync("/api/auth/google/exchange",result);Check(exchange.StatusCode==HttpStatusCode.OK,"exchange failed");
   var body=JsonDocument.Parse(await exchange.Content.ReadAsStringAsync());Check(body.RootElement.GetProperty("token").GetString()!.Split('.').Length==3,"app token missing");
   Check((await client.PostAsJsonAsync("/api/auth/google/exchange",result)).StatusCode==HttpStatusCode.Unauthorized,"exchange replay accepted");
   Check((await Callback(state)).Headers.Location!.Fragment.Contains("invalid_state"),"callback replay accepted");
   Console.WriteLine("PASS: configured/disabled modes; browser-cookie/state checks; one-use code; callback and exchange replay rejection; no app JWT in URL");
   var existing=await Begin();callback=await Callback(existing,"existing");Check(callback.Headers.Location!.Fragment.Contains("google_code="),"existing account rejected");
   Check((await client.PostAsJsonAsync("/api/auth/google/exchange",Result(callback.Headers.Location!))).StatusCode==HttpStatusCode.OK,"existing exchange failed");
   using(var scope=app.Services.CreateScope()){
    var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();var user=await db.Users.SingleAsync(u=>u.Email=="existing@gmail.com");Check(user.Id==existingId&&user.GoogleSubject=="google-existing","existing account identity changed");
    Check((await db.Subscriptions.SingleAsync(s=>s.Id==subscriptionId)).UsedMessages==7,"existing membership changed");
    Check(await db.Users.CountAsync(u=>u.Email=="new@gmail.com")==1,"duplicate signup");
   }
   var deletedState=await Begin();var recreated=await Callback(deletedState,"deleted");
   Check(recreated.Headers.Location!.Fragment.Contains("google_code="),"deleted identity could not sign up again");
   Check((await client.PostAsJsonAsync("/api/auth/google/exchange",Result(recreated.Headers.Location!))).StatusCode==HttpStatusCode.OK,"recreated account exchange failed");
   using(var scope=app.Services.CreateScope()) {
    var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();var accounts=await db.Users.Where(u=>u.Email=="deleted@gmail.com").ToListAsync();
    Check(accounts.Count==2 && accounts.Count(u=>u.DeletedAt!=null)==1,"old account was restored or duplicate live accounts created");
    var fresh=accounts.Single(u=>u.DeletedAt==null);Check(fresh.EmailVerifiedAt!=null && fresh.GoogleSubject=="google-deleted","fresh identity not verified");
    var access=await new MembershipService(db).Access(fresh.Id);Check(!access.Active && access.MessageLimit==0,"re-registration granted another trial");
    Check(!await db.Messages.AnyAsync(m=>m.UserId==fresh.Id) && !await db.Devices.AnyAsync(d=>d.UserId==fresh.Id),"deleted history/device migrated into new account");
    await SchemaUpgrade.Apply(db,"admin@ndorbit.co.nz");
   }
   var repeat=await Begin();Check((await Callback(repeat,"deleted")).Headers.Location!.Fragment.Contains("google_code="),"repeat re-registration sign-in failed");
   Console.WriteLine("PASS: deleted identity creates a new verified account; old record stays deleted; no second trial/history/device transfer; index migration is repeatable");
   foreach(var (code,error) in new[]{("admin","admin_password_required"),("suspended","account_suspended"),("wrong-sub","account_conflict"),("untrusted","verify_email"),("invalid","signin_failed")}) {
    var next=await Begin();Check((await Callback(next,code)).Headers.Location!.Fragment.Contains(error),"boundary failed: "+code);
   }
   var cancelled=await Begin();var denial=await client.GetAsync("/api/auth/google/callback?state="+Uri.EscapeDataString(cancelled)+"&error=access_denied");Check(denial.Headers.Location!.Fragment.Contains("cancelled"),"denial mishandled");
   var expired=await Begin();using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();var id=Guid.ParseExact(expired.Split('.')[0],"N");await db.GoogleLoginFlows.Where(f=>f.Id==id).ExecuteUpdateAsync(u=>u.SetProperty(f=>f.ExpiresAt,DateTime.UtcNow.AddMinutes(-1)));}
   Check((await Callback(expired)).Headers.Location!.Fragment.Contains("invalid_state"),"expired state accepted");
   var stale=await Begin();callback=await Callback(stale,"existing");using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();await db.Users.Where(u=>u.Id==existingId).ExecuteUpdateAsync(u=>u.SetProperty(u=>u.AuthVersion,u=>u.AuthVersion+1));}
   Check((await client.PostAsJsonAsync("/api/auth/google/exchange",Result(callback.Headers.Location!))).StatusCode==HttpStatusCode.Unauthorized,"reset did not invalidate pending exchange");
   Console.WriteLine("PASS: new trial; existing account/membership preservation; different subject/admin/untrusted email rejection; cancellation/expiry; password-reset boundary");
  }finally {await app.StopAsync();await app.DisposeAsync();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
 }
}
