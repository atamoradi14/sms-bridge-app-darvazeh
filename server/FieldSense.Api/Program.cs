using System.Text;
using FieldSense.Api.Data;
using FieldSense.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("FieldSense"));
var settings = builder.Configuration.GetSection("FieldSense").Get<AppSettings>() ?? new AppSettings();

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=fieldsense.db"));

builder.Services.AddScoped<EmailOtpService>();
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
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "FieldSense API", Version = "v2" });
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
            .AllowAnyMethod());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    // Lightweight SQLite column add for existing DBs (EnsureCreated does not migrate).
    foreach (var sql in new[]
             {
                 "ALTER TABLE Devices ADD COLUMN InstallationId TEXT NULL",
                 "ALTER TABLE Devices ADD COLUMN LastIpAddress TEXT NULL",
                 "ALTER TABLE Devices ADD COLUMN Platform TEXT NULL",
                 "ALTER TABLE Devices ADD COLUMN DeviceModel TEXT NULL",
                 "ALTER TABLE Devices ADD COLUMN AppVersion TEXT NULL"
             })
    {
        try { db.Database.ExecuteSqlRaw(sql); }
        catch { /* column already exists */ }
    }
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { ok = true, service = "FieldSense.Api" }));
app.MapGet("/api/public/mobile-config", (IOptions<AppSettings> opt, HttpRequest req) =>
{
    var configured = (opt.Value.PublicApiBase ?? "").Trim().TrimEnd('/');
    var proto = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? req.Scheme;
    var host = req.Headers["X-Forwarded-Host"].FirstOrDefault() ?? req.Host.Value;
    var fallback = $"{proto}://{host}".TrimEnd('/');
    return Results.Ok(new
    {
        apiBase = string.IsNullOrEmpty(configured) ? fallback : configured,
        brand = "Darvazeh",
        updatedAt = DateTime.UtcNow
    });
});

app.Run();
