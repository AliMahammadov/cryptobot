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
        private static readonly ConcurrentDictionary<string, DateTime> _lastVolatilityAlertSent = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastSymbolAlertTime = new();
        private static readonly object _heartbeatLock = new();
        private static readonly SemaphoreSlim _signalDispatchLock = new(1, 1);
        private static string _lastDailyReportDateBaku = "";

        private static readonly ConcurrentDictionary<string, List<(string Symbol, SignalDirection Direction)>> _hourlyDispatches = new();
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _signalOutcomeSemaphores = new();
        private static readonly ConcurrentDictionary<string, DateTime> _sentOutcomeDeduplication = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastPriceUpdateHandled = new();

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
                SkipStale = this.SkipStale
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

            // Initialize highest signal number from database
            using (var initScope = _serviceProvider.CreateScope())
            {
                try
                {
                    var db = initScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var maxNum = db.Signals.Any() ? db.Signals.Max(s => s.SignalNumber) : 0;
                    lock (_sendLock)
                    {
                        _nextSignalNumber = maxNum;
                    }
                    Console.WriteLine($"[BackgroundMarketScanner] _nextSignalNumber initialized to {_nextSignalNumber}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BackgroundMarketScanner] Error initializing signal number: {ex.Message}");
                }
            }

            // Start WebSocket client independently
            _wsClient.Start(stoppingToken);

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
                        _wsClient.Unsubscribe(lockedSym);
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

                // 3s loop safety: WS down -> GetLastAggTrade only for 1-3 open symbols, interval >= 2s, log REST_FALLBACK
                if (snap == null || snap.DataAgeMs > 3000)
                {
                    if (!_lastRestFallbackTime.TryGetValue(sig.Symbol, out var lastFallback) || (nowUtc - lastFallback).TotalMilliseconds >= 2000)
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
                }

                if (snap == null) continue;

                await ProcessSignalOutcomeAsync(sig, snap, unitOfWork, stoppingToken);
                if (!sig.IsClosed && !sig.OutcomeAlertSent)
                {
                    await CheckCandleInvalidationAndTrailingAsync(sig, marketData, indicatorEngine, unitOfWork, snap.Last, stoppingToken);
                }
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

                bool is180MinReached = (DateTime.UtcNow - sig.GeneratedAt).TotalMinutes >= 180;
                bool isMaxTimeReached = is180MinReached || (sig.ExpiryTimeUtc != default && DateTime.UtcNow >= sig.ExpiryTimeUtc);

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
                    else if (sig.Tp1Notified && !sig.OutcomeAlertSent && snap.Last <= sig.StopLoss)
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
                    else if (!sig.Tp1Notified && !sig.OutcomeAlertSent && (snap.SessionLow <= sig.StopLoss || snap.Last <= sig.StopLoss))
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
                    else if (isMaxTimeReached && !sig.OutcomeAlertSent && !sig.Tp1Notified)
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.ResultPercent = Math.Round(netPnl, 2);
                        sig.CloseReason = "TIME";

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

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TIME";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Bitdi", exitPrice, netPnl);
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
                    else if (sig.Tp1Notified && !sig.OutcomeAlertSent && snap.Last >= sig.StopLoss)
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
                    else if (!sig.Tp1Notified && !sig.OutcomeAlertSent && (snap.SessionHigh >= sig.StopLoss || snap.Last >= sig.StopLoss))
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
                    else if (isMaxTimeReached && !sig.OutcomeAlertSent && !sig.Tp1Notified)
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.ResultPercent = Math.Round(netPnl, 2);
                        sig.CloseReason = "TIME";

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

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TIME";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Bitdi", exitPrice, netPnl);
                        }
                    }
                }

                if (sig.IsClosed)
                {
                    if (sig.Status == SignalStatus.Failed)
                    {
                        int losses = Interlocked.Increment(ref _consecutiveLosses);
                        if (losses >= 3)
                        {
                            _circuitBreakerUntil = DateTime.UtcNow.AddHours(2);
                            Interlocked.Exchange(ref _consecutiveLosses, 0);

                            if ((DateTime.UtcNow - _lastCircuitBreakerAlertSent).TotalMinutes >= 60)
                            {
                                _lastCircuitBreakerAlertSent = DateTime.UtcNow;
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await _telegramService.BroadcastSystemAlertAsync("⚠️ <b>RISK CIRCUIT BREAKER AKTİVLƏŞDİ:</b>\n\n" +
                                            "Ardıcıl 3 uğursuz əməliyyat (Stop Loss) qeydə alındı. Bazar skaneri kapitalı qorumaq üçün <b>2 saatlıq</b> müşahidə rejiminə keçdi.");
                                    }
                                    catch (Exception _ex) { Console.WriteLine($"[BackgroundMarketScanner] Swallowed exception: {_ex.Message}"); }
                                });
                            }
                        }
                    }
                    else if (sig.Status == SignalStatus.Success)
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
                    if (!hasOther)
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
            decimal currentLast,
            CancellationToken stoppingToken)
        {
            if (sig.IsClosed || sig.OutcomeAlertSent) return;

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

            var isLong = sig.Direction == SignalDirection.Buy || sig.SignalType.Contains("LONG");
            decimal exitPrice = currentLast;
            decimal grossPnl = isLong
                ? Math.Round(((exitPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2)
                : Math.Round(((sig.EntryPrice - exitPrice) / sig.EntryPrice) * 100, 2);
            decimal netPnl = Math.Round(grossPnl - 0.10m, 2);

            // Problem 5: 4 qapalı şam MFE < 0.3R -> NO_EDGE
            decimal riskRPct = (sig.InitialRiskR > 0 && sig.EntryPrice > 0)
                ? (sig.InitialRiskR / sig.EntryPrice) * 100m
                : (Math.Abs(sig.EntryPrice - sig.StopLoss) / (sig.EntryPrice > 0 ? sig.EntryPrice : 1m)) * 100m;

            if (sig.CandlesObserved >= 4 && !sig.Tp1Notified && sig.MfePercent < (0.3m * riskRPct))
            {
                sig.OutcomeAlertSent = true;
                sig.IsClosed = true;
                sig.ClosePrice = exitPrice;
                sig.ClosedAt = DateTime.UtcNow;
                sig.Status = SignalStatus.Neutral;
                sig.CloseReason = "NO_EDGE";
                sig.ResultPercent = netPnl;
                sig.OutcomeStatus = $"{sig.Timeframe} 4 Şam Hərəkətsiz (NO_EDGE) ⚪";

                await unitOfWork.Signals.UpdateAsync(sig);
                await unitOfWork.SaveChangesAsync(stoppingToken);
                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "NO_EDGE (4 Şam Ərzində Hərəkətsiz)", exitPrice, netPnl);
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
            // Prioritet 4: Circuit breaker aktivdirsə, yeni skan dayandırılır
            if (DateTime.UtcNow < _circuitBreakerUntil)
            {
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

            var openTradesCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();

            // Portfolio-level risk management: Max 5 concurrent active positions across entire market!
            if (openTradesCount >= MaxGlobalOpenPositions || _coinActiveLocks.Count >= MaxGlobalOpenPositions)
            {
                return;
            }

            // Prioritet 4: Günlük -3.0% itki limiti çatdıqda yeni əməliyyat açılmır
            try
            {
                var todayUtc = DateTime.UtcNow.Date;
                var recentSignals = await unitOfWork.Signals.GetRecentSignalsAsync(50);
                var todayClosedPnL = recentSignals
                    .Where(s => s.IsClosed && s.ClosedAt.HasValue && s.ClosedAt.Value >= todayUtc && s.ResultPercent.HasValue)
                    .Sum(s => s.ResultPercent!.Value);

                if (todayClosedPnL <= -3.0m)
                {
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
                        foreach (var c in s.Coins) subscribedCoins.Add(c);
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
                if (_coinActiveLocks.ContainsKey(sym))
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
                // Scan by COIN in parallel (eliminates multiple threads scanning the same coin simultaneously!)
                await Parallel.ForEachAsync(coinsToScan, new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = stoppingToken }, async (sym, ct) =>
                {
                    try
                    {
                        // Check if portfolio limit was reached by another parallel thread
                        if (_coinActiveLocks.Count >= MaxGlobalOpenPositions)
                        {
                            Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                            return;
                        }
                        if (_coinActiveLocks.ContainsKey(sym))
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

                            // Telemetriya: Hər analiz olunan coin üçün real log
                            var aztNow = CryptoSense.Domain.Common.TimeHelper.NowFormatted;
                            var indRes = signal.Indicators as CryptoSense.Application.DTOs.IndicatorResult;
                            decimal adxVal = indRes?.Adx ?? 0m;
                            bool isTradeSignal = signal.Timeframe != "15m" && signal.Confidence >= 75 && (signal.SignalType.Contains("LONG") || signal.SignalType.Contains("SHORT"));
                            string resultStatus = isTradeSignal ? "PASS" : "BLOCK";
                            string reasonDesc = isTradeSignal 
                                ? signal.SignalType 
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
                                    _ => 3000000     // 50 dəqiqə (1h şam bağlanışı üçün)
                                };
                                var emitLagMs = (DateTime.UtcNow - candleCloseUtc).TotalMilliseconds;
                                if (emitLagMs > maxAllowedLagMs)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipLag);
                                    Console.WriteLine($"[MarketScanner] SKIP_CYCLE_LAG: {signal.Symbol} lag={emitLagMs:F0}ms > {maxAllowedLagMs}ms");
                                    continue;
                                }

                                // (3) Subscribe, 1500ms ws_last age<=1000, rest_fallback qadağandır
                                _wsClient.Subscribe(signal.Symbol);
                                var snap = _livePriceCache.GetSnapshot(signal.Symbol);
                                var waitDeadline = DateTime.UtcNow.AddMilliseconds(1500);
                                while ((snap == null || snap.DataAgeMs > 1000 || snap.Source == "rest_fallback") && DateTime.UtcNow < waitDeadline)
                                {
                                    await Task.Delay(100, ct);
                                    snap = _livePriceCache.GetSnapshot(signal.Symbol);
                                }

                                if (snap == null || snap.DataAgeMs > 1000 || snap.Source == "rest_fallback")
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipStale);
                                    Console.WriteLine($"[MarketScanner] SKIP_STALE_OR_REST: {signal.Symbol} dataAgeMs={(snap?.DataAgeMs ?? -1)} source={snap?.Source}");
                                    continue;
                                }

                                // (5) EntryPrice=Last, PriceSource, ExchangeTs, DataAgeMs, CandleCloseTime
                                signal.EntryPrice = snap.Last;
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
                                    Console.WriteLine($"[MarketScanner] SL too wide: {signal.Symbol} ({signal.Timeframe}) sl%={slPct:F2}% > {maxSlPct:F2}%. Trade skipped.");
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
                                    Console.WriteLine($"[MarketScanner] R:R filter blocked: {signal.Symbol} effectiveRr={effectiveRr:F2} < 1.30");
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
                                                Interlocked.Increment(ref _hourlyTelemetry.SkipCorr);
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
                                            Interlocked.Increment(ref _hourlyTelemetry.SkipCorr);
                                            Console.WriteLine($"[MarketScanner] Hourly limit reached (2 signals sent for hour {hourKey}). Skipping {signal.Symbol}.");
                                            break;
                                        }

                                        int sameDirectionCount = hourDispatches.Count(d => d.Direction == signal.Direction);
                                        if (sameDirectionCount >= 2)
                                        {
                                            Interlocked.Increment(ref _hourlyTelemetry.SkipCorr);
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

                                    // Check database explicitly for existing candle signal that was already delivered
                                    var existingCandle = await uow.Signals.GetExistingCandleSignalAsync(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                    if (existingCandle != null && (existingCandle.SignalAlertSent || existingCandle.Id > 0))
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
                                        await uow.Signals.AddAsync(signal);
                                        await uow.SaveChangesAsync(ct);
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

                                    // Always lock this candle so it cannot be retried/spammed in subsequent cycles
                                    _lastAlertSent[alertKey] = DateTime.UtcNow;
                                    _lastSymbolAlertTime[signal.Symbol] = DateTime.UtcNow;

                                    if (ok)
                                    {
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
                                        _coinActiveLocks.TryRemove(sym, out _);
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
                        Console.WriteLine($"[BackgroundMarketScanner] Heartbeat send error: {ex.Message}");
                    }
                });
            }

            // 3) HƏR 1h BAĞLANIŞINDA KONSOL (JSON Telemetriya)
            var currentHourKey = DateTime.UtcNow.ToString("yyyyMMdd_HH");
            bool isNewHour = string.IsNullOrEmpty(_lastLoggedHourKey) || _lastLoggedHourKey != currentHourKey;
            if (isNewHour)
            {
                _lastLoggedHourKey = currentHourKey;
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
                    skipStale = _hourlyTelemetry.SkipStale
                });
                Console.WriteLine(jsonTelemetry);

                LatestTelemetrySnapshot = _hourlyTelemetry.Clone();
                _hourlyTelemetry.Reset();
            }
            else
            {
                LatestTelemetrySnapshot = _hourlyTelemetry.Clone();
            }

            // 1) Anti-Spam Heartbeat (Minimum once per 30 minutes per chat with lock, real reason, persisted LastHeartbeatSentUtc)
            var nowUtc = DateTime.UtcNow;
            foreach (var kvp in TelegramBotService.UserPreferences)
            {
                var chatId = kvp.Key;
                var s = kvp.Value;
                if (!s.IsActive) continue;

                // BUG 6 & BUG 2: Skip logged-out users strictly via database & session gate
                if (!await _telegramService.CanReceivePushAsync(chatId)) continue;

                if (s.LastHeartbeatSentUtc == default)
                {
                    s.LastHeartbeatSentUtc = nowUtc;
                    TelegramBotService.SaveSettings();
                    continue;
                }

                var minutesSinceSignal = (nowUtc - s.LastSignalSentUtc).TotalMinutes;
                var minutesSinceHeartbeat = (nowUtc - s.LastHeartbeatSentUtc).TotalMinutes;

                // Son 30 dəq-də siqnal gedibsə heartbeat yox.
                // 30 dəq dolmayıbsa İKİNCİ yox.
                if (minutesSinceSignal < 30 || minutesSinceHeartbeat < 30)
                {
                    continue;
                }

                // DB-də son 30 dəq-də hər hansı siqnal və ya nəticə (TP/SL/BE) varsa heartbeat YOX
                try
                {
                    var recentSignals = await unitOfWork.Signals.GetRecentSignalsAsync(10);
                    bool hasRecentActivityIn30m = recentSignals.Any(sig =>
                        (sig.ClosedAt.HasValue && (nowUtc - sig.ClosedAt.Value).TotalMinutes < 30) ||
                        (sig.GeneratedAt != default && (nowUtc - sig.GeneratedAt).TotalMinutes < 30 && sig.SignalAlertSent));
                    if (hasRecentActivityIn30m)
                    {
                        continue;
                    }
                }
                catch (Exception _ex) { Console.WriteLine($"[BackgroundMarketScanner] Swallowed exception: {_ex.Message}"); }

                lock (_heartbeatLock)
                {
                    if ((DateTime.UtcNow - s.LastHeartbeatSentUtc).TotalMinutes < 30)
                    {
                        continue;
                    }
                    s.LastHeartbeatSentUtc = DateTime.UtcNow;
                    TelegramBotService.SaveSettings();
                }

                var snapTelemetry = LatestTelemetrySnapshot ?? new ScanTelemetry();
                int activeLocksCount = Math.Max(snapTelemetry.SkipLock, _coinActiveLocks.Count);
                var heartbeatMsg = TelegramMessageFormatter.FormatLiveHeartbeat(
                    snapTelemetry.SkipChase,
                    snapTelemetry.SkipCorr,
                    snapTelemetry.SkipSL,
                    snapTelemetry.SkipRR,
                    activeLocksCount,
                    snapTelemetry.Sent,
                    nextCheckMinutes: 30);

                // 1) HEARTBEAT: EditMessage ilə köhnə ℹ️-ni gizlin yeniləmə YOXDUR.
                // Hər 30 dəq-də YENİ mesaj. Telefon bildirişi gəlsin.
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
