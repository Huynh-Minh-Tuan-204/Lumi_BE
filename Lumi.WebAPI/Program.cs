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
        sqlServerOptions => sqlServerOptions.CommandTimeout(60)));

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
        policy.SetIsOriginAllowed(origin => true) // Cho phép tất cả origin
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin => true) // Cho phép tất cả origin
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
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

        // Cho phép SignalR nhận token qua query string
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

////////////////////////////////////////////////////
/// 6. SIGNALR + CONTROLLERS
////////////////////////////////////////////////////
builder.Services.AddSignalR();
builder.Services.AddControllers();

var app = builder.Build();

// DI CHUYỂN CORS LÊN TUYỆT ĐỐI ĐẦU PIPELINE
app.UseCors("AllowVercel"); 

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
// OPTIONS Handling for CORS (SmarterASP/Proxy robustness)
// CORS will be applied after Routing below

// HTTPS
app.UseHttpsRedirection();

// Static Files - Nằm TRƯỚC Routing để fix lỗi 404 cho ảnh và avatar
app.UseStaticFiles();

// Routing
app.UseRouting();

// CORS is now at the top

// Authentication
app.UseAuthentication();

// Middleware: Check First Login after Authentication
app.UseMiddleware<Lumi.WebAPI.Middleware.FirstLoginMiddleware>();

// Authorization
app.UseAuthorization();

////////////////////////////////////////////////////
/// 8. ENDPOINTS
////////////////////////////////////////////////////
app.MapControllers();

app.MapHub<ChatHub>("/chatHub");
app.MapHub<CallHub>("/callhub");

////////////////////////////////////////////////////
/// 9. TẠO ADMIN MẶC ĐỊNH
////////////////////////////////////////////////////
/*
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var pwdService = scope.ServiceProvider.GetRequiredService<IPasswordService>();

    // Set a longer timeout for migrations (2 minutes)
    db.Database.SetCommandTimeout(120);

    if (app.Environment.IsDevelopment())
    {
        try
        {
            Console.WriteLine("Checking database migrations...");
            db.Database.Migrate();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Migration failed: {ex.Message}. Continuing startup...");
        }
    }

    // Reset online status on startup (Clean cleanup)
    try 
    {
        //db.Database.ExecuteSqlRaw("TRUNCATE TABLE SignalRConnections");
    }
    catch { /* Ignore if DB is not ready or doesn't support truncate  }

    if (!db.Users.Any(u => u.Username.ToLower() == "admin"))
    {
        pwdService.CreatePasswordHash("Admin@123", out string hash, out string salt);

        db.Users.Add(new User
        {
            Username = "admin",
            FullName = "System Administrator",
            PasswordHash = hash,
            PasswordSalt = salt,
            RoleId = 2,
            MustChangePassword = true,
            CreatedAt = DateTime.UtcNow
        });

        db.SaveChanges();

    }
}
app.MapGet("/test-db", (ApplicationDbContext db) =>
{
    return db.Users.Count();
});
*/
////////////////////////////////////////////////////
app.MapGet("/", () => "API is running");
app.Run();