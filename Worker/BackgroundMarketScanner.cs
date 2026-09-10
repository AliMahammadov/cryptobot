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
        private static DateTime _lastDailyReportDateUtc = DateTime.MinValue;

        private static readonly ConcurrentDictionary<string, List<(string Symbol, SignalDirection Direction)>> _hourlyDispatches = new();
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _signalOutcomeSemaphores = new();
        private static readonly ConcurrentDictionary<string, DateTime> _sentOutcomeDeduplication = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastPriceUpdateHandled = new();

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
                decimal tp1DistPct = (sig.EntryPrice > 0 && sig.TakeProfit1 > 0) ? (Math.Abs(sig.TakeProfit1 - sig.EntryPrice) / sig.EntryPrice) * 100m : 1.50m;
                decimal beTrigger = (sig.Timeframe == "4h")
                    ? Math.Max(1.50m, tp1DistPct * 0.70m)
                    : Math.Max(1.00m, tp1DistPct * 0.70m);

                // MFE >= 1.00% (1h) / 1.50% (4h) -> SL girise (+0.12% bufer).
                // MFE serti odenibse SL-e getmek bug-dur — BE o tick-de yerdeyissin derhal! Treyd ACIQ qalir!
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

                            // BE xalis < +0.20% qələbə SAYILMASIN (NEUTRAL)
                            string pnlSign = sig.ResultPercent >= 0 ? "+" : "";
                            string pnlFormatted = sig.ResultPercent.HasValue ? sig.ResultPercent.Value.ToString("F2", CultureInfo.InvariantCulture) : "0.00";

                            if (sig.ResultPercent < 0.20m)
                            {
                                sig.Status = SignalStatus.Neutral;
                                sig.OutcomeStatus = $"Qorunmuş Breakeven ilə Bağlandı (BE {pnlSign}{pnlFormatted}% NEYTRAL) ⚪";
                            }
                            else
                            {
                                sig.Status = SignalStatus.Success;
                                sig.OutcomeStatus = $"Qorunmuş Breakeven ilə Bağlandı (BE {pnlSign}{pnlFormatted}%) ✅";
                            }

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_BE";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                string beOutcomeText = $"Breakeven (Xalis {pnlSign}{pnlFormatted}% - NEYTRAL)";
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, beOutcomeText, exitPrice, sig.ResultPercent.Value);
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

                            // BE xalis < +0.20% qələbə SAYILMASIN (NEUTRAL)
                            string pnlSign = sig.ResultPercent >= 0 ? "+" : "";
                            string pnlFormatted = sig.ResultPercent.HasValue ? sig.ResultPercent.Value.ToString("F2", CultureInfo.InvariantCulture) : "0.00";

                            if (sig.ResultPercent < 0.20m)
                            {
                                 sig.Status = SignalStatus.Neutral;
                                 sig.OutcomeStatus = $"Qorunmuş Breakeven ilə Bağlandı (BE {pnlSign}{pnlFormatted}% NEYTRAL) ⚪";
                            }
                            else
                            {
                                 sig.Status = SignalStatus.Success;
                                 sig.OutcomeStatus = $"Qorunmuş Breakeven ilə Bağlandı (BE {pnlSign}{pnlFormatted}%) ✅";
                            }

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_BE";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                string beOutcomeText = $"Breakeven (Xalis {pnlSign}{pnlFormatted}% - NEYTRAL)";
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, beOutcomeText, exitPrice, sig.ResultPercent.Value);
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

                        // Bağlanandan sonra minimum 1 tam şam boşluq (1h: 60 dəqiqə, 4h: 240 dəqiqə)
                        var lastClosed = await uow.Signals.GetLastClosedSignalForSymbolAsync(sym);
                        if (lastClosed != null && lastClosed.ClosedAt.HasValue)
                        {
                            var cooldownRequired = lastClosed.Timeframe == "4h" ? TimeSpan.FromHours(4) : TimeSpan.FromHours(1);
                            if (DateTime.UtcNow - lastClosed.ClosedAt.Value < cooldownRequired)
                            {
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
                            if (_coinActiveLocks.ContainsKey(sym)) break;
                            if (_coinActiveLocks.Count >= MaxGlobalOpenPositions) break;

                            var signal = await engine.AnalyzeCoinAsync(sym, tf, isLiveScan: true);

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
                            if (signal.Timeframe != "15m" && signal.Confidence >= 75 && (signal.SignalType.Contains("LONG") || signal.SignalType.Contains("SHORT")))
                            {
                                var candleDuration = signal.Timeframe switch
                                {
                                    "4h" => TimeSpan.FromHours(4),
                                    _ => TimeSpan.FromHours(1)
                                };
                                var candleCloseUtc = signal.SourceCandleOpenTimeUtc + candleDuration;

                                // (2) emitTs - candleCloseTime > maxAllowedLagMs skip (SKIP_CYCLE_LAG)
                                var maxAllowedLagMs = signal.Timeframe switch
                                {
                                    "4h" => 1800000, // 30 dəqiqə
                                    _ => 900000     // 15 dəqiqə (1h şam bağlanışı üçün)
                                };
                                var emitLagMs = (DateTime.UtcNow - candleCloseUtc).TotalMilliseconds;
                                if (emitLagMs > maxAllowedLagMs)
                                {
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

                                // (6) Risk:Reward & Target Gate:
                                // 1h: SL 0.70%-1.50%, TP1 1.10%-2.20%
                                // 4h: SL 1.00%-2.50%, TP1 1.50%-3.50%
                                // R:R >= 1.30
                                decimal tp1Dist = Math.Abs(signal.TakeProfit1 - signal.EntryPrice);
                                decimal slDist = Math.Abs(signal.StopLoss - signal.EntryPrice);
                                decimal tp1Pct = signal.EntryPrice > 0 ? (tp1Dist / signal.EntryPrice) * 100m : 0m;
                                decimal slPct = signal.EntryPrice > 0 ? (slDist / signal.EntryPrice) * 100m : 0m;
                                decimal rr = slDist > 0 ? (tp1Dist / slDist) : 0m;

                                decimal minSlPct = signal.Timeframe == "4h" ? 1.00m : 0.70m;
                                decimal maxSlPct = signal.Timeframe == "4h" ? 2.50m : 1.50m;
                                decimal minTpPct = signal.Timeframe == "4h" ? 1.50m : 1.10m;
                                decimal maxTpPct = signal.Timeframe == "4h" ? 3.50m : 2.20m;

                                if (tp1Pct < minTpPct || tp1Pct > maxTpPct || slPct < minSlPct || slPct > maxSlPct)
                                {
                                    Console.WriteLine($"[MarketScanner] CAP filter blocked: {signal.Symbol} tp1%={tp1Pct:F2}% sl%={slPct:F2}%");
                                    continue;
                                }

                                if (rr < 1.30m)
                                {
                                    Console.WriteLine($"[MarketScanner] R:R filter blocked: {signal.Symbol} rr={rr:F2} < 1.30");
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

                                    // Saatda maksimum 2 yeni giriş (1h şam bağlanışında maks 2)
                                    // Eyni istiqamətdirsə (hamısı SHORT) 2-ci altdan sonra KƏS!
                                    var hourKey = candleCloseUtc.ToString("yyyyMMdd_HH");
                                    var hourDispatches = _hourlyDispatches.GetOrAdd(hourKey, _ => new List<(string Symbol, SignalDirection Direction)>());
                                    lock (hourDispatches)
                                    {
                                        if (hourDispatches.Count >= 2)
                                        {
                                            Console.WriteLine($"[MarketScanner] Hourly limit reached (2 signals sent for hour {hourKey}). Skipping {signal.Symbol}.");
                                            break;
                                        }

                                        int sameDirectionAlts = hourDispatches.Count(d => d.Direction == signal.Direction && !d.Symbol.StartsWith("BTC") && !d.Symbol.StartsWith("ETH"));
                                        bool currentIsAlt = !signal.Symbol.StartsWith("BTC") && !signal.Symbol.StartsWith("ETH");
                                        if (currentIsAlt && sameDirectionAlts >= 2)
                                        {
                                            Console.WriteLine($"[MarketScanner] Max 2 directional altcoins reached for hour {hourKey} ({signal.Direction}). Skipping {signal.Symbol}.");
                                            break;
                                        }
                                    }

                                    // alertKey = Symbol + Direction + Timeframe + SourceCandleOpenTimeUtc
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

                                    // 8–14 saniyə fərqlə iki kart QADAĞA (Minimum 15 dəqiqə fasilə)
                                    if (_lastSymbolAlertTime.TryGetValue(signal.Symbol, out var lastSymTime) && (DateTime.UtcNow - lastSymTime).TotalMinutes < 15)
                                    {
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
                                        lock (hourDispatches)
                                        {
                                            hourDispatches.Add((signal.Symbol, signal.Direction));
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
                    catch (Exception)
                    {
                    }
                });
            }

            // Anti-Spam Heartbeat (Minimum once per 30 minutes per chat with lock, real reason, persisted LastHeartbeatSentUtc)
            var nowUtc = DateTime.UtcNow;
            foreach (var kvp in TelegramBotService.UserPreferences)
            {
                var chatId = kvp.Key;
                var s = kvp.Value;
                if (!s.IsActive) continue;

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

                lock (_heartbeatLock)
                {
                    if ((DateTime.UtcNow - s.LastHeartbeatSentUtc).TotalMinutes < 30)
                    {
                        continue;
                    }
                    s.LastHeartbeatSentUtc = DateTime.UtcNow;
                    TelegramBotService.SaveSettings();
                }

                string noSignalReason;
                if (s.Coins.Count == 0)
                {
                    noSignalReason = "0 coin seçilib (ticarət bloklanıb, zəhmət olmasa coin seçin)";
                }
                else if (_coinActiveLocks.Count >= MaxGlobalOpenPositions)
                {
                    noSignalReason = $"Limit dolu ({_coinActiveLocks.Count} aktiv mövqe izlənir)";
                }
                else
                {
                    noSignalReason = $"Filtrlər aktivdir: R:R < 1.30, zəif impuls/ADX və ya 1h/4h şam bağlanışı gözlənilir ({s.Coins.Count} coin BLOCK)";
                }

                var heartbeatMsg = TelegramMessageFormatter.FormatNoSignalReason(noSignalReason, nextCheckMinutes: 30);
                await _telegramService.SendMessageAsync(heartbeatMsg, chatId);
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
