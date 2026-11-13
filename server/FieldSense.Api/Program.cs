using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using System.Text;
using FieldSense.Api.Data;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command",LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics",LogLevel.Warning);
// HttpClient default logs include query strings containing personal/payment identifiers.
builder.Logging.AddFilter("System.Net.Http.HttpClient",LogLevel.Warning);

builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("FieldSense"));
builder.Services.Configure<GoogleSettings>(builder.Configuration.GetSection("Google"));
builder.Services.AddHttpClient<IGoogleIdentityProvider,GoogleIdentityProvider>(client=>client.Timeout=TimeSpan.FromSeconds(20));
builder.Services.Configure<BillingSettings>(builder.Configuration.GetSection("Billing"));
var settings = builder.Configuration.GetSection("FieldSense").Get<AppSettings>() ?? new AppSettings();

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=fieldsense.db"));

builder.Services.AddRateLimiter(o=> {
    o.RejectionStatusCode=429;
    o.OnRejected=(context,ct)=> {
        var seconds=context.Lease.TryGetMetadata(MetadataName.RetryAfter,out var retry)?Math.Ceiling(retry.TotalSeconds):60;
        context.HttpContext.Response.Headers.RetryAfter=seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApiRateLimit")
            .LogWarning("API rate limit rejected {Endpoint}; retryAfterSeconds={Seconds}",context.HttpContext.GetEndpoint()?.DisplayName,seconds);
        return ValueTask.CompletedTask;
    };
    o.AddPolicy("password-auth",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions {PermitLimit=60,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    o.AddPolicy("signup",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions {PermitLimit=10,Window=TimeSpan.FromMinutes(10),QueueLimit=0}));
});
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<EmailRequestBudget>();
builder.Services.AddScoped<EmailOutbox>();
builder.Services.AddSingleton<IMailTransport,MailTransport>();
builder.Services.AddHostedService<EmailQueueWorker>();
builder.Services.AddScoped<EmailOtpService>();
builder.Services.AddScoped<MembershipService>();
builder.Services.AddScoped<DeviceHealthService>();
builder.Services.AddHostedService<DeviceHealthMonitor>();
builder.Services.AddScoped<BillingService>();
builder.Services.AddHttpClient<IStripeGateway, StripeGateway>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<EzpayBroker>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<PaymentWalletService>();
builder.Services.AddHostedService<EzpayPaymentReconciler>();
builder.Services.AddScoped<JwtTokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = "fieldsense",
            ValidAudience = "fieldsense",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.JwtKey)),
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            NameClaimType = System.Security.Claims.ClaimTypes.NameIdentifier
        };
        opt.MapInboundClaims = false;
        opt.Events=new JwtBearerEvents {OnTokenValidated=async context=> {
            var id=context.Principal?.FindFirst("sub")?.Value;
            if(!Guid.TryParse(id,out var uid)){context.Fail("Invalid account");return;}
            var db=context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var version=await db.Users.Where(u=>u.Id==uid && u.DeletedAt==null && u.SuspendedAt==null).Select(u=>(int?)u.AuthVersion).SingleOrDefaultAsync();
            var supplied=int.TryParse(context.Principal?.FindFirst("auth_version")?.Value,out var parsed)?parsed:0;
            if(version==null || version!=supplied)context.Fail("Session expired");
        }};
    });

builder.Services.AddAuthorization(options => options.AddPolicy("Owner", policy =>
    policy.RequireRole("Admin").RequireClaim("email", settings.AdminEmail.Trim().ToLowerInvariant())));
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Darvazeh", Version = "v2" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme.",
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddCors(opt =>
{
    opt.AddDefaultPolicy(p =>
        p.WithOrigins(
                settings.CorsOrigin.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("Retry-After")
            .AllowCredentials());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await SchemaUpgrade.Apply(db, settings.AdminEmail.Trim());
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { ok = true, service = "darvazeh-api", brand = "دروازه" }));
app.MapGet("/api/public/mobile-config", (IOptions<AppSettings> opt, HttpRequest req) =>
{
    var configured = (opt.Value.PublicApiBase ?? "").Trim().TrimEnd('/');
    var proto = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? req.Scheme;
    var host = req.Headers["X-Forwarded-Host"].FirstOrDefault() ?? req.Host.Value;
    var fallback = $"{proto}://{host}".TrimEnd('/');
    return Results.Ok(new
    {
        apiBase = string.IsNullOrEmpty(configured) ? fallback : configured,
        brand = "دروازه",
        updatedAt = DateTime.UtcNow
    });
});

app.Run();
