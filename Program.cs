using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CryptoSense.Data;
using CryptoSense.Models;
using CryptoSense.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:5083");

builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("AppConfig"));

// Register SQLite Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=cryptosense.db"));

builder.Services.AddHttpClient<BinanceFuturesService>();
builder.Services.AddHttpClient<NewsService>();

builder.Services.AddSingleton<UserManagerService>();
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddHttpClient<TelegramBotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramBotService>());

builder.Services.AddSingleton<IndicatorService>();
builder.Services.AddSingleton<NewsService>();
builder.Services.AddSingleton<SignalEngine>();
builder.Services.AddHostedService<BackgroundMarketScanner>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

// Ensure SQLite database and tables are created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors("AllowAll");

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

List<FuturesSignal>? cachedSignals = null;
DateTime lastSignalScan = DateTime.MinValue;

// 1. Multi-User Login & Super Admin Alert
app.MapPost("/api/auth/login", async (LoginRequest req, UserManagerService userManager, TelegramBotService tgService) =>
{
    var (isValid, user) = userManager.ValidateLogin(req.Username, req.Password);
    if (isValid && user != null)
    {
        // Notify Super Admin @alimahammadov of new login
        _ = tgService.NotifySuperAdminUserLoginAsync(user.Username, "Veb Interfeys (http://localhost:5083)");

        return Results.Ok(new { 
            success = true, 
            token = "auth_" + Guid.NewGuid().ToString("N"),
            username = user.Username,
            role = user.Role
        });
    }
    return Results.Unauthorized();
});

// 2. Admin User Management Endpoints
app.MapGet("/api/admin/users", (UserManagerService userManager) =>
{
    return Results.Ok(userManager.GetAllUsers());
});

app.MapPost("/api/admin/users", (CreateUserRequest req, UserManagerService userManager) =>
{
    var created = userManager.CreateUser(req.Username, req.Password);
    return created ? Results.Ok(new { success = true, message = "Istifadeci yaradildi" }) : Results.BadRequest(new { success = false, message = "Bu ad artiq movcuddur" });
});

app.MapDelete("/api/admin/users/{username}", async (string username, UserManagerService userManager, TelegramBotService tgService) =>
{
    var deleted = userManager.DeleteUser(username);
    if (deleted)
    {
        await TelegramBotService.RevokeUserAsync(username, tgService);
        return Results.Ok(new { success = true });
    }
    return Results.BadRequest(new { success = false });
});

// 3. BTC Market Compass
app.MapGet("/api/btc-compass", async (SignalEngine signalEngine) =>
{
    var compass = await signalEngine.GetBtcCompassAsync();
    return Results.Ok(compass);
});

// 4. Top 35 Futures Tickers
app.MapGet("/api/tickers", async (BinanceFuturesService binanceService) =>
{
    var tickers = await binanceService.GetTopFuturesTickersAsync(35);
    return Results.Ok(tickers);
});

// 5. Single Coin Analysis
app.MapGet("/api/analyze/{symbol}", async (string symbol, string? tf, SignalEngine signalEngine) =>
{
    var timeframe = string.IsNullOrEmpty(tf) ? "15m" : tf;
    var signal = await signalEngine.AnalyzeCoinAsync(symbol, timeframe);
    return Results.Ok(signal);
});

// 6. Multi-Coin Live Signals Radar
app.MapGet("/api/signals/all", async (BinanceFuturesService binanceService, SignalEngine signalEngine) =>
{
    if (cachedSignals != null && DateTime.UtcNow - lastSignalScan < TimeSpan.FromSeconds(10))
    {
        return Results.Ok(cachedSignals);
    }

    var topCoins = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT" };
    var tf = TelegramBotService.UserTimeframe == "Hamisi" ? "15m" : TelegramBotService.UserTimeframe;
    var tasks = topCoins.Select(sym => signalEngine.AnalyzeCoinAsync(sym, tf));
    var results = await Task.WhenAll(tasks);
    
    cachedSignals = results.ToList();
    lastSignalScan = DateTime.UtcNow;

    return Results.Ok(cachedSignals);
});

// 7. News & Sentiment Endpoint
app.MapGet("/api/news", async (NewsService newsService) =>
{
    var summary = await newsService.GetNewsAndSentimentAsync();
    return Results.Ok(summary);
});

// 8. Chart Klines
app.MapGet("/api/klines/{symbol}", async (string symbol, string? tf, BinanceFuturesService binanceService) =>
{
    var timeframe = string.IsNullOrEmpty(tf) ? "15m" : tf;
    var klines = await binanceService.GetKlinesAsync(symbol, timeframe, 60);
    return Results.Ok(klines);
});

// 9. Telegram Test
app.MapPost("/api/telegram/test", async (TelegramBotService tgService, SignalEngine signalEngine) =>
{
    var testSignal = await signalEngine.AnalyzeCoinAsync("SOLUSDT", "15m");
    await tgService.SendSignalAlertAsync(testSignal);
    return Results.Ok(new { success = true, message = "Telegram test siqnali ugurla gonderildi!" });
});

// 10. Performance Statistics (Transparent Tracking)
app.MapGet("/api/stats", async (SignalEngine signalEngine) =>
{
    var stats = await signalEngine.GetPerformanceStatsAsync();
    return Results.Ok(stats);
});

app.Run();