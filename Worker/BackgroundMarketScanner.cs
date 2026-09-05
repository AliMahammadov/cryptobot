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
using CryptoSense.Infrastructure.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CryptoSense.Worker
{
    public class BackgroundMarketScanner : BackgroundService
    {
        private readonly ITelegramBotService _telegramService;
        private readonly IServiceProvider _serviceProvider;
        private readonly AppConfig _config;

        public const int MaxGlobalOpenPositions = 5;

        private static readonly ConcurrentDictionary<string, DateTime> _lastAlertSent = new();
        private static readonly ConcurrentDictionary<string, DateTime> _coinCooldowns = new();
        private static readonly ConcurrentDictionary<string, byte> _coinActiveLocks = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastVolatilityAlertSent = new();
        private static readonly SemaphoreSlim _signalDispatchLock = new(1, 1);

        public static void ClearLocks()
        {
            _coinCooldowns.Clear();
            _lastAlertSent.Clear();
            _lastVolatilityAlertSent.Clear();
        }

        public BackgroundMarketScanner(
            ITelegramBotService telegramService,
            IServiceProvider serviceProvider,
            IOptions<AppConfig> config)
        {
            _telegramService = telegramService;
            _serviceProvider = serviceProvider;
            _config = config.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[BackgroundMarketScanner] Yüksək Dəqiqlikli Skaner və Nəticə İzləyicisi başladı.");

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
                    await Task.Delay(2500, stoppingToken);
                }
            }, stoppingToken);

            // 3. REAL-TIME BREAKING NEWS & NEW LISTING PUSH MONITOR (Every 30 seconds)
            var newsMonitorTask = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await MonitorBreakingNewsAndListingsAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NewsMonitor] Warning: {ex.Message}");
                    }
                    await Task.Delay(30000, stoppingToken);
                }
            }, stoppingToken);

            await Task.WhenAll(outcomeTrackerTask, marketScannerTask, newsMonitorTask);
        }

        private async Task TrackActiveSignalOutcomesAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var activeSignals = await unitOfWork.Signals.GetOpenTrackedSignalsAsync();

            // Monotonic sync: Ensure all active DB signals are locked without wiping in-flight locks!
            var activeSymbolsInDb = new HashSet<string>();
            foreach (var s in activeSignals)
            {
                _coinActiveLocks.TryAdd(s.Symbol, 1);
                activeSymbolsInDb.Add(s.Symbol);
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
                    }
                }
            }

            if (activeSignals.Count == 0)
            {
                return;
            }

            var tickers = await marketData.GetTopFuturesTickersAsync(250);
            var tickerDict = new Dictionary<string, decimal>();
            foreach (var t in tickers)
            {
                tickerDict[t.Symbol] = t.Price;
            }

            foreach (var sig in activeSignals)
            {
                if (sig.OutcomeAlertSent || sig.IsClosed) continue;

                decimal currentPrice = 0;
                if (!tickerDict.TryGetValue(sig.Symbol, out currentPrice))
                {
                    if (!tickerDict.TryGetValue("1000" + sig.Symbol, out currentPrice))
                    {
                        if (sig.Symbol.StartsWith("1000"))
                        {
                            tickerDict.TryGetValue(sig.Symbol.Substring(4), out currentPrice);
                        }
                    }
                }

                bool isMaxTimeReached = sig.ExpiryTimeUtc != default 
                    ? DateTime.UtcNow >= sig.ExpiryTimeUtc 
                    : (DateTime.UtcNow - sig.GeneratedAt) >= TimeSpan.FromMinutes(30);

                if (currentPrice == 0 && isMaxTimeReached)
                {
                    try
                    {
                        var klines = await marketData.GetKlinesAsync(sig.Symbol, sig.Timeframe, 2);
                        if (klines.Count > 0) currentPrice = klines.Last().Close;
                    }
                    catch { }

                    if (currentPrice == 0) currentPrice = sig.EntryPrice;
                }

                if (currentPrice > 0)
                {
                    var isLong = sig.Direction == SignalDirection.Buy || sig.SignalType.Contains("LONG");

                    if (isLong)
                    {
                        // Long TP3 Hit
                        if (currentPrice >= sig.TakeProfit3 && !sig.OutcomeAlertSent)
                        {
                            sig.Tp3Notified = true;
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.Status = SignalStatus.Success;
                            sig.OutcomeStatus = "Hədəf 3 (TP3) (UĞURLU) ✅";
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            sig.ResultPercent = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 3 (TP3)", currentPrice, sig.ResultPercent.Value);
                        }
                        // Long TP2 Hit
                        else if (currentPrice >= sig.TakeProfit2 && !sig.Tp2Notified)
                        {
                            sig.Tp2Notified = true;
                            sig.StopLoss = sig.TakeProfit1; // Trailing stop moved to TP1
                            var profitPct = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                            sig.ProfitPercentAchieved = profitPct;
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2)", currentPrice, profitPct);
                        }
                        // Long TP1 Hit
                        else if (currentPrice >= sig.TakeProfit1 && !sig.Tp1Notified)
                        {
                            sig.Tp1Notified = true;
                            sig.StopLoss = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 1.0005m);
                            var profitPct = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                            sig.ProfitPercentAchieved = profitPct;
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1)", currentPrice, profitPct);
                        }
                        // Long Stop Loss or Breakeven Hit
                        else if (currentPrice <= sig.StopLoss && !sig.OutcomeAlertSent)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;

                            if (sig.Tp1Notified)
                            {
                                sig.Status = SignalStatus.Success;
                                var pnl = sig.Tp2Notified
                                    ? Math.Round(((sig.TakeProfit1 - sig.EntryPrice) / sig.EntryPrice) * 100, 2)
                                    : Math.Round(((sig.StopLoss - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                sig.ResultPercent = pnl;
                                sig.OutcomeStatus = "Giriş Qiymətində Bağlandı (Breakeven - Qorundu) ✅";
                                await unitOfWork.Signals.UpdateAsync(sig);
                                await unitOfWork.SaveChangesAsync(stoppingToken);
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Breakeven (Qorundu)", currentPrice, pnl);
                            }
                            else
                            {
                                sig.Status = SignalStatus.Failed;
                                sig.OutcomeStatus = "Stop Loss (SL) (UĞURSUZ) ❌";
                                sig.ResultPercent = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                await unitOfWork.Signals.UpdateAsync(sig);
                                await unitOfWork.SaveChangesAsync(stoppingToken);
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL)", currentPrice, sig.ResultPercent.Value);
                            }
                        }
                        // Long Dynamic Waiting Duration Expired (Decisive Exit: Success or Failed only, no neutral!)
                        else if (isMaxTimeReached && !sig.OutcomeAlertSent)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            var pct = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                            sig.ResultPercent = pct;

                            if (sig.Tp1Notified || pct >= 0)
                            {
                                sig.Status = SignalStatus.Success;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Tamamlandı (Qazancla Qorundu: +{pct}%) ✅";
                                await unitOfWork.Signals.UpdateAsync(sig);
                                await unitOfWork.SaveChangesAsync(stoppingToken);
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Tamamlandı (Qazancla Qorundu)", currentPrice, Math.Abs(pct));
                            }
                            else
                            {
                                sig.Status = SignalStatus.Failed;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (UĞURSUZ: {pct}%) ❌";
                                sig.ResultPercent = -Math.Abs(pct);
                                await unitOfWork.Signals.UpdateAsync(sig);
                                await unitOfWork.SaveChangesAsync(stoppingToken);
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Bitdi (UĞURSUZ)", currentPrice, sig.ResultPercent.Value);
                            }
                        }
                    }
                    else // SHORT
                    {
                        // Short TP3 Hit
                        if (currentPrice <= sig.TakeProfit3 && !sig.OutcomeAlertSent)
                        {
                            sig.Tp3Notified = true;
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.Status = SignalStatus.Success;
                            sig.OutcomeStatus = "Hədəf 3 (TP3) (UĞURLU) ✅";
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            sig.ResultPercent = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 3 (TP3)", currentPrice, sig.ResultPercent.Value);
                        }
                        // Short TP2 Hit
                        else if (currentPrice <= sig.TakeProfit2 && !sig.Tp2Notified)
                        {
                            sig.Tp2Notified = true;
                            sig.StopLoss = sig.TakeProfit1; // Trailing stop to TP1
                            var profitPct = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                            sig.ProfitPercentAchieved = profitPct;
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2)", currentPrice, profitPct);
                        }
                        // Short TP1 Hit
                        else if (currentPrice <= sig.TakeProfit1 && !sig.Tp1Notified)
                        {
                            sig.Tp1Notified = true;
                            sig.StopLoss = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 0.9995m);
                            var profitPct = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                            sig.ProfitPercentAchieved = profitPct;
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1)", currentPrice, profitPct);
                        }
                        // Short Stop Loss or Breakeven Hit
                        else if (currentPrice >= sig.StopLoss && !sig.OutcomeAlertSent)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;

                            if (sig.Tp1Notified)
                            {
                                sig.Status = SignalStatus.Success;
                                var pnl = sig.Tp2Notified
                                    ? Math.Round(((sig.EntryPrice - sig.TakeProfit1) / sig.EntryPrice) * 100, 2)
                                    : Math.Round(((sig.EntryPrice - sig.StopLoss) / sig.EntryPrice) * 100, 2);
                                sig.ResultPercent = pnl;
                                sig.OutcomeStatus = "Giriş Qiymətində Bağlandı (Breakeven - Qorundu) ✅";
                                await unitOfWork.Signals.UpdateAsync(sig);
                                await unitOfWork.SaveChangesAsync(stoppingToken);
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Breakeven (Qorundu)", currentPrice, pnl);
                            }
                            else
                            {
                                sig.Status = SignalStatus.Failed;
                                sig.OutcomeStatus = "Stop Loss (SL) (UĞURSUZ) ❌";
                                var pct = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                sig.ResultPercent = -Math.Abs(pct);
                                await unitOfWork.Signals.UpdateAsync(sig);
                                await unitOfWork.SaveChangesAsync(stoppingToken);
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL)", currentPrice, sig.ResultPercent.Value);
                            }
                        }
                        // Short Dynamic Waiting Duration Expired (Decisive Exit: Success or Failed only, no neutral!)
                        else if (isMaxTimeReached && !sig.OutcomeAlertSent)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            var pct = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                            sig.ResultPercent = pct;

                            if (sig.Tp1Notified || pct >= 0)
                            {
                                sig.Status = SignalStatus.Success;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Tamamlandı (Qazancla Qorundu: +{pct}%) ✅";
                                await unitOfWork.Signals.UpdateAsync(sig);
                                await unitOfWork.SaveChangesAsync(stoppingToken);
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Tamamlandı (Qazancla Qorundu)", currentPrice, Math.Abs(pct));
                            }
                            else
                            {
                                sig.Status = SignalStatus.Failed;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (UĞURSUZ: {pct}%) ❌";
                                sig.ResultPercent = -Math.Abs(pct);
                                await unitOfWork.Signals.UpdateAsync(sig);
                                await unitOfWork.SaveChangesAsync(stoppingToken);
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Bitdi (UĞURSUZ)", currentPrice, sig.ResultPercent.Value);
                            }
                        }
                    }

                    if (sig.IsClosed)
                    {
                        var cooldownMinutes = sig.Timeframe switch
                        {
                            "1m" => 5,
                            "3m" => 15,
                            "5m" => 20,
                            "15m" => 30,
                            "1h" => 60,
                            "4h" => 120,
                            _ => 20
                        };
                        _coinCooldowns[sig.Symbol] = DateTime.UtcNow.AddMinutes(cooldownMinutes);
                        _coinActiveLocks.TryRemove(sig.Symbol, out _);
                    }
                }
            }
        }

        private async Task ScanMarketSignalsAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var openTradesCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();

            // Portfolio-level risk management: Max 5 concurrent active positions across entire market!
            if (openTradesCount >= MaxGlobalOpenPositions || _coinActiveLocks.Count >= MaxGlobalOpenPositions)
            {
                return;
            }

            var activeTimeframes = new HashSet<string>();
            var subscribedCoins = new HashSet<string>();
            bool anyUserWantsAllCoins = false;
            bool anyUserActive = false;

            foreach (var s in TelegramBotService.UserPreferences.Values)
            {
                if (s.IsActive)
                {
                    anyUserActive = true;
                    if (s.Timeframe == "Hamısı" || s.Timeframe == "Hamisi")
                    {
                        // Golden Rule: Focus on higher timeframes; 1m is excluded from auto-scanning to prevent noise
                        activeTimeframes.Add("1h");
                        activeTimeframes.Add("4h");
                        activeTimeframes.Add("15m");
                        activeTimeframes.Add("5m");
                        activeTimeframes.Add("3m");
                    }
                    else if (s.Timeframe != "1m")
                    {
                        activeTimeframes.Add(s.Timeframe);
                    }
                    else
                    {
                        // If user previously had 1m, gracefully migrate to 15m/1h
                        activeTimeframes.Add("15m");
                    }

                    if (s.Coins.Count == 0)
                    {
                        anyUserWantsAllCoins = true;
                    }
                    else
                    {
                        foreach (var c in s.Coins) subscribedCoins.Add(c);
                    }
                }
            }

            if (!anyUserActive)
            {
                var dbUsers = await unitOfWork.Users.GetAllActiveUsersAsync();
                if (dbUsers.Any(u => u.IsActive))
                {
                    anyUserActive = true;
                    anyUserWantsAllCoins = true;
                    activeTimeframes.Add("1h");
                    activeTimeframes.Add("15m");
                    activeTimeframes.Add("3m");
                }
                else
                {
                    return;
                }
            }

            if (anyUserWantsAllCoins || subscribedCoins.Count == 0)
            {
                var defaultCoins = _config.SelectedCoins != null && _config.SelectedCoins.Count > 0
                    ? _config.SelectedCoins
                    : new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT", "NOTUSDT" };
                foreach (var c in defaultCoins) subscribedCoins.Add(c);
            }

            if (activeTimeframes.Count == 0)
            {
                activeTimeframes.Add("1h");
                activeTimeframes.Add("15m");
                activeTimeframes.Add("3m");
            }

            // Qayda 1 & Qayda 2: Filter coins at the root level before launching any analysis
            var coinsToScan = new List<string>();
            foreach (var sym in subscribedCoins)
            {
                // Strict Coin-Level Rule 1: Post-trade cooldown active?
                if (_coinCooldowns.TryGetValue(sym, out var cooldownUntil) && DateTime.UtcNow < cooldownUntil)
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

                coinsToScan.Add(sym);
            }

            if (coinsToScan.Count > 0)
            {
                // Scan by COIN in parallel (eliminates multiple threads scanning the same coin simultaneously!)
                await Parallel.ForEachAsync(coinsToScan, new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = stoppingToken }, async (sym, ct) =>
                {
                    try
                    {
                        // Check if portfolio limit was reached by another parallel thread
                        if (_coinActiveLocks.Count >= MaxGlobalOpenPositions) return;
                        if (_coinActiveLocks.ContainsKey(sym)) return;

                        using var innerScope = _serviceProvider.CreateScope();
                        var engine = innerScope.ServiceProvider.GetRequiredService<ISignalEngine>();
                        var uow = innerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                        if (await uow.Signals.HasActiveSignalForSymbolAsync(sym))
                        {
                            _coinActiveLocks.TryAdd(sym, 1);
                            return;
                        }

                        // Qızıl Qayda: Evaluate in order of institutional significance (1h -> 4h -> 15m -> 5m -> 3m)
                        var prioritizedTfs = new[] { "1h", "4h", "15m", "5m", "3m" }
                            .Where(tf => activeTimeframes.Contains(tf))
                            .ToList();

                        if (prioritizedTfs.Count == 0)
                        {
                            prioritizedTfs = new List<string> { "1h", "15m", "3m" };
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
                                    await _telegramService.SendVolatilityRiskAlertAsync(signal.Symbol, signal.CurrentPrice, 0, 3.5m, reasonText);
                                }
                                break; // High volatility on this coin, skip smaller timeframes
                            }

                            // HIGH-CONVICTION TRADE DISPATCH (Atomic Thread-Safe Lock)
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
                                    _ => TimeSpan.FromMinutes(5)
                                };
                                var maxTolerance = signal.Timeframe switch
                                {
                                    "1m" => TimeSpan.FromSeconds(90),
                                    "3m" => TimeSpan.FromMinutes(3),
                                    "5m" => TimeSpan.FromMinutes(4),
                                    "15m" => TimeSpan.FromMinutes(8),
                                    "1h" => TimeSpan.FromMinutes(15),
                                    "4h" => TimeSpan.FromMinutes(30),
                                    _ => TimeSpan.FromMinutes(3)
                                };
                                var candleCloseUtc = signal.SourceCandleOpenTimeUtc + candleDuration;
                                if (DateTime.UtcNow - candleCloseUtc > maxTolerance)
                                {
                                    continue;
                                }

                                await _signalDispatchLock.WaitAsync(ct);
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
                                        signal.SignalAlertSent = true;

                                        // Lock coin at whole-coin level (all timeframes blocked until trade closes!)
                                        _coinActiveLocks.TryAdd(sym, 1);

                                        // Persist immediately to SQLite DB so OutcomeTracker & other threads see it
                                        if (signal.Id == 0)
                                        {
                                            await uow.Signals.AddAsync(signal);
                                            await uow.SaveChangesAsync(ct);
                                        }
                                        else
                                        {
                                            var dbSig = await uow.Signals.GetByIdAsync(signal.Id);
                                            if (dbSig != null)
                                            {
                                                dbSig.SignalAlertSent = true;
                                                await uow.Signals.UpdateAsync(dbSig);
                                                await uow.SaveChangesAsync(ct);
                                            }
                                        }

                                        await _telegramService.SendSignalAlertAsync(signal);
                                        break; // Dispatched signal for this coin; do NOT check any smaller timeframes!
                                    }
                                }
                                finally
                                {
                                    _signalDispatchLock.Release();
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                });
            }

            // Periodic Liveness Heartbeat (Every 15 minutes if no signals)
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

                if (minutesSinceSignal >= 15 && minutesSinceHeartbeat >= 15)
                {
                    s.LastHeartbeatSentUtc = nowUtc;
                    var heartbeatMsg = "🟢 <b>Sistem Canlı İzləmədədir (15 Dəqiqəlik Vəziyyət):</b>\n\n" +
                                       "ℹ️ <i>Son 15 dəqiqə ərzində bazarda 75%+ risk-təsdiqli yeni A+ siqnal formalaşmadı.</i>\n\n" +
                                       "🎯 <b>Bot 24/7 rejimində bazarı analiz edir.</b> Təsdiqlənmiş yeni şam bağlanan kimi siqnal dərhal sizə göndəriləcək.";
                    await _telegramService.SendMessageAsync(heartbeatMsg, chatId);
                }
            }
        }

        private async Task MonitorBreakingNewsAndListingsAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var newsService = scope.ServiceProvider.GetRequiredService<INewsService>();

            var urgentItems = await newsService.GetUrgentBreakingNewsAndListingsAsync();
            foreach (var item in urgentItems)
            {
                if (stoppingToken.IsCancellationRequested) break;
                bool isListing = item.Title.Contains("List", StringComparison.OrdinalIgnoreCase) || 
                                 item.Title.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                                 item.Title.Contains("Binance", StringComparison.OrdinalIgnoreCase);
                await _telegramService.SendUrgentNewsAlertAsync(item, isListing);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
