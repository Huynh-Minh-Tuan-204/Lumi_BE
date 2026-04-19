using Lumi.Application.Interfaces;
using Lumi.Infrastructure.Data;
using Lumi.Infrastructure.Hubs;
using Lumi.Infrastructure.Identity;
using Lumi.Infrastructure.Services;
using Lumi.Domain.Entities;
using Hangfire;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using System.Text;

var builder = WebApplication.CreateBuilder(args);

////////////////////////////////////////////////////
/// 1. DATABASE
////////////////////////////////////////////////////
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlServerOptions => sqlServerOptions.CommandTimeout(180)));

// HANGFIRE
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHangfireServer();

////////////////////////////////////////////////////
/// 2. SERVICES
////////////////////////////////////////////////////
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IReminderService, ReminderService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IUserIdProvider, JwtUserIdProvider>();

////////////////////////////////////////////////////
/// 3. CORS
////////////////////////////////////////////////////
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowVercel", policy =>
    {
        policy.WithOrigins("https://lumi-fe-lime.vercel.app", "http://localhost:3000") // Added explicit origins
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              .SetIsOriginAllowedToAllowWildcardSubdomains()
              .WithExposedHeaders("Content-Disposition");
    });
});

////////////////////////////////////////////////////
/// 4. AUTHENTICATION (JWT)
////////////////////////////////////////////////////
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey =
                new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(builder.Configuration["JwtSettings:Key"])
                )
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) &&
                    (path.StartsWithSegments("/chatHub", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWithSegments("/callhub", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWithSegments("/api/Attachments", StringComparison.OrdinalIgnoreCase)))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

////////////////////////////////////////////////////
/// 5. AUTHORIZATION
////////////////////////////////////////////////////
builder.Services.AddAuthorization();

// ////////////////////////////////////////////////////
/// 6. SIGNALR + CONTROLLERS
////////////////////////////////////////////////////
var signalRBuilder = builder.Services.AddSignalR();
// OPTIONAL: Add Redis for horizontal scaling in production
// signalRBuilder.AddStackExchangeRedis(builder.Configuration.GetConnectionString("RedisConnection") ?? "localhost:6379");

builder.Services.AddControllers();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseHangfireDashboard();

////////////////////////////////////////////////////
/// 7. MIDDLEWARE PIPELINE
////////////////////////////////////////////////////
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                       Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors("AllowVercel");
app.UseAuthentication();
app.UseMiddleware<Lumi.WebAPI.Middleware.FirstLoginMiddleware>();
app.UseAuthorization();

////////////////////////////////////////////////////
/// 8. ENDPOINTS
////////////////////////////////////////////////////
app.MapControllers();
app.MapHub<ChatHub>("/chatHub");
app.MapHub<CallHub>("/callhub");

////////////////////////////////////////////////////
/// 9. STARTUP SCHEME SYNC (RELIABLE VERSION)
////////////////////////////////////////////////////
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    try
    {
        db.Database.SetCommandTimeout(300);
        
        // Step-by-step column addition with NULL to avoid constraint failures in shared hosting
        var cmds = new List<string> {
            "IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[Meetings]') AND name = 'MeetingGuid') ALTER TABLE [Meetings] ADD [MeetingGuid] NVARCHAR(255) NULL",
            "IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[Meetings]') AND name = 'CallType') ALTER TABLE [Meetings] ADD [CallType] NVARCHAR(50) NULL",
            "IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[Meetings]') AND name = 'SettingsJson') ALTER TABLE [Meetings] ADD [SettingsJson] NVARCHAR(MAX) NULL",
            "IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[Messages]') AND name = 'EncryptedContent') ALTER TABLE [Messages] ADD [EncryptedContent] NVARCHAR(MAX) NULL",
            "IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[Messages]') AND name = 'IV') ALTER TABLE [Messages] ADD [IV] NVARCHAR(MAX) NULL",
            "IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[Messages]') AND name = 'Signature') ALTER TABLE [Messages] ADD [Signature] NVARCHAR(MAX) NULL",
            "IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CallRecordings') CREATE TABLE [CallRecordings] (Id INT PRIMARY KEY IDENTITY, MeetingId INT, FilePath NVARCHAR(MAX), EncryptedFilePath NVARCHAR(MAX), IV NVARCHAR(MAX), FileSize BIGINT, CreatedAt DATETIME)"
        };

        foreach(var cmd in cmds) {
            try { db.Database.ExecuteSqlRaw(cmd); } catch { /* Individual failure is okay */ }
        }

        // Fill data for new and old records to maintain consistency
        db.Database.ExecuteSqlRaw("UPDATE [Meetings] SET [MeetingGuid] = CAST(NEWID() AS NVARCHAR(255)) WHERE [MeetingGuid] IS NULL");
        db.Database.ExecuteSqlRaw("UPDATE [Meetings] SET [CallType] = 'video' WHERE [CallType] IS NULL");
        db.Database.ExecuteSqlRaw("UPDATE [Meetings] SET [SettingsJson] = '{}' WHERE [SettingsJson] IS NULL");
    }
    catch { /* Critical startup failure safeguard */ }
}

app.MapGet("/", () => "API is running - Professional Room Code Version");
app.Run();