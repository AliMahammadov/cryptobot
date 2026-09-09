using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private static readonly SemaphoreSlim _signalDispatchLock = new(1, 1);
        private static DateTime _lastDailyReportDateUtc = DateTime.MinValue;

        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _signalOutcomeSemaphores = new();
        private static readonly ConcurrentDictionary<string, DateTime> _sentOutcomeDeduplication = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastPriceUpdateHandled = new();

        public static void ClearLocks()
        {
            _coinCooldowns.Clear();
            _lastAlertSent.Clear();
            _lastVolatilityAlertSent.Clear();
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
                    var maxNum = db.Signals.Any() ? db.Signals.Max(s => s.Number) : 0;
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
                    await Task.Delay(3000, stoppingToken);
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
                catch { }
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

                decimal riskRPct = (sig.InitialRiskR > 0 && sig.EntryPrice > 0)
                    ? (sig.InitialRiskR / sig.EntryPrice) * 100m
                    : (Math.Abs(sig.EntryPrice - sig.StopLoss) / (sig.EntryPrice > 0 ? sig.EntryPrice : 1m)) * 100m;
                decimal atrPct = sig.AtrPercent > 0 ? sig.AtrPercent : 1.0m;
                decimal beTrigger = Math.Max(0.55m * riskRPct, 0.7m * atrPct);

                // Problem 6 & Qızıl Qayda: BE stopu çəksin, treydi bağlamasın!
                // Tetik MFE >= max(0.55R, 0.7*ATR%) -> SL = Entry + side*0.12%. Treyd AÇIQ qalır!
                if (!sig.BreakevenTriggered && !sig.Tp1Notified && mfe >= beTrigger)
                {
                    sig.BreakevenTriggered = true;
                    sig.StopLoss = isLong
                        ? SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 1.0012m)
                        : SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 0.9988m);
                    await unitOfWork.Signals.UpdateAsync(sig);
                    await unitOfWork.SaveChangesAsync(stoppingToken);
                }

                if (isLong)
                {
                    // 1. Long TP3 Hit (SessionHigh >= TakeProfit3) - TP3 > 0 mütləqdir
                    if (sig.TakeProfit3 > 0 && snap.SessionHigh >= sig.TakeProfit3 && !sig.OutcomeAlertSent)
                    {
                        sig.Tp3Notified = true;
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "TP3";

                        decimal pnl3 = Math.Round(((sig.TakeProfit3 - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent += (sig.RemainingPositionRatio * pnl3);
                        sig.RemainingPositionRatio = 0m;
                        sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                        sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                        sig.OutcomeStatus = "Hədəf 3 (TP3) (TAM MƏNFƏƏT) ✅";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TP3";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 3 (TP3) (TAM MƏNFƏƏT)", exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 2. Long TP2 Hit (SessionHigh >= TakeProfit2) - Yalnız TP2 təyin edilibsə (> 0)
                    else if (sig.TakeProfit2 > 0 && snap.SessionHigh >= sig.TakeProfit2 && !sig.Tp2Notified)
                    {
                        sig.Tp2Notified = true;
                        decimal pnl2 = Math.Round(((sig.TakeProfit2 - sig.EntryPrice) / sig.EntryPrice) * 100, 2);

                        if (sig.TakeProfit3 <= 0 || sig.TakeProfit3 == sig.TakeProfit2)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = exitPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            sig.CloseReason = "TP2";
                            sig.RealizedProfitPercent += (sig.RemainingPositionRatio * pnl2);
                            sig.RemainingPositionRatio = 0m;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                            sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                            sig.OutcomeStatus = "Hədəf 2 (TP2) (TAM MƏNFƏƏT) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP2";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2) (TAM MƏNFƏƏT)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            sig.IsPartial2Closed = true;
                            sig.CloseReason = "TP2";
                            sig.RealizedProfitPercent += (0.25m * pnl2);
                            sig.RemainingPositionRatio = 0.25m;
                            sig.ProfitPercentAchieved = pnl2;
                            sig.StopLoss = sig.TakeProfit1; // Trailing stop moved to TP1

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP2";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2) [25% Əlavə Qazanc]", exitPrice, pnl2);
                            }
                        }
                    }
                    // 3. Long TP1 Hit (SessionHigh >= TakeProfit1) - TP1 > 0 mütləqdir
                    else if (sig.TakeProfit1 > 0 && snap.SessionHigh >= sig.TakeProfit1 && !sig.Tp1Notified)
                    {
                        sig.Tp1Notified = true;
                        decimal pnl1 = Math.Round(((sig.TakeProfit1 - sig.EntryPrice) / sig.EntryPrice) * 100, 2);

                        if (sig.TakeProfit2 <= 0 || sig.TakeProfit2 == sig.TakeProfit1)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = exitPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            sig.CloseReason = "TP1";
                            sig.RealizedProfitPercent += (sig.RemainingPositionRatio * pnl1);
                            sig.RemainingPositionRatio = 0m;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                            sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                            sig.OutcomeStatus = "Hədəf 1 (TP1) (TAM MƏNFƏƏT) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1) (TAM MƏNFƏƏT)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            sig.IsPartial1Closed = true;
                            sig.CloseReason = "TP1";
                            sig.RealizedProfitPercent += (0.50m * pnl1);
                            sig.RemainingPositionRatio = 0.50m;
                            sig.ProfitPercentAchieved = pnl1;
                            sig.StopLoss = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 1.0012m);

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1) [50% Qazanc Bağlandı]", exitPrice, pnl1);
                            }
                        }
                    }
                    // 4. Long Trailing Stop Hit (Last <= TrailPrice)
                    else if (sig.Tp2Notified && sig.TrailPrice > 0 && snap.Last <= sig.TrailPrice && !sig.OutcomeAlertSent)
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "TRAIL";
                        decimal exitPnl = Math.Round(((sig.TrailPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent += (sig.RemainingPositionRatio * exitPnl);
                        sig.RemainingPositionRatio = 0m;
                        sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                        sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                        sig.OutcomeStatus = "Trailing Stop ilə Qorundu (TRAIL) ✅";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TRAIL";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Trailing Stop (TRAIL)", exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 5. Long Stop Loss or Breakeven Hit
                    // BE stopu çəksin, treydi bağlamasın: BE və ya TP1 aktivdirsə, StopLoss yalnız canlı qiymət (snap.Last) stopa çatdıqda vurula bilər!
                    else if (((sig.BreakevenTriggered || sig.Tp1Notified) ? (snap.Last <= sig.StopLoss) : (snap.SessionLow <= sig.StopLoss || snap.Last <= sig.StopLoss)) && !sig.OutcomeAlertSent)
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;

                        if (sig.BreakevenTriggered || sig.Tp1Notified)
                        {
                            sig.CloseReason = "BE";
                            decimal exitPnl = sig.Tp2Notified
                                ? Math.Round(((sig.TakeProfit1 - sig.EntryPrice) / sig.EntryPrice) * 100, 2)
                                : Math.Round(((sig.StopLoss - sig.EntryPrice) / sig.EntryPrice) * 100, 2);

                            sig.RealizedProfitPercent += (sig.RemainingPositionRatio * exitPnl);
                            sig.RemainingPositionRatio = 0m;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                            sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                            sig.OutcomeStatus = "Qorunmuş Breakeven ilə Bağlandı (BE +0.12% bufer) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_BE";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Breakeven (+0.12% bufer ilə Qorundu)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            sig.Status = SignalStatus.Failed;
                            sig.CloseReason = "SL";
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
                    }
                    // 6. Long Time Expiry
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
                    // 1. Short TP3 Hit (SessionLow <= TakeProfit3) - TP3 > 0 mütləqdir
                    if (sig.TakeProfit3 > 0 && snap.SessionLow <= sig.TakeProfit3 && !sig.OutcomeAlertSent)
                    {
                        sig.Tp3Notified = true;
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "TP3";

                        decimal pnl3 = Math.Round(((sig.EntryPrice - sig.TakeProfit3) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent += (sig.RemainingPositionRatio * pnl3);
                        sig.RemainingPositionRatio = 0m;
                        sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                        sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                        sig.OutcomeStatus = "Hədəf 3 (TP3) (TAM MƏNFƏƏT) ✅";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TP3";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 3 (TP3) (TAM MƏNFƏƏT)", exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 2. Short TP2 Hit (SessionLow <= TakeProfit2) - Yalnız TP2 təyin edilibsə (> 0)
                    else if (sig.TakeProfit2 > 0 && snap.SessionLow <= sig.TakeProfit2 && !sig.Tp2Notified)
                    {
                        sig.Tp2Notified = true;
                        decimal pnl2 = Math.Round(((sig.EntryPrice - sig.TakeProfit2) / sig.EntryPrice) * 100, 2);

                        if (sig.TakeProfit3 <= 0 || sig.TakeProfit3 == sig.TakeProfit2)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = exitPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            sig.CloseReason = "TP2";
                            sig.RealizedProfitPercent += (sig.RemainingPositionRatio * pnl2);
                            sig.RemainingPositionRatio = 0m;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                            sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                            sig.OutcomeStatus = "Hədəf 2 (TP2) (TAM MƏNFƏƏT) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP2";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2) (TAM MƏNFƏƏT)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            sig.IsPartial2Closed = true;
                            sig.CloseReason = "TP2";
                            sig.RealizedProfitPercent += (0.25m * pnl2);
                            sig.RemainingPositionRatio = 0.25m;
                            sig.ProfitPercentAchieved = pnl2;
                            sig.StopLoss = sig.TakeProfit1; // Trailing stop to TP1

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP2";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2) [25% Əlavə Qazanc]", exitPrice, pnl2);
                            }
                        }
                    }
                    // 3. Short TP1 Hit (SessionLow <= TakeProfit1) - TP1 > 0 mütləqdir
                    else if (sig.TakeProfit1 > 0 && snap.SessionLow <= sig.TakeProfit1 && !sig.Tp1Notified)
                    {
                        sig.Tp1Notified = true;
                        decimal pnl1 = Math.Round(((sig.EntryPrice - sig.TakeProfit1) / sig.EntryPrice) * 100, 2);

                        if (sig.TakeProfit2 <= 0 || sig.TakeProfit2 == sig.TakeProfit1)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = exitPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            sig.CloseReason = "TP1";
                            sig.RealizedProfitPercent += (sig.RemainingPositionRatio * pnl1);
                            sig.RemainingPositionRatio = 0m;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                            sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                            sig.OutcomeStatus = "Hədəf 1 (TP1) (TAM MƏNFƏƏT) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1) (TAM MƏNFƏƏT)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            sig.IsPartial1Closed = true;
                            sig.CloseReason = "TP1";
                            sig.RealizedProfitPercent += (0.50m * pnl1);
                            sig.RemainingPositionRatio = 0.50m;
                            sig.ProfitPercentAchieved = pnl1;
                            sig.StopLoss = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 0.9988m);

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1) [50% Qazanc Bağlandı]", exitPrice, pnl1);
                            }
                        }
                    }
                    // 4. Short Trailing Stop Hit (Last >= TrailPrice)
                    else if (sig.Tp2Notified && sig.TrailPrice > 0 && snap.Last >= sig.TrailPrice && !sig.OutcomeAlertSent)
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "TRAIL";
                        decimal exitPnl = Math.Round(((sig.EntryPrice - sig.TrailPrice) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent += (sig.RemainingPositionRatio * exitPnl);
                        sig.RemainingPositionRatio = 0m;
                        sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                        sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                        sig.OutcomeStatus = "Trailing Stop ilə Qorundu (TRAIL) ✅";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TRAIL";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Trailing Stop (TRAIL)", exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 5. Short Stop Loss or Breakeven Hit
                    // BE stopu çəksin, treydi bağlamasın: BE və ya TP1 aktivdirsə, StopLoss yalnız canlı qiymət (snap.Last) stopa çatdıqda vurula bilər!
                    else if (((sig.BreakevenTriggered || sig.Tp1Notified) ? (snap.Last >= sig.StopLoss) : (snap.SessionHigh >= sig.StopLoss || snap.Last >= sig.StopLoss)) && !sig.OutcomeAlertSent)
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;

                        if (sig.BreakevenTriggered || sig.Tp1Notified)
                        {
                            sig.CloseReason = "BE";
                            decimal exitPnl = sig.Tp2Notified
                                ? Math.Round(((sig.EntryPrice - sig.TakeProfit1) / sig.EntryPrice) * 100, 2)
                                : Math.Round(((sig.EntryPrice - sig.StopLoss) / sig.EntryPrice) * 100, 2);

                            sig.RealizedProfitPercent += (sig.RemainingPositionRatio * exitPnl);
                            sig.RemainingPositionRatio = 0m;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);
                            sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Failed;
                            sig.OutcomeStatus = "Qorunmuş Breakeven ilə Bağlandı (BE +0.12% bufer) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_BE";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Breakeven (+0.12% bufer ilə Qorundu)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            sig.Status = SignalStatus.Failed;
                            sig.CloseReason = "SL";
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
                    }
                    // 6. Short Time Expiry
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
                                    catch { }
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
            catch { }

            var activeTimeframes = new HashSet<string>();
            var subscribedCoins = new HashSet<string>();
            bool anyUserActive = false;

            foreach (var s in TelegramBotService.UserPreferences.Values)
            {
                if (s.IsActive && s.Coins.Count > 0)
                {
                    anyUserActive = true;
                    if (s.Timeframe == "Hamısı" || s.Timeframe == "Hamisi")
                    {
                        activeTimeframes.Add("1h");
                        activeTimeframes.Add("4h");
                        activeTimeframes.Add("15m");
                    }
                    else if (!string.IsNullOrWhiteSpace(s.Timeframe))
                    {
                        activeTimeframes.Add(s.Timeframe);
                    }

                    foreach (var c in s.Coins) subscribedCoins.Add(c);
                }
            }

            if (!anyUserActive || subscribedCoins.Count == 0)
            {
                // No active user has chosen coins to trade. Do not scan empty list.
                return;
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
                activeTimeframes.Add("15m");
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
                    continue;
                }

                // Strict Coin-Level Rule 2: Does this coin ALREADY have ANY open unclosed position across ANY timeframe?
                if (_coinActiveLocks.ContainsKey(sym))
                {
                    continue;
                }

                if (await unitOfWork.Signals.HasActiveSignalForSymbolAsync(sym))
                {
                    _coinActiveLocks.TryAdd(sym, 1);
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
                        if (_coinActiveLocks.Count >= MaxGlobalOpenPositions) return;
                        if (_coinActiveLocks.ContainsKey(sym)) return;

                        using var innerScope = _serviceProvider.CreateScope();
                        var engine = innerScope.ServiceProvider.GetRequiredService<ISignalEngine>();
                        var uow = innerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                        var marketData = innerScope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

                        if (await uow.Signals.HasActiveSignalForSymbolAsync(sym))
                        {
                            _coinActiveLocks.TryAdd(sym, 1);
                            return;
                        }

                        // Qızıl Qayda: Evaluate in order of institutional significance (1h -> 4h -> 15m)
                        var prioritizedTfs = new[] { "1h", "4h", "15m" }
                            .Where(tf => activeTimeframes.Contains(tf))
                            .ToList();

                        if (prioritizedTfs.Count == 0)
                        {
                            prioritizedTfs = new List<string> { "1h", "15m" };
                        }

                        foreach (var tf in prioritizedTfs)
                        {
                            if (_coinActiveLocks.ContainsKey(sym)) break;
                            if (_coinActiveLocks.Count >= MaxGlobalOpenPositions) break;

                            var signal = await engine.AnalyzeCoinAsync(sym, tf, isLiveScan: true);

                            // ⚠️ ABNORMAL VOLATILITY / EXTREME RISK ALERT
                            if (signal.SignalType == "YÜKSƏK_VOLATİLLİK_RİSK")
                            {
                                var volKey = $"{signal.Symbol}_volatility";
                                if (!_lastVolatilityAlertSent.TryGetValue(volKey, out var lastSent) || (DateTime.UtcNow - lastSent).TotalMinutes >= 45)
                                {
                                    _lastVolatilityAlertSent[volKey] = DateTime.UtcNow;
                                    var reasonText = signal.AnalysisReasons.Count > 0 ? signal.AnalysisReasons[0] : "Kəskin dalğalanma və spayklar aşkarlandı";
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
                            if (signal.Confidence >= 75 && (signal.SignalType.Contains("LONG") || signal.SignalType.Contains("SHORT")))
                            {
                                var candleDuration = signal.Timeframe switch
                                {
                                    "1m" => TimeSpan.FromMinutes(1),
                                    "3m" => TimeSpan.FromMinutes(3),
                                    "5m" => TimeSpan.FromMinutes(5),
                                    "15m" => TimeSpan.FromMinutes(15),
                                    "1h" => TimeSpan.FromHours(1),
                                    "4h" => TimeSpan.FromHours(4),
                                    _ => TimeSpan.FromMinutes(15)
                                };
                                var candleCloseUtc = signal.SourceCandleOpenTimeUtc + candleDuration;

                                // (2) emitTs - candleCloseTime > 90000ms skip (SKIP_CYCLE_LAG)
                                var emitLagMs = (DateTime.UtcNow - candleCloseUtc).TotalMilliseconds;
                                if (emitLagMs > 90000)
                                {
                                    Console.WriteLine($"[MarketScanner] SKIP_CYCLE_LAG: {signal.Symbol} lag={emitLagMs:F0}ms > 90000ms");
                                    continue;
                                }

                                // (3) Subscribe, 1500ms ws_last age<=3000, yoxdursa 1x REST aggTrade
                                _wsClient.Subscribe(signal.Symbol);
                                var snap = _livePriceCache.GetSnapshot(signal.Symbol);
                                var waitDeadline = DateTime.UtcNow.AddMilliseconds(1500);
                                while ((snap == null || snap.DataAgeMs > 3000) && DateTime.UtcNow < waitDeadline)
                                {
                                    await Task.Delay(100, ct);
                                    snap = _livePriceCache.GetSnapshot(signal.Symbol);
                                }

                                if (snap == null || snap.DataAgeMs > 3000)
                                {
                                    var restAgg = await marketData.GetLastAggTradeAsync(signal.Symbol);
                                    if (restAgg.HasValue)
                                    {
                                        _livePriceCache.UpdateFromAggTrade(signal.Symbol, restAgg.Value.Price, restAgg.Value.ExchangeTsMs, isRestFallback: true);
                                        snap = _livePriceCache.GetSnapshot(signal.Symbol);
                                    }
                                }

                                // (4) yenə age>3000 → SKIP_STALE
                                if (snap == null || snap.DataAgeMs > 3000)
                                {
                                    Console.WriteLine($"[MarketScanner] SKIP_STALE: {signal.Symbol} dataAgeMs={(snap?.DataAgeMs ?? -1)} > 3000ms");
                                    continue;
                                }

                                // (5) EntryPrice=Last, PriceSource, ExchangeTs, DataAgeMs, CandleCloseTime
                                signal.EntryPrice = snap.Last;
                                signal.PriceSource = snap.Source;
                                signal.ExchangeTsMs = snap.ExchangeTsMs;
                                signal.DataAgeMs = snap.DataAgeMs;
                                signal.CandleCloseTimeUtc = candleCloseUtc;

                                _livePriceCache.ResetSession(signal.Symbol);

                                // (6) Risk:Reward & Target Gate:
                                // Şərt yalnız: TP1 0.60–2.50%, R:R >= 0.8, TP2 != TP1 (yoxdursa TP2 də yox)
                                decimal tp1Dist = Math.Abs(signal.TakeProfit1 - signal.EntryPrice);
                                decimal slDist = Math.Abs(signal.StopLoss - signal.EntryPrice);
                                decimal tp1Pct = signal.EntryPrice > 0 ? (tp1Dist / signal.EntryPrice) * 100m : 0m;
                                decimal slPct = signal.EntryPrice > 0 ? (slDist / signal.EntryPrice) * 100m : 0m;
                                decimal rr = slDist > 0 ? (tp1Dist / slDist) : 0m;

                                if (tp1Pct < 0.60m)
                                {
                                    Console.WriteLine($"[MarketScanner] SKIP_TP_NEAR send=NO {signal.Symbol} tp1%={tp1Pct:F2}% < 0.60% floor");
                                    continue;
                                }

                                if (tp1Pct > 2.50m)
                                {
                                    Console.WriteLine($"[MarketScanner] SKIP_TP_FAR send=NO {signal.Symbol} tp1%={tp1Pct:F2}% > 2.50% ceiling");
                                    continue;
                                }

                                if (rr < 0.8m)
                                {
                                    Console.WriteLine($"[MarketScanner] SKIP_RR send=NO {signal.Symbol} tp1%={tp1Pct:F2}% sl%={slPct:F2}% rr={rr:F2}");
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
                                    // Qayda 1 & Qayda 2 Double-Check under atomic lock
                                    if (_coinActiveLocks.ContainsKey(sym)) break;
                                    if (_coinActiveLocks.Count >= MaxGlobalOpenPositions) break;
                                    if (await uow.Signals.HasActiveSignalForSymbolAsync(sym))
                                    {
                                        _coinActiveLocks.TryAdd(sym, 1);
                                        break;
                                    }

                                    var alertKey = $"{signal.Symbol}_{signal.Timeframe}_{signal.SourceCandleOpenTimeUtc:yyyyMMddHHmmss}";
                                    if (!_lastAlertSent.ContainsKey(alertKey) && !signal.SignalAlertSent)
                                    {
                                        _lastAlertSent[alertKey] = DateTime.UtcNow;

                                        int n;
                                        lock (_sendLock)
                                        {
                                            n = _nextSignalNumber + 1;
                                            signal.Number = n;
                                        }

                                        // Persist immediately to SQLite DB so it gets an ID before dispatching
                                        if (signal.Id == 0)
                                        {
                                            await uow.Signals.AddAsync(signal);
                                            await uow.SaveChangesAsync(ct);
                                        }

                                        var ok = await _telegramService.SendSignalAlertAsync(signal);
                                        if (ok)
                                        {
                                            lock (_sendLock)
                                            {
                                                _nextSignalNumber = n;
                                            }
                                            _coinActiveLocks.TryAdd(sym, 1);
                                            break;
                                        }
                                        else
                                        {
                                            signal.Number = 0;
                                            _coinActiveLocks.TryRemove(sym, out _);
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
                    catch (Exception)
                    {
                    }
                });
            }

            // Anti-Spam Hourly Status Heartbeat (Maximum once per 60 minutes with concrete reason)
            var nowUtc = DateTime.UtcNow;
            foreach (var kvp in TelegramBotService.UserPreferences)
            {
                var chatId = kvp.Key;
                var s = kvp.Value;
                if (!s.IsActive) continue;

                if (s.LastHeartbeatSentUtc == default)
                {
                    s.LastHeartbeatSentUtc = nowUtc;
                    continue;
                }

                var minutesSinceSignal = (nowUtc - s.LastSignalSentUtc).TotalMinutes;
                var minutesSinceHeartbeat = (nowUtc - s.LastHeartbeatSentUtc).TotalMinutes;

                if (minutesSinceSignal >= 60 && minutesSinceHeartbeat >= 60)
                {
                    s.LastHeartbeatSentUtc = nowUtc;
                    string noSignalReason;
                    if (s.Coins.Count == 0)
                    {
                        noSignalReason = "heç bir coin seçilməyib";
                    }
                    else if (_coinActiveLocks.Count >= MaxGlobalOpenPositions)
                    {
                        noSignalReason = "limit dolu (maksimum 5 açıq mövqe)";
                    }
                    else
                    {
                        noSignalReason = "ADX < 20 və ya Confluence < 78% (A+ tələbi ödənmir)";
                    }

                    var heartbeatMsg = TelegramMessageFormatter.FormatNoSignalReason(noSignalReason, nextCheckMinutes: 30);
                    await _telegramService.SendMessageAsync(heartbeatMsg, chatId);
                }
            }

            // Daily Report Dispatch (Once per day at Baku midnight = 20:00 UTC)
            if (nowUtc.Date > _lastDailyReportDateUtc && nowUtc.Hour >= 20)
            {
                _lastDailyReportDateUtc = nowUtc.Date;
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

        private static DateTime _lastNewsAlertSentUtc = DateTime.MinValue;
        private static readonly TimeSpan MinNewsAlertInterval = TimeSpan.FromMinutes(3);

        private Task MonitorBreakingNewsAndListingsAsync(CancellationToken stoppingToken)
        {
            // Avtomatik xəbər axını istifadəçi əmri ilə qəti şəkildə dayandırıldı.
            return Task.CompletedTask;
        }
    }
}
