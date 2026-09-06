using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Application.Services;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using CryptoSense.Infrastructure.MarketData;
using CryptoSense.Infrastructure.Persistence;
using CryptoSense.Infrastructure.Persistence.Repositories;
using CryptoSense.Infrastructure.Telegram;
using CryptoSense.Infrastructure.Testing;
using CryptoSense.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "5083";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// 1. Configuration
builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("AppConfig"));

// 2. Persistence Layer (SQLite with EF Core)
var dataDir = Directory.Exists("/app/data")
    ? "/app/data"
    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

if (!Directory.Exists(dataDir))
{
    Directory.CreateDirectory(dataDir);
}

var dbPath = Path.Combine(dataDir, "cryptosense.db");

if (!File.Exists(dbPath))
{
    if (File.Exists("cryptosense.db"))
    {
        try { File.Copy("cryptosense.db", dbPath); Console.WriteLine($"[Persistence] Migrated root cryptosense.db -> {dbPath}"); } catch { }
    }
    else if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cryptosense.db")))
    {
        try { File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cryptosense.db"), dbPath); Console.WriteLine($"[Persistence] Migrated base cryptosense.db -> {dbPath}"); } catch { }
    }
}

var connectionString = $"Data Source={dbPath}";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ISignalRepository, SignalRepository>();
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// 3. Application & Infrastructure Services
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<IMarketDataProvider, BinanceMarketDataProvider>();
builder.Services.AddHttpClient<INewsService, NewsService>();

builder.Services.AddSingleton<IIndicatorEngine, IndicatorEngine>();
builder.Services.AddScoped<IUserManagerService, UserManagerService>();
builder.Services.AddScoped<ISignalEngine, SignalEngine>();
builder.Services.AddScoped<MarketSimulator>();
builder.Services.AddScoped<SystemTestSuite>();

// 4. Telegram Bot Service & Background Workers
builder.Services.AddSingleton<ITelegramBotService>(sp =>
{
    var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("TelegramBotClient");
    var config = sp.GetRequiredService<IOptions<AppConfig>>();
    return new TelegramBotService(client, config, sp);
});

// If running in test, audit or maintenance mode, we don't start background daemons
if (!args.Contains("--test") && !args.Contains("--audit") && !args.Contains("--clean-db") && !args.Contains("--reset-db"))
{
    builder.Services.AddHostedService(sp => (TelegramBotService)sp.GetRequiredService<ITelegramBotService>());
    builder.Services.AddHostedService<BackgroundMarketScanner>();
}

// 5. CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

// 6. Database Initialization & Super Admin Seeding
using (var scope = app.Services.CreateScope())
{
    var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
    unitOfWork.EnsureDatabaseCreated();

    if (args.Contains("--clean-db") || args.Contains("--reset-db"))
    {
        unitOfWork.PurgeAndResetDatabase();
        Console.WriteLine("[Database] Database purged and clean SuperAdmin re-created.");
        return;
    }

    var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();

    if (args.Contains("--test"))
    {
        var testSuite = scope.ServiceProvider.GetRequiredService<SystemTestSuite>();
        await testSuite.RunAllTestsAsync();
        return;
    }

    if (args.Contains("--audit"))
    {
        var testSuite = scope.ServiceProvider.GetRequiredService<SystemTestSuite>();
        await testSuite.RunAuditAsync();
        return;
    }
}

app.UseCors("AllowAll");

// 7. Static Files (if web assets exist in wwwroot)
var staticFileOptions = new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate, max-age=0";
        ctx.Context.Response.Headers["Pragma"] = "no-cache";
        ctx.Context.Response.Headers["Expires"] = "0";

        if (ctx.File.Name.EndsWith(".html")) ctx.Context.Response.ContentType = "text/html; charset=utf-8";
        else if (ctx.File.Name.EndsWith(".js")) ctx.Context.Response.ContentType = "application/javascript; charset=utf-8";
        else if (ctx.File.Name.EndsWith(".css")) ctx.Context.Response.ContentType = "text/css; charset=utf-8";
        else if (ctx.File.Name.EndsWith(".json")) ctx.Context.Response.ContentType = "application/json; charset=utf-8";
    }
};

app.UseDefaultFiles();
app.UseStaticFiles(staticFileOptions);

// 8. REST Endpoints
app.MapGet("/", () => Results.Ok(new 
{ 
    Status = "Online", 
    Service = "CryptoSense Clean Architecture v2", 
    Bot = "@Ali_Mahammadov Trading Bot Service",
    Version = "2.0.0",
    TimeUtc = DateTime.UtcNow
}));

app.MapPost("/api/auth/login", async (LoginRequest req, IUserManagerService userManager, ITelegramBotService tgService) =>
{
    var (isValid, user) = await userManager.ValidateLoginAsync(req.Username, req.Password);
    if (isValid && user != null)
    {
        _ = tgService.NotifySuperAdminUserLoginAsync(user.Username, "Veb / REST Interfeys");
        return Results.Ok(new
        {
            success = true,
            user = new
            {
                user.Id,
                user.Username,
                user.Role,
                user.TelegramUsername
            }
        });
    }
    return Results.BadRequest(new { success = false, message = "İstifadəçi adı və ya parol yalnışdır." });
});

app.MapGet("/api/stats", async (ISignalEngine signalEngine) =>
{
    var stats = await signalEngine.GetPerformanceStatsAsync();
    return Results.Ok(stats);
});

app.MapGet("/api/signals/history", async (ISignalEngine signalEngine, int? count) =>
{
    var signals = await signalEngine.GetSignalHistoryAsync(count ?? 30);
    return Results.Ok(signals);
});

app.MapGet("/api/signals/active", async (ISignalEngine signalEngine) =>
{
    var active = await signalEngine.GetTrackedActiveSignalsAsync();
    return Results.Ok(active);
});

app.MapGet("/api/compass", async (ISignalEngine signalEngine) =>
{
    var compass = await signalEngine.GetBtcCompassAsync();
    return Results.Ok(compass);
});

app.MapGet("/api/news", async (INewsService newsService) =>
{
    var news = await newsService.GetNewsAndSentimentAsync();
    return Results.Ok(news);
});

app.MapGet("/api/admin/users", async (IUserManagerService userManager) =>
{
    var users = await userManager.GetAllUsersAsync();
    return Results.Ok(users.Select(u => new
    {
        u.Id,
        u.Username,
        u.Role,
        u.TelegramUsername,
        u.TelegramChatId,
        u.IsActive,
        u.CreatedAtUtc,
        u.LastLoginAt
    }));
});

app.MapPost("/api/admin/create-user", async (CreateUserRequest req, IUserManagerService userManager) =>
{
    var success = await userManager.CreateUserAsync(req.Username, req.Password);
    return success 
        ? Results.Ok(new { success = true, message = $"'{req.Username}' istifadəçisi uğurla yaradıldı." }) 
        : Results.BadRequest(new { success = false, message = "İstifadəçi artıq mövcuddur və ya məlumatlar yalnışdır." });
});

Console.WriteLine("==========================================================");
Console.WriteLine("🚀 CryptoSense Clean Architecture v2 Uğurla Başladılır...");
Console.WriteLine("👑 Super Admin: Ali / 23031999Am (@Ali_Mahammadov)");
Console.WriteLine("📡 Port: http://0.0.0.0:5083");
Console.WriteLine("==========================================================");

app.Run();