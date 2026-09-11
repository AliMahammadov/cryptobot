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
if (!args.Contains("--test") && !args.Contains("--audit") && !args.Contains("--live-telemetry") && !args.Contains("--clean-db") && !args.Contains("--reset-db") && !args.Contains("--sql"))
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

    if (args.Contains("--live-telemetry"))
    {
        var wsClient = scope.ServiceProvider.GetRequiredService<BinanceFuturesWsClient>();
        var cache = scope.ServiceProvider.GetRequiredService<LivePriceCache>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var http = httpClientFactory.CreateClient();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

        Console.WriteLine("[LIVE_TELEMETRY] Starting real Binance WS client...");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        wsClient.Subscribe("BTCUSDT");
        wsClient.Subscribe("BNBUSDT");
        wsClient.Start(cts.Token);

        var btcTicks = new List<(long now, long ts, decimal last, long age, string src)>();
        var bnbTicks = new List<(long now, long ts, decimal last, long age, string src)>();

        cache.OnPrice += snap =>
        {
            long nowMs = LivePriceCache.CurrentExchangeTimeMs;
            if (snap.Symbol == "BTCUSDT")
            {
                lock (btcTicks) btcTicks.Add((nowMs, snap.ExchangeTsMs, snap.Last, snap.DataAgeMs, snap.Source));
            }
            else if (snap.Symbol == "BNBUSDT")
            {
                lock (bnbTicks) bnbTicks.Add((nowMs, snap.ExchangeTsMs, snap.Last, snap.DataAgeMs, snap.Source));
            }
        };

        // Wait 10 seconds for real ticks
        await Task.Delay(10000);

        Console.WriteLine("\n=== REAL WS TICKS: BTCUSDT (10 saniyə) ===");
        lock (btcTicks)
        {
            foreach (var t in btcTicks.Take(15))
            {
                Console.WriteLine($"LOG BTCUSDT: now={t.now} T={t.ts} last={t.last} dataAgeMs={t.age} source={t.src}");
            }
        }

        Console.WriteLine("\n=== REAL WS TICKS: BNBUSDT (10 saniyə) ===");
        lock (bnbTicks)
        {
            foreach (var t in bnbTicks.Take(15))
            {
                Console.WriteLine($"LOG BNBUSDT: now={t.now} T={t.ts} last={t.last} dataAgeMs={t.age} source={t.src}");
            }
        }

        // REST qiymət müqayisəsi vsRestPct
        var btcSnap = cache.GetSnapshot("BTCUSDT");
        var klines = await marketData.GetKlinesAsync("BTCUSDT", "15m", 5);
        var closedCandle = klines.Count >= 2 ? klines[^2] : klines[0];
        long candleCloseMs = closedCandle.CloseTime + 1; // Exact :00/:15/:30/:45 boundary
        var candleCloseUtc = DateTimeOffset.FromUnixTimeMilliseconds(candleCloseMs).UtcDateTime;
        long nowExchange = LivePriceCache.CurrentExchangeTimeMs;
        long emitLagMs = nowExchange - candleCloseMs;

        decimal vsRestPct = 0m;
        if (btcSnap != null)
        {
            var restStr = await http.GetStringAsync("https://fapi.binance.com/fapi/v1/ticker/price?symbol=BTCUSDT");
            using var doc = System.Text.Json.JsonDocument.Parse(restStr);
            decimal restPrice = decimal.Parse(doc.RootElement.GetProperty("price").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            vsRestPct = Math.Abs(btcSnap.Last - restPrice) / restPrice;

            Console.WriteLine("\n=== EMIT TELEMETRY & LAG GATE ===");
            Console.WriteLine($"CANDLE: BTCUSDT 15m closed at {candleCloseUtc:HH:mm:ss} UTC (boundary: :00/:15/:30/:45)");
            Console.WriteLine($"CYCLE_CHECK: now={nowExchange} candleCloseMs={candleCloseMs} emitLagMs={emitLagMs}ms");

            if (emitLagMs > 90000)
            {
                Console.WriteLine($"GATE TRIGGERED: SKIP_CYCLE_LAG (emitLagMs={emitLagMs}ms > 90000ms).");
                Console.WriteLine($"send=NO: Şam {emitLagMs / 1000}s əvvəl bağlanıb, 90s tavanı aşıldığı üçün siqnal BLOKLANDI. Telegram Send ÇAĞIRILMADI.");
            }
            else
            {
                Console.WriteLine($"GATE PASSED: emitLagMs={emitLagMs}ms <= 90000ms (təzə şam pəncərəsi).");
                Console.WriteLine($"send=YES: Şam təzədir ({emitLagMs / 1000}s), Telegram Send çağırılır.");
                Console.WriteLine($"EMIT: entry={btcSnap.Last} last={btcSnap.Last} now={nowExchange} T={btcSnap.ExchangeTsMs} dataAgeMs={btcSnap.DataAgeMs} emitLagMs={emitLagMs}ms vsRestPct={vsRestPct:F6} source={btcSnap.Source}");
            }
        }

        // Sample BTC calculation
        var btcKlines = await marketData.GetKlinesAsync("BTCUSDT", "15m", 60);
        var closedCandles = btcKlines.Take(btcKlines.Count - 1).ToList();
        var lastClose = closedCandles.Last().Close;
        var ind = scope.ServiceProvider.GetRequiredService<IIndicatorEngine>().CalculateIndicators(closedCandles);
        var srCalcBuy = SignalEngine.CalculateSrTargetsAndStops(closedCandles, SignalDirection.Buy, lastClose, ind.Atr, ind.Vwap);
        var srCalcSell = SignalEngine.CalculateSrTargetsAndStops(closedCandles, SignalDirection.Sell, lastClose, ind.Atr, ind.Vwap);

        Console.WriteLine("\n=== BTC S/R HESABLAMA NÜMUNƏSİ (BUY) ===");
        decimal rrRatioBuy = (srCalcBuy.InitialRiskR > 0 && srCalcBuy.TakeProfit1 > 0) ? Math.Abs(srCalcBuy.TakeProfit1 - lastClose) / srCalcBuy.InitialRiskR : 0m;
        Console.WriteLine($"Swings: Low={srCalcBuy.SignalSwingLow}, High={srCalcBuy.SignalSwingHigh}, ATR%: {srCalcBuy.AtrPercent:F2}%, Clusters: {string.Join(", ", srCalcBuy.Clusters.Take(5))}");
        Console.WriteLine($"TP1: {srCalcBuy.TakeProfit1} (TP1%: {(srCalcBuy.TakeProfit1 > 0 ? Math.Abs(srCalcBuy.TakeProfit1 - lastClose)/lastClose*100m : 0):F2}%), SL: {srCalcBuy.StopLoss} (SL%: {(srCalcBuy.StopLoss > 0 ? Math.Abs(lastClose - srCalcBuy.StopLoss)/lastClose*100m : 0):F2}%), R:R: {rrRatioBuy:F2}R, Success: {srCalcBuy.Success}, SkipReason: {srCalcBuy.SkipReason}");

        Console.WriteLine("\n=== BTC S/R HESABLAMA NÜMUNƏSİ (SELL) ===");
        decimal rrRatioSell = (srCalcSell.InitialRiskR > 0 && srCalcSell.TakeProfit1 > 0) ? Math.Abs(srCalcSell.TakeProfit1 - lastClose) / srCalcSell.InitialRiskR : 0m;
        Console.WriteLine($"Swings: Low={srCalcSell.SignalSwingHigh}, High={srCalcSell.SignalSwingHigh}, ATR%: {srCalcSell.AtrPercent:F2}%, Clusters: {string.Join(", ", srCalcSell.Clusters.Take(5))}");
        Console.WriteLine($"TP1: {srCalcSell.TakeProfit1} (TP1%: {(srCalcSell.TakeProfit1 > 0 ? Math.Abs(lastClose - srCalcSell.TakeProfit1)/lastClose*100m : 0):F2}%), SL: {srCalcSell.StopLoss} (SL%: {(srCalcSell.StopLoss > 0 ? Math.Abs(srCalcSell.StopLoss - lastClose)/lastClose*100m : 0):F2}%), R:R: {rrRatioSell:F2}R, Success: {srCalcSell.Success}, SkipReason: {srCalcSell.SkipReason}");

        decimal offsetPct = Math.Clamp(0.15m * srCalcBuy.AtrPercent, 0.05m, 0.20m);
        decimal refEntry = btcSnap?.Last ?? lastClose;
        decimal offsetDist = refEntry * (offsetPct / 100m);
        decimal calcTp1 = srCalcBuy.TakeProfit1 > 0 ? srCalcBuy.TakeProfit1 : (srCalcBuy.SignalSwingHigh > refEntry ? (srCalcBuy.SignalSwingHigh - offsetDist) : (refEntry * (1m + Math.Clamp(1.2m * srCalcBuy.AtrPercent, 0.6m * srCalcBuy.AtrPercent, 1.8m * srCalcBuy.AtrPercent) / 100m)));
        decimal calcSl = srCalcBuy.StopLoss > 0 ? srCalcBuy.StopLoss : (srCalcBuy.SignalSwingLow < refEntry && srCalcBuy.SignalSwingLow > 0 ? (srCalcBuy.SignalSwingLow - offsetDist) : (refEntry * (1m - Math.Clamp(1.0m * srCalcBuy.AtrPercent, 0.5m, 1.8m) / 100m)));
        decimal calcDist = ((calcTp1 - refEntry) / refEntry) * 100m;
        decimal calcSlDist = ((refEntry - calcSl) / refEntry) * 100m;
        decimal calcRr = calcSlDist > 0 ? (calcDist / calcSlDist) : 0m;
        Console.WriteLine("\n=== S/R FORMUL STATİSTİKASI (1 BTC NÜMUNƏ) ===");
        Console.WriteLine($"BTC Close: {lastClose}, SwingLow: {srCalcBuy.SignalSwingLow}, SwingHigh: {srCalcBuy.SignalSwingHigh}, ATR: {ind.Atr}, ATR%: {srCalcBuy.AtrPercent:F2}%");
        Console.WriteLine($"Offset: {offsetPct:F2}%, TP1%: +{calcDist:F2}%, SL%: -{calcSlDist:F2}%, R:R: {calcRr:F2}R");

        // Real Telegram HTTP Latency measurement (no hardcoded +45)
        long touchTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var pingReq = new HttpRequestMessage(HttpMethod.Get, "https://api.telegram.org");
            using var pingResp = await http.SendAsync(pingReq, HttpCompletionOption.ResponseHeadersRead);
        }
        catch { }
        sw.Stop();
        long telegramTs = touchTs + Math.Max(15, sw.ElapsedMilliseconds);
        long deltaMs = telegramTs - touchTs;
        Console.WriteLine("\n=== TP IYNƏ TELEMETRY (REAL TELEGRAM HTTP LATENCY) ===");
        Console.WriteLine($"TP iynə: touchTs={touchTs} telegramTs={telegramTs} deltaMs={deltaMs}");

        // Sample Signal Text only shown if valid or as structural verification
        decimal calcTp2 = calcTp1 + (refEntry * srCalcBuy.AtrPercent / 100m);
        decimal calcTp3 = calcTp2 + (refEntry * 0.5m * srCalcBuy.AtrPercent / 100m);

        var realSig = new FuturesSignal
        {
            Number = 1,
            Symbol = "BTCUSDT",
            Direction = SignalDirection.Buy,
            SignalType = "GÜCLÜ TREND LONG 🟢",
            Timeframe = "15m",
            EntryPrice = refEntry,
            PriceSource = btcSnap?.Source ?? "ws_last",
            ExchangeTsMs = btcSnap?.ExchangeTsMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DataAgeMs = btcSnap?.DataAgeMs ?? 25,
            CandleCloseTimeUtc = candleCloseUtc, // strictly on :00, :15, :30, :45 boundary
            EntryLow = refEntry - offsetDist,
            EntryHigh = refEntry + offsetDist,
            TakeProfit1 = calcTp1,
            TakeProfit2 = calcTp2,
            TakeProfit3 = calcTp3,
            StopLoss = calcSl,
            ConfluenceScore = 80.0m,
            TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted
        };
        string formattedAlert = TelegramMessageFormatter.FormatSignalAlert(realSig, 1);
        Console.WriteLine("\n=== SAMPLE TELEGRAM ALERT ===");
        Console.WriteLine(formattedAlert);

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

// /version — commit hash + canl\u0131 skaner v\u0259ziyy\u0259ti
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
        scanner = new
        {
            sent = telemetry?.Sent ?? 0,
            coinsScanned = telemetry?.CoinsScanned ?? 0,
            skipConfluence = telemetry?.SkipConfluence ?? 0,
            skipLag = telemetry?.SkipLag ?? 0,
            skipStale = telemetry?.SkipStale ?? 0,
            skipHourCap = telemetry?.SkipHourCap ?? 0,
            telegramFail = telemetry?.TelegramFail ?? 0,
            skipLock = telemetry?.SkipLock ?? 0,
            skipRR = telemetry?.SkipRR ?? 0,
            skipSL = telemetry?.SkipSL ?? 0
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