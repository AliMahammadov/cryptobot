using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Application.Services;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using CryptoSense.Infrastructure.MarketData;
using CryptoSense.Infrastructure.Persistence;
using CryptoSense.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CryptoSense.Worker
{
    public class BackgroundMarketScanner : BackgroundService
    {
        private readonly ITelegramBotService _telegramService;
        private readonly IServiceProvider _serviceProvider;
        private readonly LivePriceCache _livePriceCache;
        private readonly BinanceFuturesWsClient _wsClient;
        private readonly AppConfig _config;

        public const int MaxGlobalOpenPositions = 20;

        private static int _consecutiveLosses = 0;
        private static DateTime _circuitBreakerUntil = DateTime.MinValue;
        private static DateTime _lastCircuitBreakerAlertSent = DateTime.MinValue;

        private static int _nextSignalNumber = 0;
        private static readonly object _sendLock = new();
        private static readonly SemaphoreSlim _sendSemaphore = new(1, 1);
        private static readonly ConcurrentDictionary<string, DateTime> _lastRestFallbackTime = new();
        private static readonly ConcurrentDictionary<string, DateTime> _coinLastScanTime = new();

        private static readonly ConcurrentDictionary<string, DateTime> _lastAlertSent = new();
        private static readonly ConcurrentDictionary<string, DateTime> _coinCooldowns = new();
        private static readonly ConcurrentDictionary<string, byte> _coinActiveLocks = new();
        private static readonly ConcurrentDictionary<string, byte> _scanningCoins = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastVolatilityAlertSent = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastSymbolAlertTime = new();
        private static readonly object _heartbeatLock = new();
        private static readonly SemaphoreSlim _signalDispatchLock = new(1, 1);
        private static string _lastDailyReportDateBaku = "";

        private static readonly ConcurrentDictionary<string, List<(string Symbol, SignalDirection Direction)>> _hourlyDispatches = new();
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _signalOutcomeSemaphores = new();
        private static readonly ConcurrentDictionary<string, DateTime> _sentOutcomeDeduplication = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastPriceUpdateHandled = new();
        // Overfilter diaqnostika: coin+tf → (confluence%, skipReason)
        private static readonly ConcurrentDictionary<string, (double Confluence, string SkipReason)> _overfilterDiag = new();

        public class ScanTelemetry
        {
            public int CoinsScanned;
            public int Sent;
            public int SkipChase;
            public int SkipCorr;
            public int SkipSL;
            public int SkipRR;
            public int SkipLock;
            public int SkipLag;
            public int SkipStale;
            public int SkipConfluence;  // Confluence < 75 (BLOCK, not PASS)
            public int SkipGozleme;     // GÖZLƏMƏ ⚪ (market neutral, ADX < 16, no setup)
            public int SkipBtcBearLong; // SKIP_BTC_BEAR_LONG
            public int SkipBtcRange;    // SKIP_BTC_RANGE
            public int SkipBtc4hOppose; // SKIP_BTC_4H_OPPOSE
            public int SkipHourCap;     // morningCap >=1 OR hourCap >=2
            public int TelegramFail;    // SendSignalAlertAsync returned false
            public int SkipCircuitBreaker;
            public int SkipMaxOpen;
            public int SkipDailyLoss;

            public int SkipBtcGate => SkipBtcBearLong + SkipBtc4hOppose;

            public ScanTelemetry Clone() => new ScanTelemetry
            {
                CoinsScanned = this.CoinsScanned,
                Sent = this.Sent,
                SkipChase = this.SkipChase,
                SkipCorr = this.SkipCorr,
                SkipSL = this.SkipSL,
                SkipRR = this.SkipRR,
                SkipLock = this.SkipLock,
                SkipLag = this.SkipLag,
                SkipStale = this.SkipStale,
                SkipConfluence = this.SkipConfluence,
                SkipGozleme = this.SkipGozleme,
                SkipBtcBearLong = this.SkipBtcBearLong,
                SkipBtcRange = this.SkipBtcRange,
                SkipBtc4hOppose = this.SkipBtc4hOppose,
                SkipHourCap = this.SkipHourCap,
                TelegramFail = this.TelegramFail,
                SkipCircuitBreaker = this.SkipCircuitBreaker,
                SkipMaxOpen = this.SkipMaxOpen,
                SkipDailyLoss = this.SkipDailyLoss
            };

            public void Reset()
            {
                CoinsScanned = 0;
                Sent = 0;
                SkipChase = 0;
                SkipCorr = 0;
                SkipSL = 0;
                SkipRR = 0;
                SkipLock = 0;
                SkipLag = 0;
                SkipStale = 0;
                SkipConfluence = 0;
                SkipGozleme = 0;
                SkipBtcBearLong = 0;
                SkipBtcRange = 0;
                SkipBtc4hOppose = 0;
                SkipHourCap = 0;
                TelegramFail = 0;
                SkipCircuitBreaker = 0;
                SkipMaxOpen = 0;
                SkipDailyLoss = 0;
            }
        }

        private static readonly ScanTelemetry _hourlyTelemetry = new();
        public static ScanTelemetry LatestTelemetrySnapshot { get; private set; } = new();
        private static string _lastLoggedHourKey = "";

        public static void ClearLocks()
        {
            _coinCooldowns.Clear();
            _lastAlertSent.Clear();
            _lastVolatilityAlertSent.Clear();
            _lastSymbolAlertTime.Clear();
            _hourlyDispatches.Clear();
            _signalOutcomeSemaphores.Clear();
            _sentOutcomeDeduplication.Clear();
            _lastPriceUpdateHandled.Clear();
        }

        public static void ResetSignalCounter()
        {
            lock (_sendLock)
            {
                _nextSignalNumber = 0;
            }
        }

        public BackgroundMarketScanner(
            ITelegramBotService telegramService,
            IServiceProvider serviceProvider,
            LivePriceCache livePriceCache,
            BinanceFuturesWsClient wsClient,
            IOptions<AppConfig> config)
        {
            _telegramService = telegramService;
            _serviceProvider = serviceProvider;
            _livePriceCache = livePriceCache;
            _wsClient = wsClient;
            _config = config.Value;

            _livePriceCache.OnPrice += HandlePriceUpdate;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[BackgroundMarketScanner] Yüksək Dəqiqlikli Skaner və Nəticə İzləyicisi başladı.");

            // Initialize highest signal number from database and perform startup cleanup of orphaned signals
            using (var initScope = _serviceProvider.CreateScope())
            {
                try
                {
                    var uow = initScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    int cleaned = await uow.Signals.CleanupOrphanedSignalsAsync();
                    if (cleaned > 0)
                    {
                        Console.WriteLine($"[BackgroundMarketScanner] Cleaned {cleaned} orphaned signals from database on startup.");
                    }

                    var db = initScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var maxNum = db.Signals.Any(s => s.SignalAlertSent && s.SignalNumber > 0)
                        ? db.Signals.Where(s => s.SignalAlertSent && s.SignalNumber > 0).Max(s => s.SignalNumber)
                        : 0;
                    lock (_sendLock)
                    {
                        _nextSignalNumber = maxNum;
                    }
                    Console.WriteLine($"[BackgroundMarketScanner] _nextSignalNumber initialized to {_nextSignalNumber}");

                    // STARTUP CATCH-UP: Sönülü / deploy ikən SL və ya TP toxunubsa dərhal aşkarla, bağla və Telegram nəticə göndər
                    await PerformStartupCatchUpAsync(uow, initScope.ServiceProvider.GetRequiredService<IMarketDataProvider>());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BackgroundMarketScanner] Error initializing signal number / cleanup / catch-up: {ex.Message}");
                }
            }

            // Heartbeat timers are preserved from persisted settings on startup (never reset to avoid duplicate heartbeats on restart)

            // Pre-subscribe Default 40 institutional coins and active portfolio coins to WebSocket stream on startup
            foreach (var coin in TelegramBotService.Default40Coins)
            {
                _wsClient.Subscribe(coin);
            }
            foreach (var pref in TelegramBotService.UserPreferences.Values)
            {
                if (pref.IsActive && pref.Coins != null)
                {
                    foreach (var c in pref.Coins)
                    {
                        var norm = c.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? c.ToUpperInvariant() : c.ToUpperInvariant() + "USDT";
                        _wsClient.Subscribe(norm);
                    }
                }
            }

            // Start WebSocket client independently
            _wsClient.Start(stoppingToken);

            // Wait until WS is healthy or seeded
            await WaitUntilWsHealthyAsync(stoppingToken);

            // Perform 1-time Market Catch-Up Scan & Send Boot Briefing to SuperAdmin
            await PerformStartupMarketCatchUpAsync(stoppingToken);

            // 1. DEDICATED FAST OUTCOME TRACKER (Evaluates TP/SL and Expirations every 3 seconds)
            var outcomeTrackerTask = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await TrackActiveSignalOutcomesAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[OutcomeTracker] Error: {ex.Message}");
                    }
                    await Task.Delay(2000, stoppingToken);
                }
            }, stoppingToken);

            // 2. CONTINUOUS HIGH-CONVICTION MARKET SCANNER
            var marketScannerTask = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await ScanMarketSignalsAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MarketScanner] Scanner loop warning: {ex.Message}");
                    }
                    await Task.Delay(5000, stoppingToken);
                }
            }, stoppingToken);

            // 3. REAL-TIME BREAKING NEWS & NEW LISTING PUSH MONITOR - Dayandırıldı (istifadəçi tələbi ilə avtomatik xəbər axını söndürüldü)
            await Task.WhenAll(outcomeTrackerTask, marketScannerTask);
        }

        private async Task TrackActiveSignalOutcomesAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var indicatorEngine = scope.ServiceProvider.GetRequiredService<IIndicatorEngine>();
            var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();

            var activeSignals = await unitOfWork.Signals.GetOpenTrackedSignalsAsync();

            // Monotonic sync: Ensure all active DB signals are locked without wiping in-flight locks!
            var activeSymbolsInDb = new HashSet<string>();
            foreach (var s in activeSignals)
            {
                _coinActiveLocks.TryAdd(s.Symbol, 1);
                activeSymbolsInDb.Add(s.Symbol);
                _wsClient.Subscribe(s.Symbol);
            }

            // Stale lock cleanup: only remove lock if coin is definitively not active in DB
            foreach (var lockedSym in _coinActiveLocks.Keys.ToList())
            {
                if (!activeSymbolsInDb.Contains(lockedSym))
                {
                    bool stillHasActive = await unitOfWork.Signals.HasActiveSignalForSymbolAsync(lockedSym);
                    if (!stillHasActive)
                    {
                        _coinActiveLocks.TryRemove(lockedSym, out _);
                        if (!TelegramBotService.Default40Coins.Contains(lockedSym, StringComparer.OrdinalIgnoreCase))
                        {
                            _wsClient.Unsubscribe(lockedSym);
                        }
                    }
                }
            }

            if (activeSignals.Count == 0)
            {
                return;
            }

            foreach (var sig in activeSignals)
            {
                if (sig.OutcomeAlertSent || sig.IsClosed) continue;

                var snap = _livePriceCache.GetSnapshot(sig.Symbol);
                var nowUtc = DateTime.UtcNow;

                // DataAge > 3500: REST last götür, skip etmə — SL buraxılmasın
                if (snap == null || snap.DataAgeMs > 3500)
                {
                    _lastRestFallbackTime[sig.Symbol] = nowUtc;
                    try
                    {
                        var lastAgg = await marketData.GetLastAggTradeAsync(sig.Symbol);
                        if (lastAgg.HasValue)
                        {
                            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastAgg.Value.ExchangeTsMs;
                            Console.WriteLine($"[REST_FALLBACK] {sig.Symbol} {lastAgg.Value.Price} age={age}ms");
                            _livePriceCache.UpdateFromAggTrade(sig.Symbol, lastAgg.Value.Price, lastAgg.Value.ExchangeTsMs, isRestFallback: true);
                            snap = _livePriceCache.GetSnapshot(sig.Symbol);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[OutcomeTracker] REST_FALLBACK error for {sig.Symbol}: {ex.Message}");
                    }
                }

                if (snap == null) continue;

                await ProcessSignalOutcomeAsync(sig, snap, unitOfWork, stoppingToken);
                if (!sig.IsClosed && !sig.OutcomeAlertSent)
                {
                    await CheckCandleInvalidationAndTrailingAsync(sig, marketData, indicatorEngine, unitOfWork, signalEngine, snap.Last, stoppingToken);
                }
            }
        }

        private async Task PerformStartupCatchUpAsync(IUnitOfWork uow, IMarketDataProvider marketData)
        {
            try
            {
                var openSignals = await uow.Signals.GetOpenTrackedSignalsAsync();
                if (openSignals.Count == 0) return;

                Console.WriteLine($"[StartupCatchUp] {openSignals.Count} aktiv siqnal üzrə restart catch-up yoxlanışı başladı...");

                foreach (var sig in openSignals)
                {
                    _coinActiveLocks.TryAdd(sig.Symbol, 1);
                    _wsClient.Subscribe(sig.Symbol);

                    var klines = await marketData.GetKlinesAsync(sig.Symbol, "1m", 60);
                    if (klines.Count == 0)
                    {
                        klines = await marketData.GetKlinesAsync(sig.Symbol, sig.Timeframe, 5);
                    }

                    if (klines.Count == 0) continue;

                    decimal maxHigh = klines.Max(k => k.High);
                    decimal minLow = klines.Min(k => k.Low);
                    decimal lastPrice = klines.Last().Close;

                    _livePriceCache.UpdateFromAggTrade(sig.Symbol, lastPrice, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isRestFallback: true);
                    var snap = _livePriceCache.GetSnapshot(sig.Symbol);
                    if (snap != null)
                    {
                        snap.SessionHigh = Math.Max(snap.SessionHigh, maxHigh);
                        snap.SessionLow = Math.Min(snap.SessionLow, minLow);
                    }

                    bool isLong = sig.Direction == SignalDirection.Buy || sig.SignalType.Contains("LONG");
                    bool slHit = isLong ? (minLow <= sig.StopLoss) : (maxHigh >= sig.StopLoss);
                    bool tp1Hit = sig.TakeProfit1 > 0 && (isLong ? (maxHigh >= sig.TakeProfit1) : (minLow <= sig.TakeProfit1));

                    if (slHit && !sig.Tp1Notified)
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = sig.StopLoss;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "SL_RESTART_CATCHUP";
                        sig.Status = SignalStatus.Failed;
                        sig.OutcomeStatus = "Stop Loss (SL) (Restart Catch-up) ❌";

                        decimal exitPnl = isLong
                            ? Math.Round(((sig.StopLoss - sig.EntryPrice) / sig.EntryPrice) * 100, 2)
                            : Math.Round(((sig.EntryPrice - sig.StopLoss) / sig.EntryPrice) * 100, 2);
                        sig.ResultPercent = Math.Round(exitPnl - 0.10m, 2);

                        await uow.Signals.UpdateAsync(sig);
                        await uow.SaveChangesAsync(CancellationToken.None);

                        _coinActiveLocks.TryRemove(sig.Symbol, out _);

                        if (sig.SignalAlertSent)
                        {
                            await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL) [Restart Aşkarlanması]", sig.StopLoss, sig.ResultPercent.Value);
                        }
                        Console.WriteLine($"[StartupCatchUp] SL caught up for {sig.Symbol} (Id={sig.Id}, NetPnL={sig.ResultPercent}%)");
                    }
                    else if (tp1Hit && !sig.Tp1Notified)
                    {
                        sig.Tp1Notified = true;
                        sig.IsPartial1Closed = true;
                        sig.RemainingPositionRatio = 0.50m;
                        sig.CloseReason = "TP_A";

                        decimal pnl1 = isLong
                            ? Math.Round(((sig.TakeProfit1 - sig.EntryPrice) / sig.EntryPrice) * 100, 2)
                            : Math.Round(((sig.EntryPrice - sig.TakeProfit1) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent = Math.Round(0.50m * pnl1, 2);
                        sig.ProfitPercentAchieved = pnl1;
                        sig.StopLoss = isLong
                            ? SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 1.0012m)
                            : SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 0.9988m);
                        sig.BreakevenTriggered = true;

                        if (sig.TakeProfit2 <= 0 || sig.TakeProfit2 == sig.TakeProfit1)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = sig.TakeProfit1;
                            sig.ClosedAt = DateTime.UtcNow;
                            sig.RemainingPositionRatio = 0m;
                            sig.RealizedProfitPercent = pnl1;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                            sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                            sig.OutcomeStatus = "Hədəf A (TP_A 1.0R) (TAM MƏNFƏƏT) [Restart Catch-up] ✅";

                            await uow.Signals.UpdateAsync(sig);
                            await uow.SaveChangesAsync(CancellationToken.None);
                            _coinActiveLocks.TryRemove(sig.Symbol, out _);

                            if (sig.SignalAlertSent)
                            {
                                await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.0R) [Restart Catch-up]", sig.TakeProfit1, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            await uow.Signals.UpdateAsync(sig);
                            await uow.SaveChangesAsync(CancellationToken.None);
                            if (sig.SignalAlertSent)
                            {
                                await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.0R) [Restart Catch-up + BE Aktiv]", sig.TakeProfit1, pnl1);
                            }
                        }
                        Console.WriteLine($"[StartupCatchUp] TP1 caught up for {sig.Symbol} (Id={sig.Id})");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StartupCatchUp] Error executing startup catch-up: {ex.Message}");
            }
        }

        private async Task WaitUntilWsHealthyAsync(CancellationToken stoppingToken)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine("[BackgroundMarketScanner] WebSocket sağlamlığı gözlənilir (BTCUSDT DataAge <= 3500ms)...");

            while (sw.ElapsedMilliseconds < 45000 && !stoppingToken.IsCancellationRequested)
            {
                var snap = _livePriceCache.GetSnapshot("BTCUSDT");
                if (snap != null && snap.DataAgeMs <= 3500)
                {
                    Console.WriteLine($"[WS_READY] dataAgeMs={snap.DataAgeMs} elapsed={sw.ElapsedMilliseconds}ms source={snap.Source}");
                    return;
                }
                await Task.Delay(1000, stoppingToken);
            }

            // Timeout olarsa: 1 dəfəlik REST ilə BTC qiymətini çəkib DataAge-i təzələsin
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
                var lastAgg = await marketData.GetLastAggTradeAsync("BTCUSDT");
                if (lastAgg.HasValue)
                {
                    _livePriceCache.UpdateFromAggTrade("BTCUSDT", lastAgg.Value.Price, lastAgg.Value.ExchangeTsMs, isRestFallback: false);
                    var snap = _livePriceCache.GetSnapshot("BTCUSDT");
                    Console.WriteLine($"[WS_READY] REST fallback seeded BTCUSDT dataAgeMs={snap?.DataAgeMs ?? -1} price={lastAgg.Value.Price}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WS_READY] Failed to seed BTCUSDT via REST: {ex.Message}");
            }
        }

        private async Task PerformStartupMarketCatchUpAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[BackgroundMarketScanner] Startup Market Catch-Up skanı başladı...");
            var emittedSignals = new List<FuturesSignal>();
            var skipCounts = new ConcurrentDictionary<string, int>();
            int scannedCount = 0;

            // 1. Gather target coins (Default 40 + any user active coins)
            var targetCoins = new HashSet<string>(TelegramBotService.Default40Coins, StringComparer.OrdinalIgnoreCase);
            foreach (var pref in TelegramBotService.UserPreferences.Values)
            {
                if (pref.IsActive && pref.Coins != null)
                {
                    foreach (var c in pref.Coins)
                    {
                        var norm = c.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? c.ToUpperInvariant() : c.ToUpperInvariant() + "USDT";
                        targetCoins.Add(norm);
                    }
                }
            }

            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var engine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
                    var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

                    foreach (var sym in targetCoins)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        if (_coinActiveLocks.ContainsKey(sym)) continue;

                        var timeframes = new[] { "1h", "4h" };
                        foreach (var tf in timeframes)
                        {
                            if (stoppingToken.IsCancellationRequested) break;
                            if (_coinActiveLocks.ContainsKey(sym)) break;

                            scannedCount++;
                            FuturesSignal? signal = null;
                            try
                            {
                                signal = await engine.AnalyzeCoinAsync(sym, tf, isLiveScan: true);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[StartupMarketCatchUp] AnalyzeCoinAsync error for {sym} {tf}: {ex.Message}");
                                continue;
                            }

                            if (signal == null) continue;

                            bool isTradeQualified = signal.Timeframe != "15m" && signal.Confidence >= 75 &&
                                                    signal.SignalType != null &&
                                                    (signal.SignalType.Contains("LONG") || signal.SignalType.Contains("SHORT"));

                            if (!isTradeQualified)
                            {
                                string reasonKey = "Other";
                                if (signal.AnalysisReasons != null && signal.AnalysisReasons.Count > 0)
                                {
                                    var r = signal.AnalysisReasons[0];
                                    if (r.Contains("SKIP_BTC_BEAR_LONG") || r.Contains("SKIP_BTC_4H_OPPOSE")) reasonKey = "BtcGate";
                                    else if (r.Contains("SKIP_BTC_RANGE")) reasonKey = "Range";
                                    else if (r.Contains("Confluence") || r.Contains("< 75.0%")) reasonKey = "Confluence";
                                    else if (r.Contains("ADX")) reasonKey = "ADX";
                                    else if (r.Contains("GÖZLƏMƏ")) reasonKey = "Gözləmə";
                                    else reasonKey = r.Length > 20 ? r.Substring(0, 20) : r;
                                }
                                else if (signal.SignalType != null && signal.SignalType.Contains("GÖZLƏMƏ"))
                                {
                                    reasonKey = "Gözləmə";
                                }
                                skipCounts.AddOrUpdate(reasonKey, 1, (_, v) => v + 1);
                                continue;
                            }

                            // Trade qualified PASS! Check if candle was already delivered
                            var alertKey = $"{signal.Symbol}_{signal.Direction}_{signal.Timeframe}_{signal.SourceCandleOpenTimeUtc:yyyyMMddHHmmss}";
                            if (_lastAlertSent.ContainsKey(alertKey) || signal.SignalAlertSent)
                            {
                                continue;
                            }

                            if (await uow.Signals.HasActiveSignalForSymbolAsync(signal.Symbol))
                            {
                                continue;
                            }

                            var existingCandle = await uow.Signals.GetExistingCandleSignalAsync(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                            if (existingCandle != null && existingCandle.SignalAlertSent)
                            {
                                continue;
                            }

                            // Live price check
                            var snap = _livePriceCache.GetSnapshot(signal.Symbol);
                            if (snap == null || snap.DataAgeMs > 1000)
                            {
                                try
                                {
                                    var lastAgg = await marketData.GetLastAggTradeAsync(signal.Symbol);
                                    if (lastAgg.HasValue)
                                    {
                                        _livePriceCache.UpdateFromAggTrade(signal.Symbol, lastAgg.Value.Price, lastAgg.Value.ExchangeTsMs, isRestFallback: false);
                                        snap = _livePriceCache.GetSnapshot(signal.Symbol);
                                    }
                                }
                                catch { }
                            }

                            if (snap == null || snap.DataAgeMs > 3500)
                            {
                                SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                skipCounts.AddOrUpdate("Stale", 1, (_, v) => v + 1);
                                continue;
                            }

                            signal.EntryPrice = snap.Last;
                            signal.CurrentPrice = snap.Last;
                            signal.PriceSource = snap.Source;
                            signal.ExchangeTsMs = snap.ExchangeTsMs;
                            signal.DataAgeMs = snap.DataAgeMs;

                            var candleDuration = signal.Timeframe == "4h" ? TimeSpan.FromHours(4) : TimeSpan.FromHours(1);
                            signal.CandleCloseTimeUtc = signal.SourceCandleOpenTimeUtc + candleDuration;

                            decimal slDist = Math.Abs(signal.StopLoss - signal.EntryPrice);
                            decimal slPct = signal.EntryPrice > 0 ? (slDist / signal.EntryPrice) * 100m : 0m;
                            decimal maxSlPct = signal.Timeframe == "4h" ? 4.00m : 2.80m;
                            if (slPct > maxSlPct)
                            {
                                SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                skipCounts.AddOrUpdate("SL", 1, (_, v) => v + 1);
                                continue;
                            }

                            decimal tp1Dist = Math.Abs(signal.TakeProfit1 - signal.EntryPrice);
                            decimal tpBDist = signal.TakeProfit2 > 0 ? Math.Abs(signal.TakeProfit2 - signal.EntryPrice) : tp1Dist;
                            decimal weightedTpDist = (0.50m * tp1Dist) + (0.50m * tpBDist);
                            decimal effectiveRr = slDist > 0 ? (weightedTpDist / slDist) : 0m;
                            if (effectiveRr < 1.30m)
                            {
                                SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                skipCounts.AddOrUpdate("RR", 1, (_, v) => v + 1);
                                continue;
                            }

                            // Dispatch signal
                            await _sendSemaphore.WaitAsync(stoppingToken);
                            try
                            {
                                if (_coinActiveLocks.ContainsKey(sym)) continue;
                                if (_coinActiveLocks.Count >= MaxGlobalOpenPositions) break;
                                if (await uow.Signals.HasActiveSignalForSymbolAsync(signal.Symbol)) continue;

                                signal.Number = 0;
                                await uow.Signals.AddAsync(signal);
                                await uow.SaveChangesAsync(stoppingToken);

                                bool ok = await _telegramService.SendSignalAlertAsync(signal);
                                if (ok)
                                {
                                    _lastAlertSent[alertKey] = DateTime.UtcNow;
                                    _lastSymbolAlertTime[signal.Symbol] = DateTime.UtcNow;
                                    SignalEngine.RecordSentSignalCandle(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc, signal);
                                    _coinActiveLocks.TryAdd(sym, 1);
                                    emittedSignals.Add(signal);
                                    Console.WriteLine($"[BOOT_CATCHUP_EMIT] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} Entry={signal.EntryPrice}");
                                }
                                else
                                {
                                    _lastAlertSent.TryRemove(alertKey, out _);
                                    SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                    signal.IsClosed = true;
                                    signal.ClosedAt = DateTime.UtcNow;
                                    signal.Status = SignalStatus.Neutral;
                                    signal.CloseReason = "ALERT_NEVER_SENT_FAILED";
                                    await uow.SaveChangesAsync(stoppingToken);
                                }
                            }
                            finally
                            {
                                _sendSemaphore.Release();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StartupMarketCatchUp] Catch-up error: {ex.Message}");
            }

            // Now, compose and send Boot Briefing to SuperAdmin
            try
            {
                string? superAdminId = TelegramBotService.SuperAdminChatId;
                if (!string.IsNullOrEmpty(superAdminId) && await _telegramService.CanReceivePushAsync(superAdminId))
                {
                    using var scope = _serviceProvider.CreateScope();
                    var engine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
                    var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

                    string commitHash = TelegramMessageFormatter.GetShortGitCommitHash();
                    var btcSnap = _livePriceCache.GetSnapshot("BTCUSDT");
                    long btcDataAgeMs = btcSnap?.DataAgeMs ?? -1;

                    var btcCompass = await engine.GetBtcCompassAsync();
                    string btcTrend = btcCompass?.Trend ?? "Bilinmir";
                    string btcRegime = btcCompass?.Regime.ToString() ?? "Bilinmir";
                    decimal btcAdx = btcCompass?.Btc1hAdx ?? 0m;
                    bool btcStBullish = btcCompass?.IsSuperTrendBullish ?? false;
                    bool btcCandleGreen = btcCompass?.Btc1hCandleColor?.Equals("Green", StringComparison.OrdinalIgnoreCase) ?? true;

                    // ETH 1h check
                    bool ethCandleGreen = true;
                    bool ethFilterPassing = true;
                    try
                    {
                        var ethKlines = await marketData.GetKlinesAsync("ETHUSDT", "1h", 5);
                        var closedEth = ethKlines.Count >= 2 ? ethKlines.Take(ethKlines.Count - 1).ToList() : ethKlines;
                        if (closedEth.Count > 0)
                        {
                            var lastEth = closedEth.Last();
                            ethCandleGreen = lastEth.Close >= lastEth.Open;
                            ethFilterPassing = ethCandleGreen;
                        }
                    }
                    catch { }

                    var nowUtc = DateTime.UtcNow;
                    var last1hClose = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc);
                    int last1hAgeMinutes = (int)Math.Max(0, (nowUtc - last1hClose).TotalMinutes);

                    int current4hBlockHour = (nowUtc.Hour / 4) * 4;
                    var last4hClose = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, current4hBlockHour, 0, 0, DateTimeKind.Utc);
                    int last4hAgeMinutes = (int)Math.Max(0, (nowUtc - last4hClose).TotalMinutes);

                    int nextCheckMinutes = Math.Max(1, 60 - nowUtc.Minute);

                    string dominantSkipName = "Yoxdur";
                    int dominantSkipCount = 0;
                    if (skipCounts.Count > 0)
                    {
                        var topSkip = skipCounts.OrderByDescending(kv => kv.Value).First();
                        dominantSkipName = topSkip.Key;
                        dominantSkipCount = topSkip.Value;
                    }

                    var briefingMsg = TelegramMessageFormatter.FormatBootBriefing(
                        commitHash: commitHash,
                        dataAgeMsBtc: btcDataAgeMs,
                        btcTrend: btcTrend,
                        btcRegime: btcRegime,
                        btcAdx: btcAdx,
                        btcSuperTrendBullish: btcStBullish,
                        btcCandleGreen: btcCandleGreen,
                        ethCandleGreen: ethCandleGreen,
                        ethFilterPassing: ethFilterPassing,
                        last1hAgeMinutes: last1hAgeMinutes,
                        last4hAgeMinutes: last4hAgeMinutes,
                        scannedCount: targetCoins.Count,
                        emittedSignals: emittedSignals,
                        dominantSkipName: dominantSkipName,
                        dominantSkipCount: dominantSkipCount,
                        nextCheckMinutes: nextCheckMinutes
                    );

                    await _telegramService.SendMessageReturnIdAsync(briefingMsg, superAdminId);
                    Console.WriteLine("[StartupMarketCatchUp] Sent SuperAdmin Boot Briefing successfully.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StartupMarketCatchUp] Failed to send boot briefing: {ex.Message}");
            }
        }

        private void HandlePriceUpdate(LivePriceSnapshot snap)
        {
            if (!_coinActiveLocks.ContainsKey(snap.Symbol)) return;

            var now = DateTime.UtcNow;
            if (_lastPriceUpdateHandled.TryGetValue(snap.Symbol, out var lastTime) && (now - lastTime).TotalMilliseconds < 250)
            {
                return;
            }
            _lastPriceUpdateHandled[snap.Symbol] = now;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var openSignals = await unitOfWork.Signals.GetOpenTrackedSignalsAsync();
                    var targetSignals = openSignals.Where(s => s.Symbol == snap.Symbol && !s.IsClosed && !s.OutcomeAlertSent).ToList();
                    foreach (var sig in targetSignals)
                    {
                        await ProcessSignalOutcomeAsync(sig, snap, unitOfWork, CancellationToken.None);
                    }
                }
                catch (Exception _ex) { Console.WriteLine($"[BackgroundMarketScanner] Swallowed exception: {_ex.Message}"); }
            });
        }

        private async Task ProcessSignalOutcomeAsync(FuturesSignal sig, LivePriceSnapshot snap, IUnitOfWork unitOfWork, CancellationToken stoppingToken)
        {
            if (sig.OutcomeAlertSent || sig.IsClosed) return;

            var sem = _signalOutcomeSemaphores.GetOrAdd(sig.Id, _ => new SemaphoreSlim(1, 1));
            if (!await sem.WaitAsync(0))
            {
                return;
            }

            try
            {
                if (sig.OutcomeAlertSent || sig.IsClosed) return;

                // Maksimum ömür yalnız timeframe TTL ilə: 1h -> 8 saat (480 dəq), 4h -> 24 saat (1440 dəq).
                int ttlMinutes = sig.Timeframe == "4h" ? 1440 : 480;
                DateTime expiryUtc = (sig.ExpiryTimeUtc != default && sig.ExpiryTimeUtc > sig.GeneratedAt)
                    ? sig.ExpiryTimeUtc
                    : sig.GeneratedAt.AddMinutes(ttlMinutes);

                // Açıq siqnalların (o cümlədən cari 4h BTC/DOGE) 3 saatda kəsilməməsi üçün timeframe TTL təmin edilir:
                if (sig.Timeframe == "4h" && (expiryUtc - sig.GeneratedAt).TotalMinutes < 1440)
                {
                    expiryUtc = sig.GeneratedAt.AddMinutes(1440);
                    sig.ExpiryTimeUtc = expiryUtc;
                }
                else if (sig.Timeframe == "1h" && (expiryUtc - sig.GeneratedAt).TotalMinutes < 480)
                {
                    expiryUtc = sig.GeneratedAt.AddMinutes(480);
                    sig.ExpiryTimeUtc = expiryUtc;
                }

                bool isMaxTimeReached = DateTime.UtcNow >= expiryUtc;

                var isLong = sig.Direction == SignalDirection.Buy || sig.SignalType.Contains("LONG");

                decimal exitPrice = snap.Last;
                decimal grossPnl = isLong
                    ? Math.Round(((exitPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2)
                    : Math.Round(((sig.EntryPrice - exitPrice) / sig.EntryPrice) * 100, 2);
                decimal netPnl = Math.Round(grossPnl - 0.10m, 2);

                decimal mfe = isLong
                    ? Math.Round(((snap.SessionHigh - sig.EntryPrice) / sig.EntryPrice) * 100, 2)
                    : Math.Round(((sig.EntryPrice - snap.SessionLow) / sig.EntryPrice) * 100, 2);
                decimal mae = isLong
                    ? Math.Round(((sig.EntryPrice - snap.SessionLow) / sig.EntryPrice) * 100, 2)
                    : Math.Round(((snap.SessionHigh - sig.EntryPrice) / sig.EntryPrice) * 100, 2);

                sig.PriceSource = snap.Source;
                sig.ExchangeTsMs = snap.ExchangeTsMs;
                sig.DataAgeMs = snap.DataAgeMs;
                sig.SessionHigh = snap.SessionHigh;
                sig.SessionLow = snap.SessionLow;
                sig.GrossResultPercent = grossPnl;
                sig.NetResultPercent = netPnl;
                sig.MfePercent = mfe;
                sig.MaePercent = mae;

                decimal tickSize = SignalEngine.GetCoinTickSize(sig.EntryPrice);

                if (isLong)
                {
                    // 1. Long TP1 (TP_A 1.0R) Hit - 50% bağlandı + BE Aktivləşdi
                    if (sig.TakeProfit1 > 0 && !sig.Tp1Notified && (snap.SessionHigh >= sig.TakeProfit1 || snap.Last >= (sig.TakeProfit1 - tickSize)))
                    {
                        sig.Tp1Notified = true;
                        sig.IsPartial1Closed = true;
                        sig.RemainingPositionRatio = 0.50m;
                        sig.CloseReason = "TP_A";

                        decimal pnl1 = Math.Round(((sig.TakeProfit1 - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent = Math.Round(0.50m * pnl1, 2);
                        sig.ProfitPercentAchieved = pnl1;

                        // BƏND 3: Qalan 50%: SL-i BE-yə çək YALNIZ TP_A (1.0R) vurulandan SONRA.
                        sig.StopLoss = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 1.0012m);
                        sig.BreakevenTriggered = true;

                        if (sig.TakeProfit2 <= 0 || sig.TakeProfit2 == sig.TakeProfit1)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = exitPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            sig.RemainingPositionRatio = 0m;
                            sig.RealizedProfitPercent = pnl1;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                            sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                            sig.OutcomeStatus = "Hədəf A (TP_A 1.0R) (TAM MƏNFƏƏT) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.0R) (TAM MƏNFƏƏT)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.0R) [50% Qazanc Bağlandı + BE Aktiv]", exitPrice, pnl1);
                            }
                        }
                    }
                    // 2. Long TP2 (TP_B) Hit - Yalnız TP1-dən sonra qalan 50% bağlanır
                    else if (sig.Tp1Notified && !sig.OutcomeAlertSent && sig.TakeProfit2 > 0 && (snap.SessionHigh >= sig.TakeProfit2 || snap.Last >= (sig.TakeProfit2 - tickSize)))
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "TP_B";

                        decimal pnl2 = Math.Round(((sig.TakeProfit2 - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent += Math.Round(sig.RemainingPositionRatio * pnl2, 2);
                        sig.RemainingPositionRatio = 0m;
                        sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                        sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                        sig.OutcomeStatus = "Hədəf B (TP_B) (TAM MƏNFƏƏT) ✅";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TP2";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf B (TP_B) [Qalan 50% Tam Mənfəət]", exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 3. Long Breakeven Hit (YALNIZ TP1-dən sonra qalan 50% BE stopuna dəyərsə)
                    else if (sig.Tp1Notified && !sig.OutcomeAlertSent && !sig.IsClosed && (snap.Last <= sig.StopLoss || snap.SessionLow <= sig.StopLoss))
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "BE";

                        decimal exitPnl = Math.Round(((sig.StopLoss - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent += Math.Round(sig.RemainingPositionRatio * exitPnl, 2);
                        sig.RemainingPositionRatio = 0m;
                        sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);

                        // BƏND 4: BE-yə qayıdıb bağlandı (NEYTRAL, net |PnL|<0.20% win rate-ə yox)
                        sig.Status = SignalStatus.Neutral;
                        string pnlSign = sig.ResultPercent >= 0 ? "+" : "";
                        string pnlFormatted = sig.ResultPercent.HasValue ? sig.ResultPercent.Value.ToString("F2", CultureInfo.InvariantCulture) : "0.00";
                        sig.OutcomeStatus = $"Qorunmuş Breakeven ilə Bağlandı (BE {pnlSign}{pnlFormatted}% NEYTRAL) ⚪";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_BE";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            string beOutcomeText = $"Breakeven (BE {pnlSign}{pnlFormatted}% - NEYTRAL)";
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, beOutcomeText, exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 4. Long Initial Stop Loss Hit (TP1 vurulmadan əvvəl)
                    else if (!sig.Tp1Notified && !sig.OutcomeAlertSent && !sig.IsClosed && (snap.SessionLow <= sig.StopLoss || snap.Last <= sig.StopLoss))
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "SL";
                        sig.Status = SignalStatus.Failed;
                        sig.OutcomeStatus = "Stop Loss (SL) (UĞURSUZ) ❌";
                        sig.ResultPercent = Math.Round(netPnl, 2);

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_SL";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL)", exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 5. Long Time Expiry
                    // 5. Long Time Expiry (Yalnız timeframe TTL çatanda)
                    else if (isMaxTimeReached && !sig.OutcomeAlertSent)
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "TIME";

                        if (sig.Tp1Notified)
                        {
                            decimal remainingGrossPnl = Math.Round(((exitPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                            sig.RealizedProfitPercent += Math.Round(sig.RemainingPositionRatio * remainingGrossPnl, 2);
                            sig.RemainingPositionRatio = 0m;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                            sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                            sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (TP1 Sonrası TIME ilə Tam Bağlandı: +{sig.ResultPercent}%) ⚪";
                        }
                        else
                        {
                            sig.ResultPercent = Math.Round(netPnl, 2);
                            if (netPnl > 0.2m)
                            {
                                sig.Status = SignalStatus.Success;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (Kiçik Bazar Çıxışı: +{netPnl}%) ⚪";
                            }
                            else if (Math.Abs(netPnl) <= 0.2m)
                            {
                                sig.Status = SignalStatus.Neutral;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (Neytral/Konsolidasiya) ⚪";
                            }
                            else
                            {
                                sig.Status = SignalStatus.Failed;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (UĞURSUZ: {netPnl}%) ❌";
                            }
                        }

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TIME";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            decimal finalPnl = sig.ResultPercent ?? netPnl;
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Bitdi", exitPrice, finalPnl);
                        }
                    }
                }
                else // SHORT
                {
                    // 1. Short TP1 (TP_A 1.0R) Hit - 50% bağlandı + BE Aktivləşdi
                    if (sig.TakeProfit1 > 0 && !sig.Tp1Notified && (snap.SessionLow <= sig.TakeProfit1 || snap.Last <= (sig.TakeProfit1 + tickSize)))
                    {
                        sig.Tp1Notified = true;
                        sig.IsPartial1Closed = true;
                        sig.RemainingPositionRatio = 0.50m;
                        sig.CloseReason = "TP_A";

                        decimal pnl1 = Math.Round(((sig.EntryPrice - sig.TakeProfit1) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent = Math.Round(0.50m * pnl1, 2);
                        sig.ProfitPercentAchieved = pnl1;

                        // BƏND 3: Qalan 50%: SL-i BE-yə çək YALNIZ TP_A (1.0R) vurulandan SONRA.
                        sig.StopLoss = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 0.9988m);
                        sig.BreakevenTriggered = true;

                        if (sig.TakeProfit2 <= 0 || sig.TakeProfit2 == sig.TakeProfit1)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = exitPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            sig.RemainingPositionRatio = 0m;
                            sig.RealizedProfitPercent = pnl1;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                            sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                            sig.OutcomeStatus = "Hədəf A (TP_A 1.0R) (TAM MƏNFƏƏT) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.0R) (TAM MƏNFƏƏT)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.0R) [50% Qazanc Bağlandı + BE Aktiv]", exitPrice, pnl1);
                            }
                        }
                    }
                    // 2. Short TP2 (TP_B) Hit - Yalnız TP1-dən sonra qalan 50% bağlanır
                    else if (sig.Tp1Notified && !sig.OutcomeAlertSent && sig.TakeProfit2 > 0 && (snap.SessionLow <= sig.TakeProfit2 || snap.Last <= (sig.TakeProfit2 + tickSize)))
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "TP_B";

                        decimal pnl2 = Math.Round(((sig.EntryPrice - sig.TakeProfit2) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent += Math.Round(sig.RemainingPositionRatio * pnl2, 2);
                        sig.RemainingPositionRatio = 0m;
                        sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                        sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                        sig.OutcomeStatus = "Hədəf B (TP_B) (TAM MƏNFƏƏT) ✅";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TP2";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf B (TP_B) [Qalan 50% Tam Mənfəət]", exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 3. Short Breakeven Hit (YALNIZ TP1-dən sonra qalan 50% BE stopuna dəyərsə)
                    else if (sig.Tp1Notified && !sig.OutcomeAlertSent && !sig.IsClosed && (snap.Last >= sig.StopLoss || snap.SessionHigh >= sig.StopLoss))
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "BE";

                        decimal exitPnl = Math.Round(((sig.EntryPrice - sig.StopLoss) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent += Math.Round(sig.RemainingPositionRatio * exitPnl, 2);
                        sig.RemainingPositionRatio = 0m;
                        sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);

                        // BƏND 4: BE-yə qayıdıb bağlandı (NEYTRAL, net |PnL|<0.20% win rate-ə yox)
                        sig.Status = SignalStatus.Neutral;
                        string pnlSign = sig.ResultPercent >= 0 ? "+" : "";
                        string pnlFormatted = sig.ResultPercent.HasValue ? sig.ResultPercent.Value.ToString("F2", CultureInfo.InvariantCulture) : "0.00";
                        sig.OutcomeStatus = $"Qorunmuş Breakeven ilə Bağlandı (BE {pnlSign}{pnlFormatted}% NEYTRAL) ⚪";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_BE";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            string beOutcomeText = $"Breakeven (BE {pnlSign}{pnlFormatted}% - NEYTRAL)";
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, beOutcomeText, exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 4. Short Initial Stop Loss Hit (TP1 vurulmadan əvvəl)
                    else if (!sig.Tp1Notified && !sig.OutcomeAlertSent && !sig.IsClosed && (snap.SessionHigh >= sig.StopLoss || snap.Last >= sig.StopLoss))
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "SL";
                        sig.Status = SignalStatus.Failed;
                        sig.OutcomeStatus = "Stop Loss (SL) (UĞURSUZ) ❌";
                        sig.ResultPercent = Math.Round(netPnl, 2);

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_SL";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL)", exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 5. Short Time Expiry
                    // 5. Short Time Expiry (Yalnız timeframe TTL çatanda)
                    else if (isMaxTimeReached && !sig.OutcomeAlertSent)
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "TIME";

                        if (sig.Tp1Notified)
                        {
                            decimal remainingGrossPnl = Math.Round(((sig.EntryPrice - exitPrice) / sig.EntryPrice) * 100, 2);
                            sig.RealizedProfitPercent += Math.Round(sig.RemainingPositionRatio * remainingGrossPnl, 2);
                            sig.RemainingPositionRatio = 0m;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                            sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                            sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (TP1 Sonrası TIME ilə Tam Bağlandı: +{sig.ResultPercent}%) ⚪";
                        }
                        else
                        {
                            sig.ResultPercent = Math.Round(netPnl, 2);
                            if (netPnl > 0.2m)
                            {
                                sig.Status = SignalStatus.Success;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (Kiçik Bazar Çıxışı: +{netPnl}%) ⚪";
                            }
                            else if (Math.Abs(netPnl) <= 0.2m)
                            {
                                sig.Status = SignalStatus.Neutral;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (Neytral/Konsolidasiya) ⚪";
                            }
                            else
                            {
                                sig.Status = SignalStatus.Failed;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (UĞURSUZ: {netPnl}%) ❌";
                            }
                        }

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TIME";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            decimal finalPnl = sig.ResultPercent ?? netPnl;
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Bitdi", exitPrice, finalPnl);
                        }
                    }
                }

                if (sig.IsClosed)
                {
                    // Yalnız real Stop Loss streak-i breaker-ə düşür.
                    // TIME / NO_EDGE / BE / ALERT_NEVER_SENT Failed sayılsa belə breaker-ə GETMƏSİN.
                    bool isHardStopLoss = sig.CloseReason == "SL" || sig.CloseReason == "SL_RESTART_CATCHUP";
                    if (sig.Status == SignalStatus.Failed && isHardStopLoss)
                    {
                        int losses = Interlocked.Increment(ref _consecutiveLosses);
                        if (losses >= 2)
                        {
                            _circuitBreakerUntil = DateTime.UtcNow.AddHours(4);

                            if ((DateTime.UtcNow - _lastCircuitBreakerAlertSent).TotalMinutes >= 60)
                            {
                                _lastCircuitBreakerAlertSent = DateTime.UtcNow;
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await _telegramService.BroadcastSystemAlertAsync("⚠️ <b>RISK CIRCUIT BREAKER AKTİVLƏŞDİ:</b>\n\n" +
                                            "Ardıcıl 2 uğursuz əməliyyat (Stop Loss) qeydə alındı. Bazar skaneri kapitalı qorumaq üçün <b>4 saatlıq</b> müşahidə rejiminə keçdi.");
                                    }
                                    catch (Exception _ex) { Console.WriteLine($"[BackgroundMarketScanner] Swallowed exception: {_ex.Message}"); }
                                });
                            }
                        }
                    }
                    else if (sig.CloseReason == "TP_B" || sig.CloseReason == "TP3")
                    {
                        Interlocked.Exchange(ref _consecutiveLosses, 0);
                    }

                    var cooldownMinutes = sig.Timeframe switch
                    {
                        "1m" => 10,
                        "3m" => 25,
                        "5m" => 35,
                        "15m" => 60,
                        "1h" => 120,
                        "4h" => 240,
                        _ => 35
                    };
                    _coinCooldowns[sig.Symbol] = DateTime.UtcNow.AddMinutes(cooldownMinutes);
                    _coinActiveLocks.TryRemove(sig.Symbol, out _);

                    bool hasOther = await unitOfWork.Signals.HasActiveSignalForSymbolAsync(sig.Symbol);
                    if (!hasOther && !TelegramBotService.Default40Coins.Contains(sig.Symbol, StringComparer.OrdinalIgnoreCase))
                    {
                        _wsClient.Unsubscribe(sig.Symbol);
                    }
                }
            }
            finally
            {
                sem.Release();
            }
        }

        private async Task CheckCandleInvalidationAndTrailingAsync(
            FuturesSignal sig,
            IMarketDataProvider marketData,
            IIndicatorEngine indicatorEngine,
            IUnitOfWork unitOfWork,
            ISignalEngine signalEngine,
            decimal currentLast,
            CancellationToken stoppingToken)
        {
            if (sig.IsClosed || sig.OutcomeAlertSent) return;

            var isLong = sig.Direction == SignalDirection.Buy || sig.SignalType.Contains("LONG");
            decimal exitPrice = currentLast;
            decimal grossPnl = isLong
                ? Math.Round(((exitPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2)
                : Math.Round(((sig.EntryPrice - exitPrice) / sig.EntryPrice) * 100, 2);
            decimal netPnl = Math.Round(grossPnl - 0.10m, 2);

            // BƏND E: Alt LONG üçün BTC əks rejimi aşkarlananda dərhal çıxış (ETH daxil, BTCUSDT istisna)
            if (isLong && !sig.Symbol.Equals("BTCUSDT", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var btcCompass = await signalEngine.GetBtcCompassAsync();
                    if (btcCompass != null && btcCompass.Regime == BtcMarketRegime.Bearish && !btcCompass.IsBtc4hSuperTrendBullish)
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "INVALIDATION";
                        sig.ResultPercent = netPnl;
                        sig.Status = (Math.Abs(netPnl) <= 0.20m) ? SignalStatus.Neutral : SignalStatus.Failed;
                        sig.OutcomeStatus = "BTC əks rejim (INVALIDATION) ❌";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);
                        if (sig.SignalAlertSent)
                        {
                            await _telegramService.SendOutcomeAlertAsync(sig, "BTC əks — çıxış", exitPrice, netPnl);
                        }
                        return;
                    }
                }
                catch (Exception btcEx)
                {
                    Console.WriteLine($"[EarlyExit] Error checking BTC compass for {sig.Symbol}: {btcEx.Message}");
                }
            }

            // Simvol başına max 1 kline / 15m throttle
            // Yalnız 15m şam qapanışında və ya ən tez 30s-dən bir yoxla
            var now = DateTime.UtcNow;
            if (sig.LastObservedCandleTime != default && (now - sig.LastObservedCandleTime).TotalSeconds < 30)
            {
                return;
            }

            List<Kline> rawKlines;
            try
            {
                rawKlines = await marketData.GetKlinesAsync(sig.Symbol, sig.Timeframe, 60);
            }
            catch
            {
                return;
            }

            if (rawKlines == null || rawKlines.Count < 25) return;

            // Son qapalı şamlar: forming candle-i çıxar
            var closedCandles = rawKlines.Take(rawKlines.Count - 1).ToList();
            if (closedCandles.Count < 20) return;

            var lastClosed = closedCandles[^1];
            var lastCandleTime = DateTimeOffset.FromUnixTimeMilliseconds(lastClosed.OpenTime).UtcDateTime;

            // Əgər yeni şam qapanmayıbsa, təkrar hesablama
            if (sig.LastObservedCandleTime != default && lastCandleTime <= sig.LastObservedCandleTime)
            {
                return;
            }

            sig.LastObservedCandleTime = lastCandleTime;
            sig.CandlesObserved++;

            // NO_EDGE (ölü edge) — timeframe-nisbi: (1h: 4 şam = 4 saat) və ya (4h: 3 şam = 12 saat)
            // YALNIZ: MFE < 0.4R VƏ TP_A hit olmayıb.
            // Vaxt kill TƏTBİQ OLUNMASIN əgər: MFE >= 0.4R VƏ ya qiymət TP_A-ya yaxındır / artıq +R-dədir VƏ ya TP_A artıq vurulub.
            decimal riskRPct = (sig.InitialRiskR > 0 && sig.EntryPrice > 0)
                ? (sig.InitialRiskR / sig.EntryPrice) * 100m
                : (Math.Abs(sig.EntryPrice - sig.StopLoss) / (sig.EntryPrice > 0 ? sig.EntryPrice : 1m)) * 100m;

            int requiredNoEdgeCandles = sig.Timeframe == "4h" ? 3 : 4;
            decimal tickSize = SignalEngine.GetCoinTickSize(sig.EntryPrice);

            bool isNearTpA = isLong
                ? (sig.TakeProfit1 > 0 && currentLast >= (sig.TakeProfit1 - (5 * tickSize)))
                : (sig.TakeProfit1 > 0 && currentLast <= (sig.TakeProfit1 + (5 * tickSize)));

            bool canTriggerNoEdge;
            if (sig.Timeframe == "1h")
            {
                decimal hoursOpen = (decimal)(DateTime.UtcNow - sig.GeneratedAt).TotalHours;
                decimal absMove = sig.EntryPrice > 0 ? (Math.Abs(currentLast - sig.EntryPrice) / sig.EntryPrice) : 1m;
                canTriggerNoEdge = !sig.Tp1Notified
                                && !sig.OutcomeAlertSent
                                && !sig.IsClosed
                                && sig.EntryPrice > 0
                                && sig.InitialRiskR > 0
                                && hoursOpen >= 3.0m
                                && sig.MfePercent < (0.40m * riskRPct)
                                && absMove <= 0.0035m;
            }
            else
            {
                bool isPositiveR = grossPnl > 0;
                canTriggerNoEdge = sig.CandlesObserved >= requiredNoEdgeCandles
                                && sig.MfePercent < (0.4m * riskRPct)
                                && !sig.Tp1Notified
                                && !sig.IsPartial1Closed
                                && !isNearTpA
                                && !isPositiveR;
            }

            if (canTriggerNoEdge)
            {
                sig.OutcomeAlertSent = true;
                sig.IsClosed = true;
                sig.ClosePrice = exitPrice;
                sig.ClosedAt = DateTime.UtcNow;
                sig.ResultPercent = Math.Round(netPnl, 2);
                if (Math.Abs(netPnl) <= 0.20m)
                {
                    sig.Status = SignalStatus.Neutral;
                    sig.OutcomeStatus = $"{sig.Timeframe} Hərəkətsiz (NO_EDGE Neytral: {netPnl}%) ⚪";
                }
                else
                {
                    sig.Status = SignalStatus.Failed;
                    sig.OutcomeStatus = $"{sig.Timeframe} Hərəkətsiz (NO_EDGE Donma çıxışı: {netPnl}%) ❌";
                }
                sig.CloseReason = "NO_EDGE";

                await unitOfWork.Signals.UpdateAsync(sig);
                await unitOfWork.SaveChangesAsync(stoppingToken);
                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "NO_EDGE (Donma çıxışı)", exitPrice, netPnl);
                return;
            }

            // Problem 5: Invalidation
            // LONG bağla: close < siqnal swing low VƏ [(SuperTrend flip VƏ RSI close<50) VEYA (2 ardıcıl əks şam VƏ vol>1.3×SMA20)].
            // SHORT güzgü (swing high, RSI>50).
            var ind = indicatorEngine.CalculateIndicators(closedCandles);
            var volSma20 = closedCandles.TakeLast(20).Average(c => c.Volume);

            if (isLong && sig.SignalSwingLow > 0)
            {
                bool belowSwing = lastClosed.Close < sig.SignalSwingLow;
                bool superTrendFlip = ind.SuperTrendDirection.Contains("BEARISH") || ind.SuperTrendVote == IndicatorVote.Bearish;
                bool rsiUnder50 = ind.Rsi < 50m;
                bool condA = superTrendFlip && rsiUnder50;

                bool prevRed = closedCandles.Count >= 2 && closedCandles[^2].Close < closedCandles[^2].Open;
                bool currRed = lastClosed.Close < lastClosed.Open;
                bool twoOpposite = prevRed && currRed;
                bool volSurge = volSma20 > 0 && lastClosed.Volume > 1.3m * volSma20;
                bool condB = twoOpposite && volSurge;

                if (belowSwing && (condA || condB))
                {
                    sig.OutcomeAlertSent = true;
                    sig.IsClosed = true;
                    sig.ClosePrice = exitPrice;
                    sig.ClosedAt = DateTime.UtcNow;
                    sig.Status = SignalStatus.Failed;
                    sig.CloseReason = "INVALIDATION";
                    sig.ResultPercent = netPnl;
                    sig.OutcomeStatus = "Struktur Pozuldu (INVALIDATION) ❌";

                    await unitOfWork.Signals.UpdateAsync(sig);
                    await unitOfWork.SaveChangesAsync(stoppingToken);
                    if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Struktur Pozuldu (INVALIDATION)", exitPrice, netPnl);
                    return;
                }
            }
            else if (!isLong && sig.SignalSwingHigh > 0)
            {
                bool aboveSwing = lastClosed.Close > sig.SignalSwingHigh;
                bool superTrendFlip = ind.SuperTrendDirection.Contains("BULLISH") || ind.SuperTrendVote == IndicatorVote.Bullish;
                bool rsiAbove50 = ind.Rsi > 50m;
                bool condA = superTrendFlip && rsiAbove50;

                bool prevGreen = closedCandles.Count >= 2 && closedCandles[^2].Close > closedCandles[^2].Open;
                bool currGreen = lastClosed.Close > lastClosed.Open;
                bool twoOpposite = prevGreen && currGreen;
                bool volSurge = volSma20 > 0 && lastClosed.Volume > 1.3m * volSma20;
                bool condB = twoOpposite && volSurge;

                if (aboveSwing && (condA || condB))
                {
                    sig.OutcomeAlertSent = true;
                    sig.IsClosed = true;
                    sig.ClosePrice = exitPrice;
                    sig.ClosedAt = DateTime.UtcNow;
                    sig.Status = SignalStatus.Failed;
                    sig.CloseReason = "INVALIDATION";
                    sig.ResultPercent = netPnl;
                    sig.OutcomeStatus = "Struktur Pozuldu (INVALIDATION) ❌";

                    await unitOfWork.Signals.UpdateAsync(sig);
                    await unitOfWork.SaveChangesAsync(stoppingToken);
                    if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Struktur Pozuldu (INVALIDATION)", exitPrice, netPnl);
                    return;
                }
            }

            // Problem 6: TP2-dən sonra trail son 3 qapalı swing ± 0.3*ATR
            if (sig.Tp2Notified && !sig.OutcomeAlertSent && !sig.IsClosed)
            {
                decimal atrVal = ind.Atr > 0 ? ind.Atr : (sig.EntryPrice * (sig.AtrPercent > 0 ? sig.AtrPercent / 100m : 0.01m));
                if (isLong)
                {
                    // Son 3 qapalı swing low tap
                    var recentLows = new List<decimal>();
                    for (int i = closedCandles.Count - 3; i >= 2 && recentLows.Count < 3; i--)
                    {
                        if (closedCandles[i].Low <= closedCandles[i - 1].Low && closedCandles[i].Low <= closedCandles[i - 2].Low &&
                            closedCandles[i].Low <= closedCandles[i + 1].Low && closedCandles[i].Low <= closedCandles[i + 2].Low)
                        {
                            recentLows.Add(closedCandles[i].Low);
                        }
                    }
                    if (recentLows.Count > 0)
                    {
                        decimal newTrail = recentLows.Max() - (0.3m * atrVal);
                        newTrail = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, newTrail);
                        if (newTrail > sig.TrailPrice && newTrail >= sig.TakeProfit1)
                        {
                            sig.TrailPrice = newTrail;
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                        }
                    }
                }
                else
                {
                    // Son 3 qapalı swing high tap
                    var recentHighs = new List<decimal>();
                    for (int i = closedCandles.Count - 3; i >= 2 && recentHighs.Count < 3; i--)
                    {
                        if (closedCandles[i].High >= closedCandles[i - 1].High && closedCandles[i].High >= closedCandles[i - 2].High &&
                            closedCandles[i].High >= closedCandles[i + 1].High && closedCandles[i].High >= closedCandles[i + 2].High)
                        {
                            recentHighs.Add(closedCandles[i].High);
                        }
                    }
                    if (recentHighs.Count > 0)
                    {
                        decimal newTrail = recentHighs.Min() + (0.3m * atrVal);
                        newTrail = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, newTrail);
                        if ((sig.TrailPrice == 0 || newTrail < sig.TrailPrice) && newTrail <= sig.TakeProfit1)
                        {
                            sig.TrailPrice = newTrail;
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                        }
                    }
                }
            }
        }

        private async Task ScanMarketSignalsAsync(CancellationToken stoppingToken)
        {
            var nowUtcStart = DateTime.UtcNow;
            var last1hCloseUtc = new DateTime(nowUtcStart.Year, nowUtcStart.Month, nowUtcStart.Day, nowUtcStart.Hour, 0, 0, DateTimeKind.Utc);
            var last1hAgeMin = Math.Round((nowUtcStart - last1hCloseUtc).TotalMinutes, 1);
            bool isBootWindow = (nowUtcStart - SignalEngine.ProcessStartTimeUtc).TotalMinutes <= 15;
            var btcSnapStart = _livePriceCache.GetSnapshot("BTCUSDT");
            var wsAgeMs = btcSnapStart?.DataAgeMs ?? -1;

            // DIAQ: Skan dövrəsi başladı — Railway logunda bu sətri görməyənlər skaner ÖLÜDÜR
            Console.WriteLine($"[SCAN_CYCLE_START] {nowUtcStart:HH:mm:ss}UTC coinsLocked={_coinActiveLocks.Count} cbUntil={(nowUtcStart < _circuitBreakerUntil ? _circuitBreakerUntil.ToString("HH:mm:ss") : "none")} bootWindow={isBootWindow} wsAgeMs={wsAgeMs} last1hAgeMin={last1hAgeMin}");

            // Prioritet 4: Circuit breaker aktivdirsə, yeni skan dayandırılır
            if (DateTime.UtcNow < _circuitBreakerUntil)
            {
                Interlocked.Increment(ref _hourlyTelemetry.SkipCircuitBreaker);
                Console.WriteLine($"[SCAN_CYCLE_SKIP] reason=CIRCUIT_BREAKER cbUntil={_circuitBreakerUntil:HH:mm:ss}UTC");
                return;
            }
            else if (_circuitBreakerUntil != DateTime.MinValue)
            {
                Interlocked.Exchange(ref _consecutiveLosses, 0);
                _circuitBreakerUntil = DateTime.MinValue;
            }

            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

            var openTradesCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();

            // Portfolio-level risk management: Max 20 concurrent active positions across entire market!
            if (openTradesCount >= MaxGlobalOpenPositions || _coinActiveLocks.Count >= MaxGlobalOpenPositions)
            {
                Interlocked.Increment(ref _hourlyTelemetry.SkipMaxOpen);
                Console.WriteLine($"[SCAN_CYCLE_SKIP] reason=MAX_OPEN count={openTradesCount} activeLocks={_coinActiveLocks.Count} max={MaxGlobalOpenPositions}");
                return;
            }

            // Prioritet 4: Günlük -3.0% itki limiti çatdıqda yeni əməliyyat açılmır (Bakı vaxtı 00:00 ilə)
            try
            {
                var bakuDayStartUtc = DateTime.UtcNow.AddHours(4).Date.AddHours(-4);
                var todaySignals = await unitOfWork.Signals.GetSignalsSinceAsync(bakuDayStartUtc);
                var todayClosedPnL = todaySignals
                    .Where(s => (s.IsClosed || s.Status != SignalStatus.Open) && s.ResultPercent.HasValue)
                    .Sum(s => s.ResultPercent!.Value);

                if (todayClosedPnL <= -3.0m)
                {
                    Interlocked.Increment(ref _hourlyTelemetry.SkipDailyLoss);
                    Console.WriteLine($"[SCAN_CYCLE_SKIP] reason=DAILY_LOSS pnl={todayClosedPnL:F2}% threshold=-3.0%");
                    return;
                }
            }
            catch (Exception _ex) { Console.WriteLine($"[BackgroundMarketScanner] Swallowed exception: {_ex.Message}"); }

            var activeTimeframes = new HashSet<string>();
            var subscribedCoins = new HashSet<string>();

            foreach (var s in TelegramBotService.UserPreferences.Values)
            {
                if (s.IsActive)
                {
                    if (s.Timeframe == "Hamısı" || s.Timeframe == "Hamisi")
                    {
                        activeTimeframes.Add("1h");
                        activeTimeframes.Add("4h");
                    }
                    else if (!string.IsNullOrWhiteSpace(s.Timeframe) && s.Timeframe != "15m")
                    {
                        activeTimeframes.Add(s.Timeframe);
                    }

                    if (s.Coins != null && s.Coins.Count > 0)
                    {
                        foreach (var c in s.Coins)
                        {
                            // SYMBOL NORMALIZE: "BTC" → "BTCUSDT", "ETHUSDT" → "ETHUSDT"
                            var normalized = c.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
                                ? c.ToUpperInvariant()
                                : c.ToUpperInvariant() + "USDT";
                            subscribedCoins.Add(normalized);
                        }
                    }
                }
            }

            // Always ensure all 40 institutional coins are scanned if user list is empty or default
            if (subscribedCoins.Count == 0)
            {
                foreach (var c in TelegramBotService.Default40Coins)
                {
                    subscribedCoins.Add(c);
                }
            }

            var top80Tickers = await marketData.GetTopFuturesTickersAsync(80);
            var top80Set = top80Tickers.Select(t => t.Symbol).ToHashSet();

            var targetCoins = subscribedCoins.ToList();
            if (targetCoins.Count > 80)
            {
                targetCoins = targetCoins.Where(c => top80Set.Contains(c) || top80Set.Contains("1000" + c)).Take(80).ToList();
            }

            if (activeTimeframes.Count == 0)
            {
                activeTimeframes.Add("1h");
                activeTimeframes.Add("4h");
            }

            // Qayda 1 & Qayda 2: Filter coins at the root level before launching any analysis
            var coinsToScan = new List<string>();
            foreach (var sym in targetCoins)
            {
                var scanNowUtc = DateTime.UtcNow;
                // Kline throttling: max 1 kline analysis per 30s to prevent 429
                if (_coinLastScanTime.TryGetValue(sym, out var lastScan) && (scanNowUtc - lastScan).TotalSeconds < 30)
                {
                    continue;
                }

                // Strict Coin-Level Rule 1: Post-trade cooldown active?
                if (_coinCooldowns.TryGetValue(sym, out var cooldownUntil) && scanNowUtc < cooldownUntil)
                {
                    Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                    continue;
                }

                // Strict Coin-Level Rule 2: Does this coin ALREADY have ANY open unclosed position across ANY timeframe?
                if (_coinActiveLocks.ContainsKey(sym) || _scanningCoins.ContainsKey(sym))
                {
                    Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                    continue;
                }

                if (await unitOfWork.Signals.HasActiveSignalForSymbolAsync(sym))
                {
                    _coinActiveLocks.TryAdd(sym, 1);
                    Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                    continue;
                }

                _coinLastScanTime[sym] = scanNowUtc;
                coinsToScan.Add(sym);
            }

            if (coinsToScan.Count > 0)
            {
                // Scan by COIN in parallel (eliminates multiple threads scanning the same coin simultaneously via _scanningCoins optimistic lock)
                await Parallel.ForEachAsync(coinsToScan, new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = stoppingToken }, async (sym, ct) =>
                {
                    if (_coinActiveLocks.ContainsKey(sym) || !_scanningCoins.TryAdd(sym, 1))
                    {
                        Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                        return;
                    }

                    try
                    {
                        // Check if portfolio limit was reached by another parallel thread
                        if (_coinActiveLocks.Count >= MaxGlobalOpenPositions)
                        {
                            Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                            return;
                        }

                        using var innerScope = _serviceProvider.CreateScope();
                        var engine = innerScope.ServiceProvider.GetRequiredService<ISignalEngine>();
                        var uow = innerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                        var marketData = innerScope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

                        if (await uow.Signals.HasActiveSignalForSymbolAsync(sym))
                        {
                            _coinActiveLocks.TryAdd(sym, 1);
                            Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                            return;
                        }

                        // Bağlanandan sonra minimum 1 tam şam boşluq (1h: 60 dəqiqə, 4h: 240 dəqiqə)
                        var lastClosed = await uow.Signals.GetLastClosedSignalForSymbolAsync(sym);
                        if (lastClosed != null && lastClosed.ClosedAt.HasValue)
                        {
                            var cooldownRequired = lastClosed.Timeframe == "4h" ? TimeSpan.FromHours(4) : TimeSpan.FromHours(1);
                            if (DateTime.UtcNow - lastClosed.ClosedAt.Value < cooldownRequired)
                            {
                                Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                                return;
                            }
                        }

                        // Qızıl Qayda: Evaluate in order of institutional significance (1h -> 4h)
                        var prioritizedTfs = new[] { "1h", "4h" }
                            .Where(tf => activeTimeframes.Contains(tf))
                            .ToList();

                        if (prioritizedTfs.Count == 0)
                        {
                            prioritizedTfs = new List<string> { "1h", "4h" };
                        }

                        foreach (var tf in prioritizedTfs)
                        {
                            if (_coinActiveLocks.ContainsKey(sym))
                            {
                                Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                                break;
                            }
                            if (_coinActiveLocks.Count >= MaxGlobalOpenPositions)
                            {
                                Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                                break;
                            }

                            Interlocked.Increment(ref _hourlyTelemetry.CoinsScanned);

                            var signal = await engine.AnalyzeCoinAsync(sym, tf, isLiveScan: true);

                            // Telemetriya: Confluence, GÖZLƏMƏ, BTC Gate, Range ayrı sayğaclar (indiki Conf-un içində itməsin)
                            bool isTradeQualified = signal.Timeframe != "15m" && signal.Confidence >= 75 &&
                                                    signal.SignalType != null &&
                                                    (signal.SignalType.Contains("LONG") || signal.SignalType.Contains("SHORT"));
                            if (!isTradeQualified)
                            {
                                bool hasBtcBearLong = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("SKIP_BTC_BEAR_LONG"));
                                bool hasBtcRange = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("SKIP_BTC_RANGE"));
                                bool hasBtc4hOppose = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("SKIP_BTC_4H_OPPOSE"));
                                bool hasPureConfluence = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("Confluence Filtri") || r.Contains("< 75.0%"));

                                if (hasBtcBearLong)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipBtcBearLong);
                                }
                                if (hasBtcRange)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipBtcRange);
                                }
                                if (hasBtc4hOppose)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipBtc4hOppose);
                                }

                                if (hasPureConfluence && !hasBtcBearLong && !hasBtcRange && !hasBtc4hOppose)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipConfluence);
                                }
                                else if (!hasBtcBearLong && !hasBtcRange && !hasBtc4hOppose)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipGozleme);
                                }
                                else if (signal.SignalType != null && signal.SignalType.Contains("GÖZLƏMƏ"))
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipGozleme);
                                }
                            }

                            // Telemetriya: S/R və Chase filtrlərini sayğaca əlavə et
                            if (signal.AnalysisReasons != null && signal.AnalysisReasons.Count > 0)
                            {
                                foreach (var r in signal.AnalysisReasons)
                                {
                                    if (r.Contains("SKIP_CHASE") || r.Contains("Chase")) Interlocked.Increment(ref _hourlyTelemetry.SkipChase);
                                    if (r.Contains("SKIP_SL_TOO_WIDE") || r.Contains("SL too wide")) Interlocked.Increment(ref _hourlyTelemetry.SkipSL);
                                    if (r.Contains("SKIP_LOW_RR") || r.Contains("R:R")) Interlocked.Increment(ref _hourlyTelemetry.SkipRR);
                                }
                            }

                            // Overfilter diaqnostika: BLOCK olanları qeyd et
                            if (!isTradeQualified)
                            {
                                var skipReason = (signal.AnalysisReasons != null && signal.AnalysisReasons.Count > 0)
                                    ? signal.AnalysisReasons[0]
                                    : $"conf={signal.ConfluenceScore:F1}%";
                                _overfilterDiag[$"{sym}_{tf}"] = ((double)signal.ConfluenceScore, skipReason);
                            }

                            // Telemetriya: Hər analiz olunan coin üçün real log
                            var aztNow = CryptoSense.Domain.Common.TimeHelper.NowFormatted;
                            var indRes = signal.Indicators as CryptoSense.Application.DTOs.IndicatorResult;
                            decimal adxVal = indRes?.Adx ?? 0m;
                            bool isTradeSignal = signal.Timeframe != "15m" && signal.Confidence >= 75 && (signal.SignalType?.Contains("LONG") == true || signal.SignalType?.Contains("SHORT") == true);
                            string resultStatus = isTradeSignal ? "PASS" : "BLOCK";
                            string reasonDesc = isTradeSignal
                                ? (signal.SignalType ?? "LONG/SHORT")
                                : ((signal.AnalysisReasons != null && signal.AnalysisReasons.Count > 0) ? signal.AnalysisReasons[0] : (signal.SignalType ?? "GÖZLƏMƏ"));
                            Console.WriteLine($"[SCAN] {aztNow} {sym} tf={tf} confluence={signal.ConfluenceScore:F1}% adx={adxVal:F1} result={resultStatus} reason={reasonDesc}");

                            // ⚠️ ABNORMAL VOLATILITY / EXTREME RISK ALERT
                            if (signal.SignalType == "YÜKSƏK_VOLATİLLİK_RİSK")
                            {
                                var volKey = $"{signal.Symbol}_volatility";
                                if (!_lastVolatilityAlertSent.TryGetValue(volKey, out var lastSent) || (DateTime.UtcNow - lastSent).TotalMinutes >= 45)
                                {
                                    _lastVolatilityAlertSent[volKey] = DateTime.UtcNow;
                                    var reasonText = (signal.AnalysisReasons != null && signal.AnalysisReasons.Count > 0) ? signal.AnalysisReasons[0] : "Kəskin dalğalanma və spayklar aşkarlandı";
                                    var ticker = await marketData.Get24hTickerAsync(signal.Symbol);
                                    decimal chg24 = ticker?.PriceChangePercent ?? 0m;
                                    var ind = signal.Indicators as CryptoSense.Application.DTOs.IndicatorResult;
                                    decimal volRatio = ind?.VolumeSurgeRatio ?? 0m;
                                    if (volRatio <= 0 && signal.CurrentPrice > 0 && ind?.Atr > 0)
                                    {
                                        volRatio = Math.Round((ind.Atr / (signal.CurrentPrice * 0.012m)), 1);
                                    }

                                    if (Math.Abs(chg24) > 0.001m && volRatio >= 2.5m)
                                    {
                                        await _telegramService.SendVolatilityRiskAlertAsync(signal.Symbol, signal.CurrentPrice, chg24, volRatio, reasonText);
                                    }
                                }
                                break;
                            }

                            // HIGH-CONVICTION TRADE DISPATCH (Faza A: Live WebSocket Price, Emit Lag & Stale Filter, Atomic Send-Success Numbering)
                            if (signal.Timeframe != "15m" && signal.Confidence >= 75 && signal.SignalType != null && (signal.SignalType.Contains("LONG") || signal.SignalType.Contains("SHORT")))
                            {
                                var candleDuration = signal.Timeframe switch
                                {
                                    "4h" => TimeSpan.FromHours(4),
                                    _ => TimeSpan.FromHours(1)
                                };
                                var candleCloseUtc = signal.SourceCandleOpenTimeUtc + candleDuration;

                                // (2) 1h/4h PƏNCƏRƏ: emitTs - candleCloseTime > maxAllowedLagMs skip (SKIP_CYCLE_LAG)
                                // 1h: 50 dəqiqəyə qədər (3,000,000 ms), 4h: 3 saata qədər (10,800,000 ms)
                                var maxAllowedLagMs = signal.Timeframe switch
                                {
                                    "4h" => 10800000, // 3 saat
                                    _ => isBootWindow ? 5400000 : 3000000 // 90 dəqiqə (boot) / 50 dəqiqə
                                };
                                var emitLagMs = (DateTime.UtcNow - candleCloseUtc).TotalMilliseconds;
                                if (emitLagMs > maxAllowedLagMs)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipLag);
                                    SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                    Console.WriteLine($"[MarketScanner] SKIP_CYCLE_LAG: {signal.Symbol} lag={emitLagMs:F0}ms > {maxAllowedLagMs}ms");
                                    Console.WriteLine($"[EMIT_RETRY_ARMED] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} reason=CYCLE_LAG");
                                    continue;
                                }

                                // (3) WS Live Price Snapshot (əvvəlcədən abunə olunduğu üçün gecikmədən birbaşa yoxlanılır)
                                var snap = _livePriceCache.GetSnapshot(signal.Symbol);

                                // QAYDA 3: DataAge > 1000ms olarsa, REST ilə dərhal ən son ticarət qiyməti çəkilir və DataAge yenilənir
                                if (snap == null || snap.DataAgeMs > 1000)
                                {
                                    try
                                    {
                                        var lastAgg = await marketData.GetLastAggTradeAsync(signal.Symbol);
                                        if (lastAgg.HasValue)
                                        {
                                            _livePriceCache.UpdateFromAggTrade(signal.Symbol, lastAgg.Value.Price, lastAgg.Value.ExchangeTsMs, isRestFallback: false);
                                            snap = _livePriceCache.GetSnapshot(signal.Symbol);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[MarketScanner] Live price refresh error for {signal.Symbol}: {ex.Message}");
                                    }
                                }

                                // SKIP_STALE: Əgər hələ də yoxdursa və ya DataAge > 3500ms-dirsə: köhnə qiymətlə kart QƏTİ GÖNDƏRİLMƏSİN!
                                if (snap == null || snap.DataAgeMs > 3500)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipStale);
                                    SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                    Console.WriteLine($"[MarketScanner] SKIP_STALE: {signal.Symbol} dataAgeMs={(snap?.DataAgeMs ?? -1)} source={snap?.Source}");
                                    Console.WriteLine($"[EMIT_RETRY_ARMED] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} reason=STALE");
                                    continue;
                                }

                                // (5) EntryPrice=Last, CurrentPrice=Last (canlı qiymət əks olunsun, köhnə şam bağlanışı yox!)
                                signal.EntryPrice = snap.Last;
                                signal.CurrentPrice = snap.Last;
                                signal.PriceSource = snap.Source;
                                signal.ExchangeTsMs = snap.ExchangeTsMs;
                                signal.DataAgeMs = snap.DataAgeMs;
                                signal.CandleCloseTimeUtc = candleCloseUtc;

                                _livePriceCache.ResetSession(signal.Symbol);

                                // (6) BƏND 2 & BƏND 3: Risk:Reward & Target Gate:
                                decimal tp1Dist = Math.Abs(signal.TakeProfit1 - signal.EntryPrice);
                                decimal slDist = Math.Abs(signal.StopLoss - signal.EntryPrice);
                                decimal slPct = signal.EntryPrice > 0 ? (slDist / signal.EntryPrice) * 100m : 0m;

                                // BƏND 2: 1h SL məsafəsi > 2.8% → kart AçMA (ölçünü sıxmaq yox, treydi keç)
                                // 4h: ATR(4h), swing 4–6 × 4h (AAVE 1.87 ATR / 2.40% saxla, max 4.0%)
                                decimal maxSlPct = signal.Timeframe == "4h" ? 4.00m : 2.80m;
                                if (slPct > maxSlPct)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipSL);
                                    SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                    Console.WriteLine($"[MarketScanner] SL too wide: {signal.Symbol} ({signal.Timeframe}) sl%={slPct:F2}% > {maxSlPct:F2}%. Trade skipped.");
                                    Console.WriteLine($"[EMIT_RETRY_ARMED] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} reason=SL");
                                    continue;
                                }

                                // BƏND 3: Weighted R:R = TP_A (1.0R - 50%) + TP_B (min(2.0R, struct) - 50%)
                                decimal tpBDist = signal.TakeProfit2 > 0 ? Math.Abs(signal.TakeProfit2 - signal.EntryPrice) : tp1Dist;
                                decimal weightedTpDist = (0.50m * tp1Dist) + (0.50m * tpBDist);
                                decimal effectiveRr = slDist > 0 ? (weightedTpDist / slDist) : 0m;

                                // R:R < 1.30 (YENİ SL ilə hesabla) → kart yox
                                if (effectiveRr < 1.30m)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipRR);
                                    SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                    Console.WriteLine($"[MarketScanner] R:R filter blocked: {signal.Symbol} effectiveRr={effectiveRr:F2} < 1.30");
                                    Console.WriteLine($"[EMIT_RETRY_ARMED] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} reason=RR");
                                    continue;
                                }

                                if (signal.TakeProfit2 > 0 && signal.TakeProfit2 == signal.TakeProfit1)
                                {
                                    signal.TakeProfit2 = 0m;
                                }
                                if (signal.TakeProfit3 > 0 && (signal.TakeProfit3 == signal.TakeProfit2 || signal.TakeProfit3 == signal.TakeProfit1))
                                {
                                    signal.TakeProfit3 = 0m;
                                }

                                await _sendSemaphore.WaitAsync(ct);
                                try
                                {
                                    // BƏND 5: Həmin Symbol açıqdırsa (istənilən TF) yeni kart YOX
                                    if (_coinActiveLocks.ContainsKey(sym))
                                    {
                                        Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                                        break;
                                    }
                                    if (_coinActiveLocks.Count >= MaxGlobalOpenPositions)
                                    {
                                        Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                                        break;
                                    }
                                    if (await uow.Signals.HasActiveSignalForSymbolAsync(sym))
                                    {
                                        _coinActiveLocks.TryAdd(sym, 1);
                                        Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                                        break;
                                    }

                                    // BƏND 5: 1 şam boşluq (cooldown) və bağlandıqdan sonra 6 saat eyni istiqamətə yenidən 1h YOX
                                    var lastClosedSig = await uow.Signals.GetLastClosedSignalForSymbolAsync(sym);
                                    if (lastClosedSig?.ClosedAt != null)
                                    {
                                        // BƏND 5: Bağlandıqdan sonra 6 saat eyni istiqamətə yenidən 1h YOX
                                        if (signal.Timeframe == "1h" && lastClosedSig.Direction == signal.Direction)
                                        {
                                            if (DateTime.UtcNow - lastClosedSig.ClosedAt.Value < TimeSpan.FromHours(6))
                                            {
                                                Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                                                Console.WriteLine($"[MarketScanner] 6-hour same direction cooldown active for {sym} ({signal.Direction}). Skipping.");
                                                break;
                                            }
                                        }

                                        var candleCooldown = signal.Timeframe == "4h" ? TimeSpan.FromHours(4) : TimeSpan.FromHours(1);
                                        if (DateTime.UtcNow - lastClosedSig.ClosedAt.Value < candleCooldown)
                                        {
                                            Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                                            Console.WriteLine($"[MarketScanner] 1-candle cooldown active for {sym}. Skipping.");
                                            break;
                                        }
                                    }

                                    // BƏND 5: 05:00–07:00 +4 pəncərəsində 1h: nazik kitab, maks 1 ədəd 1h kart (spam pəncərəsi)
                                    var aztNowHour = DateTime.UtcNow.AddHours(4).Hour;
                                    if (signal.Timeframe == "1h" && aztNowHour >= 5 && aztNowHour < 7)
                                    {
                                        var morningKey = DateTime.UtcNow.AddHours(4).ToString("yyyyMMdd_05_07");
                                        var morningDispatches = _hourlyDispatches.GetOrAdd(morningKey, _ => new List<(string Symbol, SignalDirection Direction)>());
                                        lock (morningDispatches)
                                        {
                                            if (morningDispatches.Count >= 1)
                                            {
                                                Interlocked.Increment(ref _hourlyTelemetry.SkipHourCap);
                                                Console.WriteLine($"[MarketScanner] Thin book 05:00-07:00 +4 limit reached (max 1 1h signal). Skipping {signal.Symbol}.");
                                                break;
                                            }
                                        }
                                    }

                                    // BƏND 5: Eyni 1h bağlanışında maks 2 siqnal. 3-cü kəs. Hamısı eyni istiqamətdirsə 2-dən sonra kəs.
                                    var hourKey = candleCloseUtc.ToString("yyyyMMdd_HH");
                                    var hourDispatches = _hourlyDispatches.GetOrAdd(hourKey, _ => new List<(string Symbol, SignalDirection Direction)>());
                                    lock (hourDispatches)
                                    {
                                        if (hourDispatches.Count >= 2)
                                        {
                                            Interlocked.Increment(ref _hourlyTelemetry.SkipHourCap);
                                            Console.WriteLine($"[MarketScanner] Hourly limit reached (2 signals sent for hour {hourKey}). Skipping {signal.Symbol}.");
                                            break;
                                        }

                                        int sameDirectionCount = hourDispatches.Count(d => d.Direction == signal.Direction);
                                        if (sameDirectionCount >= 2)
                                        {
                                            Interlocked.Increment(ref _hourlyTelemetry.SkipHourCap);
                                            Console.WriteLine($"[MarketScanner] Max 2 same direction signals reached for hour {hourKey} ({signal.Direction}). Skipping {signal.Symbol}.");
                                            break;
                                        }
                                    }

                                    // alertKey = Symbol + Direction + Timeframe + SourceCandleOpenTimeUtc UNIQUE
                                    var alertKey = $"{signal.Symbol}_{signal.Direction}_{signal.Timeframe}_{signal.SourceCandleOpenTimeUtc:yyyyMMddHHmmss}";
                                    if (_lastAlertSent.ContainsKey(alertKey) || signal.SignalAlertSent)
                                    {
                                        break;
                                    }

                                    // Check active signal in DB for symbol (HasActive)
                                    if (await uow.Signals.HasActiveSignalForSymbolAsync(signal.Symbol))
                                    {
                                        _lastAlertSent[alertKey] = DateTime.UtcNow;
                                        _lastSymbolAlertTime[signal.Symbol] = DateTime.UtcNow;
                                        _coinActiveLocks.TryAdd(sym, 1);
                                        break;
                                    }

                                    // Check database explicitly for existing candle signal that was already delivered
                                    var existingCandle = await uow.Signals.GetExistingCandleSignalAsync(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                    if (existingCandle != null && existingCandle.SignalAlertSent)
                                    {
                                        _lastAlertSent[alertKey] = DateTime.UtcNow;
                                        _lastSymbolAlertTime[signal.Symbol] = DateTime.UtcNow;
                                        _coinActiveLocks.TryAdd(sym, 1);
                                        break;
                                    }

                                    signal.Number = 0;

                                    // Persist immediately to SQLite DB so it gets an ID before dispatching
                                    if (signal.Id == 0)
                                    {
                                        try
                                        {
                                            await uow.Signals.AddAsync(signal);
                                            await uow.SaveChangesAsync(ct);
                                        }
                                        catch (Exception dbEx)
                                        {
                                            Console.WriteLine($"[MarketScanner] Suppressed duplicate signal insert for {signal.Symbol}: {dbEx.Message}");
                                            _lastAlertSent[alertKey] = DateTime.UtcNow;
                                            _lastSymbolAlertTime[signal.Symbol] = DateTime.UtcNow;
                                            _coinActiveLocks.TryAdd(sym, 1);
                                            break;
                                        }
                                    }

                                    bool ok = false;
                                    try
                                    {
                                        ok = await _telegramService.SendSignalAlertAsync(signal);
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[MarketScanner] SendSignalAlert error for {signal.Symbol}: {ex.Message}");
                                    }

                                    if (ok)
                                    {
                                        _lastAlertSent[alertKey] = DateTime.UtcNow;
                                        _lastSymbolAlertTime[signal.Symbol] = DateTime.UtcNow;
                                        SignalEngine.RecordSentSignalCandle(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc, signal);

                                        Interlocked.Increment(ref _hourlyTelemetry.Sent);
                                        foreach (var s in TelegramBotService.UserPreferences.Values)
                                        {
                                            s.LastSignalSentUtc = DateTime.UtcNow;
                                            s.LastHeartbeatSentUtc = DateTime.UtcNow;
                                        }
                                        TelegramBotService.SaveSettings();

                                        lock (hourDispatches)
                                        {
                                            hourDispatches.Add((signal.Symbol, signal.Direction));
                                        }
                                        if (signal.Timeframe == "1h" && aztNowHour >= 5 && aztNowHour < 7)
                                        {
                                            var morningKey = DateTime.UtcNow.AddHours(4).ToString("yyyyMMdd_05_07");
                                            var morningDispatches = _hourlyDispatches.GetOrAdd(morningKey, _ => new List<(string Symbol, SignalDirection Direction)>());
                                            lock (morningDispatches)
                                            {
                                                morningDispatches.Add((signal.Symbol, signal.Direction));
                                            }
                                        }
                                        _coinActiveLocks.TryAdd(sym, 1);
                                        break;
                                    }
                                    else
                                    {
                                        _lastAlertSent.TryRemove(alertKey, out _);
                                        SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                        Console.WriteLine($"[EMIT_RETRY_ARMED] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} reason=TELEGRAM_FAIL");

                                        Interlocked.Increment(ref _hourlyTelemetry.TelegramFail);
                                        Console.WriteLine($"[MarketScanner] TELEGRAM_FAIL: {signal.Symbol} {signal.Timeframe} — SendSignalAlert false (DataAge? R:R? UserFilter?)");
                                        _coinActiveLocks.TryRemove(sym, out _);

                                        // Clean up unsent signal from DB so it never remains as an open #0 position!
                                        try
                                        {
                                            signal.IsClosed = true;
                                            signal.ClosedAt = DateTime.UtcNow;
                                            signal.Status = SignalStatus.Neutral;
                                            signal.CloseReason = "ALERT_NEVER_SENT_FAILED";
                                            await uow.SaveChangesAsync(ct);
                                        }
                                        catch (Exception cleanEx)
                                        {
                                            Console.WriteLine($"[MarketScanner] Failed to clean unsent signal {signal.Id}: {cleanEx.Message}");
                                        }
                                    }
                                }
                                finally
                                {
                                    _sendSemaphore.Release();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BackgroundMarketScanner] Error scanning {sym}: {ex.Message}");
                    }
                    finally
                    {
                        _scanningCoins.TryRemove(sym, out _);
                    }
                });
            }

            // 3) HƏR 1h BAĞLANIŞINDA KONSOL (JSON Telemetriya)
            var currentHourKey = DateTime.UtcNow.ToString("yyyyMMdd_HH");
            bool isNewHour = string.IsNullOrEmpty(_lastLoggedHourKey) || _lastLoggedHourKey != currentHourKey;
            if (isNewHour)
            {
                _lastLoggedHourKey = currentHourKey;
                var btcSnapForLog = _livePriceCache.GetSnapshot("BTCUSDT");
                var jsonTelemetry = System.Text.Json.JsonSerializer.Serialize(new
                {
                    time = CryptoSense.Domain.Common.TimeHelper.NowFormatted,
                    coinsScanned = _hourlyTelemetry.CoinsScanned,
                    sent = _hourlyTelemetry.Sent,
                    skipChase = _hourlyTelemetry.SkipChase,
                    skipCorr = _hourlyTelemetry.SkipCorr,
                    skipSL = _hourlyTelemetry.SkipSL,
                    skipRR = _hourlyTelemetry.SkipRR,
                    skipLock = _hourlyTelemetry.SkipLock,
                    skipLag = _hourlyTelemetry.SkipLag,
                    skipStale = _hourlyTelemetry.SkipStale,
                    skipConfluence = _hourlyTelemetry.SkipConfluence,
                    skipGozleme = _hourlyTelemetry.SkipGozleme,
                    skipBtcBearLong = _hourlyTelemetry.SkipBtcBearLong,
                    skipBtcRange = _hourlyTelemetry.SkipBtcRange,
                    skipBtcGate = _hourlyTelemetry.SkipBtcGate,
                    skipHourCap = _hourlyTelemetry.SkipHourCap,
                    telegramFail = _hourlyTelemetry.TelegramFail,
                    skipCircuitBreaker = _hourlyTelemetry.SkipCircuitBreaker,
                    skipMaxOpen = _hourlyTelemetry.SkipMaxOpen,
                    skipDailyLoss = _hourlyTelemetry.SkipDailyLoss,
                    dataAgeMsBtc = btcSnapForLog?.DataAgeMs ?? -1,
                    btcSource = btcSnapForLog?.Source ?? "no_snap",
                    cbActive = DateTime.UtcNow < _circuitBreakerUntil
                });
                Console.WriteLine(jsonTelemetry);

                // OVERFILTER DIAQNOSTIKA: sent=0 + coinsScanned>0 + skips > 0 → dominant skip
                int totalSkips = _hourlyTelemetry.SkipConfluence + _hourlyTelemetry.SkipGozleme +
                                 _hourlyTelemetry.SkipBtcBearLong + _hourlyTelemetry.SkipBtcRange +
                                 _hourlyTelemetry.SkipBtc4hOppose + _hourlyTelemetry.SkipChase +
                                 _hourlyTelemetry.SkipSL + _hourlyTelemetry.SkipRR +
                                 _hourlyTelemetry.SkipLag + _hourlyTelemetry.SkipStale + _hourlyTelemetry.SkipHourCap +
                                 _hourlyTelemetry.SkipCircuitBreaker + _hourlyTelemetry.SkipMaxOpen + _hourlyTelemetry.SkipDailyLoss;
                if (_hourlyTelemetry.Sent == 0 && _hourlyTelemetry.CoinsScanned > 0 && totalSkips > 0)
                {
                    // dominant skip növü
                    var skipCounts = new[]
                    {
                        ("CircuitBreaker", _hourlyTelemetry.SkipCircuitBreaker),
                        ("DailyLoss", _hourlyTelemetry.SkipDailyLoss),
                        ("MaxOpen", _hourlyTelemetry.SkipMaxOpen),
                        ("BtcGate", _hourlyTelemetry.SkipBtcGate),
                        ("BtcRange", _hourlyTelemetry.SkipBtcRange),
                        ("Gozleme", _hourlyTelemetry.SkipGozleme),
                        ("Confluence", _hourlyTelemetry.SkipConfluence),
                        ("Chase", _hourlyTelemetry.SkipChase),
                        ("SL", _hourlyTelemetry.SkipSL),
                        ("RR", _hourlyTelemetry.SkipRR),
                        ("Lag", _hourlyTelemetry.SkipLag),
                        ("Stale", _hourlyTelemetry.SkipStale),
                        ("HourCap", _hourlyTelemetry.SkipHourCap),
                        ("TelegramFail", _hourlyTelemetry.TelegramFail)
                    };
                    var dominant = skipCounts.OrderByDescending(x => x.Item2).First();
                    Console.WriteLine($"[OVERFILTER_DIAG] sent=0 dominantSkip={dominant.Item1}:{dominant.Item2} totalSkips={totalSkips}");

                    // TOP5 confluence bazlı BLOCK coin
                    var top5 = _overfilterDiag
                        .OrderByDescending(kv => kv.Value.Confluence)
                        .Take(5)
                        .ToList();
                    foreach (var kv in top5)
                    {
                        Console.WriteLine($"[OVERFILTER_DIAG] TOP_BLOCK {kv.Key} conf={kv.Value.Confluence:F1}% reason={kv.Value.SkipReason}");
                    }
                    _overfilterDiag.Clear();
                }

                LatestTelemetrySnapshot = _hourlyTelemetry.Clone();
                _hourlyTelemetry.Reset();
            }
            else
            {
                LatestTelemetrySnapshot = _hourlyTelemetry.Clone();
            }

            // 1) HEARTBEAT: Saat başı, 1 ədəd. Yalnız Bakı dəqiqə 0–2 (00:00, 01:00, ...). 30 dəq intervalı SİLİNDİ.
            var nowUtc = DateTime.UtcNow;
            var bakuNowHb = nowUtc.AddHours(4);
            bool isHourlyWindow = bakuNowHb.Minute >= 0 && bakuNowHb.Minute <= 2;

            if (isHourlyWindow)
            {
                foreach (var kvp in TelegramBotService.UserPreferences)
                {
                    var chatId = kvp.Key;
                    var s = kvp.Value;
                    if (!s.IsActive) continue;

                    // Başqa user-in heartbeat-i SuperAdmin çatına GETMƏSİN — Tək qapı ilə yoxlanılır və yalnız bu chatId-yə göndərilir
                    if (!await _telegramService.CanReceivePushAsync(chatId)) continue;

                    // Eyni chatId-yə saatda 1 heartbeat. Cari Bakı saatında artıq göndərilibsə ötür
                    if (s.LastHeartbeatSentUtc != default)
                    {
                        var lastSentBaku = s.LastHeartbeatSentUtc.AddHours(4);
                        if (lastSentBaku.Date == bakuNowHb.Date && lastSentBaku.Hour == bakuNowHb.Hour)
                        {
                            continue;
                        }
                    }

                    lock (_heartbeatLock)
                    {
                        if (s.LastHeartbeatSentUtc != default)
                        {
                            var lastSentBaku = s.LastHeartbeatSentUtc.AddHours(4);
                            if (lastSentBaku.Date == bakuNowHb.Date && lastSentBaku.Hour == bakuNowHb.Hour)
                            {
                                continue;
                            }
                        }
                        s.LastHeartbeatSentUtc = DateTime.UtcNow;
                        TelegramBotService.SaveSettings();
                    }

                    var snapTelemetry = LatestTelemetrySnapshot ?? new ScanTelemetry();
                    int activeLocksCount = Math.Max(snapTelemetry.SkipLock, _coinActiveLocks.Count);
                    var btcSnapHb = _livePriceCache.GetSnapshot("BTCUSDT");
                    var heartbeatMsg = TelegramMessageFormatter.FormatLiveHeartbeat(
                        chase: snapTelemetry.SkipChase,
                        corr: snapTelemetry.SkipCorr,
                        slWide: snapTelemetry.SkipSL,
                        lowRr: snapTelemetry.SkipRR,
                        activeLocks: activeLocksCount,
                        sent: snapTelemetry.Sent,
                        nextCheckMinutes: 60,
                        dataAgeMsBtc: btcSnapHb?.DataAgeMs ?? -1,
                        skipStale: snapTelemetry.SkipStale,
                        skipLag: snapTelemetry.SkipLag,
                        skipConfluence: snapTelemetry.SkipConfluence,
                        telegramFail: snapTelemetry.TelegramFail,
                        skipGozleme: snapTelemetry.SkipGozleme,
                        skipBtcGate: snapTelemetry.SkipBtcGate,
                        skipBtcRange: snapTelemetry.SkipBtcRange,
                        skipCircuitBreaker: snapTelemetry.SkipCircuitBreaker,
                        skipMaxOpen: snapTelemetry.SkipMaxOpen,
                        skipDailyLoss: snapTelemetry.SkipDailyLoss);

                    // 1) HEARTBEAT: EditMessage ilə köhnə ℹ️-ni gizlin yeniləmə YOXDUR.
                    // Saat başı YENİ mesaj. Telefon bildirişi gəlsin.
                    if (s.LastHeartbeatMessageId.HasValue)
                    {
                        try
                        {
                            await _telegramService.DeleteMessageAsync(chatId, s.LastHeartbeatMessageId.Value);
                        }
                        catch { /* Köhnə mesaj silinə bilməsə belə yeni mesaj mütləq getməlidir */ }
                    }

                    var newMsgId = await _telegramService.SendMessageReturnIdAsync(heartbeatMsg, chatId);
                    if (newMsgId.HasValue)
                    {
                        s.LastHeartbeatMessageId = newMsgId;
                        TelegramBotService.SaveSettings();
                    }
                }
            }

            // QIZIL QAYDA: Gündəlik hesabat (Günün sonu - Bakı vaxtı ilə 00:00 - 00:30 pəncərəsi)
            var bakuNow = DateTime.UtcNow.AddHours(4);
            if (bakuNow.Hour == 0 && bakuNow.Minute < 30)
            {
                var bakuDateStr = bakuNow.ToString("yyyy-MM-dd");
                if (_lastDailyReportDateBaku != bakuDateStr)
                {
                    _lastDailyReportDateBaku = bakuDateStr;
                    try
                    {
                        await _telegramService.SendDailyReportAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BackgroundMarketScanner] Daily Report error: {ex.Message}");
                    }
                }
            }
        }

        private static DateTime _lastNewsAlertSentUtc = DateTime.MinValue;
        private static readonly TimeSpan MinNewsAlertInterval = TimeSpan.FromMinutes(3);

        private Task MonitorBreakingNewsAndListingsAsync(CancellationToken stoppingToken)
        {
            // Avtomatik xəbər axını istifadəçi əmri ilə qəti şəkildə dayandırıldı.
            return Task.CompletedTask;
        }
    }
}
