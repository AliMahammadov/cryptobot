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

// 1. Configuration — bind base values from appsettings.json
builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("AppConfig"));

// Override sensitive fields from environment variables (Railway / Docker secrets).
// TELEGRAM_BOT_TOKEN env var always wins over appsettings.json — prevents accidental commit of live token.
builder.Services.PostConfigure<AppConfig>(cfg =>
{
    var envToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
    if (!string.IsNullOrWhiteSpace(envToken))
        cfg.TelegramBotToken = envToken;

    var envChatId = Environment.GetEnvironmentVariable("SUPER_ADMIN_CHAT_ID");
    if (!string.IsNullOrWhiteSpace(envChatId))
        cfg.SuperAdminChatId = envChatId;

    var envAdminPwd = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
    if (!string.IsNullOrWhiteSpace(envAdminPwd))
        cfg.AdminSeedPassword = envAdminPwd;
});


// 2. Persistence Layer (SQLite with EF Core)
var dbPath = CryptoSense.Domain.Common.AppPaths.DatabasePath;
Console.WriteLine($"[Persistence] Active Database Path: {dbPath}");
var connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared;Default Timeout=30;";

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
builder.Services.AddSingleton<LivePriceCache>();
builder.Services.AddSingleton<BinanceFuturesWsClient>();
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
if (!args.Contains("--test") && !args.Contains("--audit") && !args.Contains("--clean-db") && !args.Contains("--reset-db") && !args.Contains("--sql"))
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

    if (args.Contains("--sql"))
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        var sqlIdx = Array.IndexOf(args, "--sql");
        var query = sqlIdx < args.Length - 1 ? args[sqlIdx + 1] : "SELECT Username, TelegramChatId, TelegramUserId FROM Users";
        cmd.CommandText = query;
        using var reader = await cmd.ExecuteReaderAsync();
        var colCount = reader.FieldCount;
        var colNames = new List<string>();
        for (int i = 0; i < colCount; i++) colNames.Add(reader.GetName(i));
        Console.WriteLine(string.Join(" | ", colNames));
        Console.WriteLine(new string('-', 50));
        while (await reader.ReadAsync())
        {
            var vals = new List<string>();
            for (int i = 0; i < colCount; i++) vals.Add(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "");
            Console.WriteLine(string.Join(" | ", vals));
        }
        return;
    }

    if (args.Contains("--clean-db") || args.Contains("--reset-db"))
    {
        unitOfWork.PurgeAndResetDatabase();
        Console.WriteLine("[Database] Database purged and clean SuperAdmin re-created.");
        return;
    }

    var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();

    if (args.Contains("--test") || args.Contains("--test-all"))
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

// /version — commit hash + canlı skaner vəziyyəti
app.MapGet("/version", () =>
{
    var commit = Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA") ?? "unknown";
    var shortCommit = commit.Length >= 7 ? commit[..7] : commit;
    var btcSnap = app.Services.GetService<CryptoSense.Application.Services.LivePriceCache>()?.GetSnapshot("BTCUSDT");
    var telemetry = CryptoSense.Worker.BackgroundMarketScanner.LatestTelemetrySnapshot;
    return Results.Ok(new
    {
        commit = shortCommit,
        commitFull = commit,
        version = "2.0.0",
        timeUtc = DateTime.UtcNow,
        timeBaku = DateTime.UtcNow.AddHours(4).ToString("yyyy-MM-dd HH:mm:ss"),
        lastScanUtc = CryptoSense.Worker.BackgroundMarketScanner.LastScanUtc,
        scanner = new
        {
            sent = telemetry?.Sent ?? 0,
            coinsScanned = telemetry?.CoinsScanned ?? 0,
            maxConfluenceSeen = telemetry?.MaxConfluenceSeen ?? -1m,
            skipConfluence = telemetry?.SkipConfluence ?? 0,
            skipLag = telemetry?.SkipLag ?? 0,
            skipStale = telemetry?.SkipStale ?? 0,
            skipHourCap = telemetry?.SkipHourCap ?? 0,
            telegramFail = telemetry?.TelegramFail ?? 0,
            skipLock = telemetry?.SkipLock ?? 0,
            skipRR = telemetry?.SkipRR ?? 0,
            skipSL = telemetry?.SkipSL ?? 0,
            lastScanUtc = CryptoSense.Worker.BackgroundMarketScanner.LastScanUtc
        },
        btc = new
        {
            dataAgeMs = btcSnap?.DataAgeMs ?? -1,
            source = btcSnap?.Source ?? "no_snap",
            last = btcSnap?.Last ?? 0,
            wsHealthy = (btcSnap != null && btcSnap.DataAgeMs <= 3500 && btcSnap.Source != "rest_fallback")
        }
    });
});

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

// Minimum Admin Authorization check for /api/admin/*
bool IsSuperAdmin(HttpContext ctx)
{
    var adminPwd = Environment.GetEnvironmentVariable("ADMIN_PASSWORD") ?? "23031999Am";
    if (ctx.Request.Headers.TryGetValue("X-Admin-Password", out var pwd) && pwd == adminPwd) return true;
    if (ctx.Request.Headers.TryGetValue("Authorization", out var auth))
    {
        var authStr = auth.ToString();
        if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && authStr[7..].Trim() == adminPwd) return true;
        if (authStr.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(authStr[6..].Trim()));
                var parts = decoded.Split(':', 2);
                if (parts.Length == 2 && parts[0].Equals("Ali", StringComparison.OrdinalIgnoreCase) && parts[1] == adminPwd) return true;
            }
            catch { }
        }
    }
    if (ctx.Request.Query.TryGetValue("adminPassword", out var qPwd) && qPwd == adminPwd) return true;
    return false;
}

app.MapGet("/api/admin/users", async (HttpContext ctx, IUserManagerService userManager) =>
{
    if (!IsSuperAdmin(ctx)) return Results.Unauthorized();
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

app.MapPost("/api/admin/create-user", async (HttpContext ctx, CreateUserRequest req, IUserManagerService userManager) =>
{
    if (!IsSuperAdmin(ctx)) return Results.Unauthorized();
    var success = await userManager.CreateUserAsync(req.Username, req.Password);
    return success 
        ? Results.Ok(new { success = true, message = $"'{req.Username}' istifadəçisi uğurla yaradıldı." }) 
        : Results.BadRequest(new { success = false, message = "İstifadəçi artıq mövcuddur və ya məlumatlar yalnışdır." });
});

Console.WriteLine("==========================================================");
Console.WriteLine("🚀 CryptoSense Clean Architecture v2 Uğurla Başladılır...");
Console.WriteLine("👑 Super Admin: Ali (@Ali_Mahammadov)");
Console.WriteLine("📡 Port: http://0.0.0.0:5083");
Console.WriteLine("==========================================================");

app.Run();