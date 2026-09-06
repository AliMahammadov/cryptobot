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

        private static int _consecutiveLosses = 0;
        private static DateTime _circuitBreakerUntil = DateTime.MinValue;
        private static DateTime _lastCircuitBreakerAlertSent = DateTime.MinValue;

        private static readonly ConcurrentDictionary<string, DateTime> _lastAlertSent = new();
        private static readonly ConcurrentDictionary<string, DateTime> _coinCooldowns = new();
        private static readonly ConcurrentDictionary<string, byte> _coinActiveLocks = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastVolatilityAlertSent = new();
        private static readonly SemaphoreSlim _signalDispatchLock = new(1, 1);
        private static DateTime _lastDailyReportDateUtc = DateTime.MinValue;

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
                        decimal currentPnl = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);

                        // 1. Long TP3 Hit (Qalan 25% və ya qalan bütün pay tam bağlanır)
                        if (currentPrice >= sig.TakeProfit3 && !sig.OutcomeAlertSent)
                        {
                            sig.Tp3Notified = true;
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.Status = SignalStatus.Success;
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;

                            decimal pnl3 = Math.Round(((sig.TakeProfit3 - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                            sig.RealizedProfitPercent += (sig.RemainingPositionRatio * pnl3);
                            sig.RemainingPositionRatio = 0m;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent, 2);
                            sig.OutcomeStatus = "Hədəf 3 (TP3) (TAM MƏNFƏƏT) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 3 (TP3) (TAM MƏNFƏƏT)", currentPrice, sig.ResultPercent.Value);
                        }
                        // 2. Long TP2 Hit (Qalan 50%-in yarısı = İlkin mövqenin 25%-i bağlanır)
                        else if (currentPrice >= sig.TakeProfit2 && !sig.Tp2Notified)
                        {
                            sig.Tp2Notified = true;
                            sig.IsPartial2Closed = true;
                            decimal pnl2 = Math.Round(((sig.TakeProfit2 - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                            sig.RealizedProfitPercent += (0.25m * pnl2);
                            sig.RemainingPositionRatio = 0.25m;
                            sig.ProfitPercentAchieved = pnl2;
                            sig.StopLoss = sig.TakeProfit1; // Trailing stop moved to TP1

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2) [25% Əlavə Qazanc]", currentPrice, pnl2);
                        }
                        // 3. Long TP1 Hit (Mövqenin 50%-i dərhal bağlanır, SL Breakeven +0.05%-ə çəkilir)
                        else if (currentPrice >= sig.TakeProfit1 && !sig.Tp1Notified)
                        {
                            sig.Tp1Notified = true;
                            sig.IsPartial1Closed = true;
                            decimal pnl1 = Math.Round(((sig.TakeProfit1 - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                            sig.RealizedProfitPercent += (0.50m * pnl1);
                            sig.RemainingPositionRatio = 0.50m;
                            sig.ProfitPercentAchieved = pnl1;
                            sig.StopLoss = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 1.0005m);

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1) [50% Qazanc Bağlandı]", currentPrice, pnl1);
                        }
                        // 4. Long Stop Loss or Breakeven Hit
                        else if (currentPrice <= sig.StopLoss && !sig.OutcomeAlertSent)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;

                            if (sig.Tp1Notified)
                            {
                                sig.Status = SignalStatus.Success;
                                decimal exitPnl = sig.Tp2Notified
                                    ? Math.Round(((sig.TakeProfit1 - sig.EntryPrice) / sig.EntryPrice) * 100, 2)
                                    : Math.Round(((sig.StopLoss - sig.EntryPrice) / sig.EntryPrice) * 100, 2);

                                sig.RealizedProfitPercent += (sig.RemainingPositionRatio * exitPnl);
                                sig.RemainingPositionRatio = 0m;
                                sig.ResultPercent = Math.Round(sig.RealizedProfitPercent, 2);
                                sig.OutcomeStatus = "Qorunmuş Breakeven ilə Bağlandı (Partial TP ✅)";

                                await unitOfWork.Signals.UpdateAsync(sig);
                                await unitOfWork.SaveChangesAsync(stoppingToken);
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Breakeven (Partial Close ilə Qorundu)", currentPrice, sig.ResultPercent.Value);
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
                        // 5. Long Dynamic Waiting Duration Expired (YALNIZ TP1 VURULMAYIBSA!)
                        else if (isMaxTimeReached && !sig.OutcomeAlertSent && !sig.Tp1Notified)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            sig.ResultPercent = currentPnl;

                            if (currentPnl > 0.2m)
                            {
                                sig.Status = SignalStatus.Success;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (Kiçik Bazar Çıxışı: +{currentPnl}%) ⚪";
                            }
                            else if (Math.Abs(currentPnl) <= 0.2m)
                            {
                                sig.Status = SignalStatus.Neutral;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (Neytral/Konsolidasiya) ⚪";
                            }
                            else
                            {
                                sig.Status = SignalStatus.Failed;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (UĞURSUZ: {currentPnl}%) ❌";
                            }

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Bitdi", currentPrice, currentPnl);
                        }
                    }
                    else // SHORT
                    {
                        decimal currentPnl = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);

                        // 1. Short TP3 Hit (Qalan 25% və ya qalan bütün pay tam bağlanır)
                        if (currentPrice <= sig.TakeProfit3 && !sig.OutcomeAlertSent)
                        {
                            sig.Tp3Notified = true;
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.Status = SignalStatus.Success;
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;

                            decimal pnl3 = Math.Round(((sig.EntryPrice - sig.TakeProfit3) / sig.EntryPrice) * 100, 2);
                            sig.RealizedProfitPercent += (sig.RemainingPositionRatio * pnl3);
                            sig.RemainingPositionRatio = 0m;
                            sig.ResultPercent = Math.Round(sig.RealizedProfitPercent, 2);
                            sig.OutcomeStatus = "Hədəf 3 (TP3) (TAM MƏNFƏƏT) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 3 (TP3) (TAM MƏNFƏƏT)", currentPrice, sig.ResultPercent.Value);
                        }
                        // 2. Short TP2 Hit (Qalan 50%-in yarısı = İlkin mövqenin 25%-i bağlanır)
                        else if (currentPrice <= sig.TakeProfit2 && !sig.Tp2Notified)
                        {
                            sig.Tp2Notified = true;
                            sig.IsPartial2Closed = true;
                            decimal pnl2 = Math.Round(((sig.EntryPrice - sig.TakeProfit2) / sig.EntryPrice) * 100, 2);
                            sig.RealizedProfitPercent += (0.25m * pnl2);
                            sig.RemainingPositionRatio = 0.25m;
                            sig.ProfitPercentAchieved = pnl2;
                            sig.StopLoss = sig.TakeProfit1; // Trailing stop to TP1

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2) [25% Əlavə Qazanc]", currentPrice, pnl2);
                        }
                        // 3. Short TP1 Hit (Mövqenin 50%-i dərhal bağlanır, SL Breakeven -0.05%-ə çəkilir)
                        else if (currentPrice <= sig.TakeProfit1 && !sig.Tp1Notified)
                        {
                            sig.Tp1Notified = true;
                            sig.IsPartial1Closed = true;
                            decimal pnl1 = Math.Round(((sig.EntryPrice - sig.TakeProfit1) / sig.EntryPrice) * 100, 2);
                            sig.RealizedProfitPercent += (0.50m * pnl1);
                            sig.RemainingPositionRatio = 0.50m;
                            sig.ProfitPercentAchieved = pnl1;
                            sig.StopLoss = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice * 0.9995m);

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1) [50% Qazanc Bağlandı]", currentPrice, pnl1);
                        }
                        // 4. Short Stop Loss or Breakeven Hit
                        else if (currentPrice >= sig.StopLoss && !sig.OutcomeAlertSent)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;

                            if (sig.Tp1Notified)
                            {
                                sig.Status = SignalStatus.Success;
                                decimal exitPnl = sig.Tp2Notified
                                    ? Math.Round(((sig.EntryPrice - sig.TakeProfit1) / sig.EntryPrice) * 100, 2)
                                    : Math.Round(((sig.EntryPrice - sig.StopLoss) / sig.EntryPrice) * 100, 2);

                                sig.RealizedProfitPercent += (sig.RemainingPositionRatio * exitPnl);
                                sig.RemainingPositionRatio = 0m;
                                sig.ResultPercent = Math.Round(sig.RealizedProfitPercent, 2);
                                sig.OutcomeStatus = "Qorunmuş Breakeven ilə Bağlandı (Partial TP ✅)";

                                await unitOfWork.Signals.UpdateAsync(sig);
                                await unitOfWork.SaveChangesAsync(stoppingToken);
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Breakeven (Partial Close ilə Qorundu)", currentPrice, sig.ResultPercent.Value);
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
                        // 5. Short Dynamic Waiting Duration Expired (YALNIZ TP1 VURULMAYIBSA!)
                        else if (isMaxTimeReached && !sig.OutcomeAlertSent && !sig.Tp1Notified)
                        {
                            sig.OutcomeAlertSent = true;
                            sig.IsClosed = true;
                            sig.ClosePrice = currentPrice;
                            sig.ClosedAt = DateTime.UtcNow;
                            sig.ResultPercent = currentPnl;

                            if (currentPnl > 0.2m)
                            {
                                sig.Status = SignalStatus.Success;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (Kiçik Bazar Çıxışı: +{currentPnl}%) ⚪";
                            }
                            else if (Math.Abs(currentPnl) <= 0.2m)
                            {
                                sig.Status = SignalStatus.Neutral;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (Neytral/Konsolidasiya) ⚪";
                            }
                            else
                            {
                                sig.Status = SignalStatus.Failed;
                                sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (UĞURSUZ: {currentPnl}%) ❌";
                            }

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Bitdi", currentPrice, currentPnl);
                        }
                    }

                    if (sig.IsClosed)
                    {
                        // Prioritet 4: Consecutive Loss Circuit Breaker
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

            if (activeTimeframes.Count == 0)
            {
                activeTimeframes.Add("1h");
                activeTimeframes.Add("15m");
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

                        // Qızıl Qayda: Evaluate in order of institutional significance (1h -> 4h -> 15m)
                        // Note: 3m is noise-filtered and completely excluded from default scanner flow
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

                                        // Persist immediately to SQLite DB so it gets an ID before dispatching
                                        if (signal.Id == 0)
                                        {
                                            await uow.Signals.AddAsync(signal);
                                            await uow.SaveChangesAsync(ct);
                                        }

                                        // SendSignalAlertAsync delivers ONLY to matching users (coin, timeframe, limits)
                                        // and sets signal.SignalAlertSent = true ONLY IF at least one user received it!
                                        await _telegramService.SendSignalAlertAsync(signal);

                                        if (signal.SignalAlertSent)
                                        {
                                            // Lock coin at whole-coin level (all timeframes blocked until trade closes!)
                                            _coinActiveLocks.TryAdd(sym, 1);
                                            break; // Dispatched signal for this coin; do NOT check any smaller timeframes!
                                        }
                                        else
                                        {
                                            // No user was tracking this coin/timeframe or user daily limits reached.
                                            _coinActiveLocks.TryRemove(sym, out _);
                                        }
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
