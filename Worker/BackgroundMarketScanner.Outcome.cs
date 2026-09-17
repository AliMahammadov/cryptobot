using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.Interfaces;
using CryptoSense.Application.Services;
using CryptoSense.Domain.Common;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using CryptoSense.Infrastructure.Telegram;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoSense.Worker
{
    public partial class BackgroundMarketScanner
    {
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
                if (snap == null || snap.DataAgeMs > BotConstants.Thresholds.MaxDataAgeMs)
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
                        sig.RemainingPositionRatio = 1m - BotConstants.Thresholds.PartialTp1;
                        sig.CloseReason = "TP_A";

                        decimal pnl1 = isLong
                            ? Math.Round(((sig.TakeProfit1 - sig.EntryPrice) / sig.EntryPrice) * 100, 2)
                            : Math.Round(((sig.EntryPrice - sig.TakeProfit1) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent = Math.Round(BotConstants.Thresholds.PartialTp1 * pnl1, 2);
                        sig.ProfitPercentAchieved = pnl1;

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
                            sig.OutcomeStatus = "Hədəf A (TP_A 1.5R) (TAM MƏNFƏƏT) [Restart Catch-up] ✅";

                            await uow.Signals.UpdateAsync(sig);
                            await uow.SaveChangesAsync(CancellationToken.None);
                            _coinActiveLocks.TryRemove(sig.Symbol, out _);

                            if (sig.SignalAlertSent)
                            {
                                await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.5R) [Restart Catch-up]", sig.TakeProfit1, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            await uow.Signals.UpdateAsync(sig);
                            await uow.SaveChangesAsync(CancellationToken.None);

                            if (sig.SignalAlertSent)
                            {
                                await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.5R) [40% Qazanc Bağlandı; BE/trail sonra]", sig.TakeProfit1, pnl1);
                            }
                        }
                        Console.WriteLine($"[StartupCatchUp] TP1 caught up for {sig.Symbol} (Id={sig.Id}, PnL1={pnl1}%)");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StartupCatchUp] Error executing startup catch-up: {ex.Message}");
            }
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
                    // 1. Long TP1 (TP_A 1.5R) Hit - 40% bağlandı
                    if (sig.TakeProfit1 > 0 && !sig.Tp1Notified && (snap.SessionHigh >= sig.TakeProfit1 || snap.Last >= (sig.TakeProfit1 - tickSize)))
                    {
                        sig.Tp1Notified = true;
                        sig.IsPartial1Closed = true;
                        sig.RemainingPositionRatio = 1m - BotConstants.Thresholds.PartialTp1;
                        sig.CloseReason = "TP_A";

                        decimal pnl1 = Math.Round(((sig.TakeProfit1 - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent = Math.Round(BotConstants.Thresholds.PartialTp1 * pnl1, 2);
                        sig.ProfitPercentAchieved = pnl1;

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
                            sig.OutcomeStatus = "Hədəf A (TP_A 1.5R) (TAM MƏNFƏƏT) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.5R) (TAM MƏNFƏƏT)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.5R) [40% Qazanc Bağlandı; BE/trail sonra]", exitPrice, pnl1);
                            }
                        }
                    }
                    // 2. Long TP2 (TP_B) Hit - Yalnız TP1-dən sonra qalan hissədən 50% bağlanır (ilkinin 30%-i), 30% trail edir
                    else if (!sig.IsPartial2Closed && sig.Tp1Notified && !sig.OutcomeAlertSent && sig.TakeProfit2 > 0 && (snap.SessionHigh >= sig.TakeProfit2 || snap.Last >= (sig.TakeProfit2 - tickSize)))
                    {
                        decimal pnl2 = Math.Round(((sig.TakeProfit2 - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                        decimal closedPortion = 0.30m;
                        sig.RealizedProfitPercent += Math.Round(closedPortion * pnl2, 2);
                        sig.RemainingPositionRatio = 0.30m;
                        sig.IsPartial2Closed = true;
                        sig.Tp2Notified = true;
                        sig.CloseReason = "TP_B";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TP2";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf B (TP_B) [30% bağlandı, 30% trail]", exitPrice, pnl2);
                        }
                    }
                    // 3. Long Breakeven / Trail Hit (YALNIZ TP1-dən sonra qalan hissə BE/Trail stopuna dəyərsə)
                    else if (sig.Tp1Notified && !sig.OutcomeAlertSent && !sig.IsClosed && (snap.Last <= sig.StopLoss || snap.SessionLow <= sig.StopLoss))
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;

                        bool isTrail = sig.StopLoss > sig.EntryPrice;
                        sig.CloseReason = isTrail ? "TRAIL" : "BE";

                        decimal exitPnl = Math.Round(((sig.StopLoss - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent += Math.Round(sig.RemainingPositionRatio * exitPnl, 2);
                        sig.RemainingPositionRatio = 0m;
                        sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);

                        sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Neutral;
                        string pnlSign = sig.ResultPercent >= 0 ? "+" : "";
                        string pnlFormatted = sig.ResultPercent.HasValue ? sig.ResultPercent.Value.ToString("F2", CultureInfo.InvariantCulture) : "0.00";
                        sig.OutcomeStatus = isTrail
                            ? $"Trailing Stop ilə Bağlandı (TRAIL {pnlSign}{pnlFormatted}%) ⚪"
                            : $"Qorunmuş Breakeven ilə Bağlandı (BE {pnlSign}{pnlFormatted}% NEYTRAL) ⚪";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_{(isTrail ? "TRAIL" : "BE")}";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            string alertText = isTrail
                                ? $"Trailing Stop (TRAIL {pnlSign}{pnlFormatted}%)"
                                : $"Breakeven (BE {pnlSign}{pnlFormatted}% - NEYTRAL)";
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, alertText, exitPrice, sig.ResultPercent.Value);
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
                    // 5. Long Time Expiry (Yalnız timeframe TTL çatanda)
                    else if (isMaxTimeReached && !sig.OutcomeAlertSent)
                    {
                        if (sig.Tp1Notified) return;
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
                    // 1. Short TP1 (TP_A 1.5R) Hit - 40% bağlandı
                    if (sig.TakeProfit1 > 0 && !sig.Tp1Notified && (snap.SessionLow <= sig.TakeProfit1 || snap.Last <= (sig.TakeProfit1 + tickSize)))
                    {
                        sig.Tp1Notified = true;
                        sig.IsPartial1Closed = true;
                        sig.RemainingPositionRatio = 1m - BotConstants.Thresholds.PartialTp1;
                        sig.CloseReason = "TP_A";

                        decimal pnl1 = Math.Round(((sig.EntryPrice - sig.TakeProfit1) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent = Math.Round(BotConstants.Thresholds.PartialTp1 * pnl1, 2);
                        sig.ProfitPercentAchieved = pnl1;

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
                            sig.OutcomeStatus = "Hədəf A (TP_A 1.5R) (TAM MƏNFƏƏT) ✅";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.5R) (TAM MƏNFƏƏT)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf A (TP_A 1.5R) [40% Qazanc Bağlandı; BE/trail sonra]", exitPrice, pnl1);
                            }
                        }
                    }
                    // 2. Short TP2 (TP_B) Hit - Yalnız TP1-dən sonra qalan hissədən 50% bağlanır (ilkinin 30%-i), 30% trail edir
                    else if (!sig.IsPartial2Closed && sig.Tp1Notified && !sig.OutcomeAlertSent && sig.TakeProfit2 > 0 && (snap.SessionLow <= sig.TakeProfit2 || snap.Last <= (sig.TakeProfit2 + tickSize)))
                    {
                        decimal pnl2 = Math.Round(((sig.EntryPrice - sig.TakeProfit2) / sig.EntryPrice) * 100, 2);
                        decimal closedPortion = 0.30m;
                        sig.RealizedProfitPercent += Math.Round(closedPortion * pnl2, 2);
                        sig.RemainingPositionRatio = 0.30m;
                        sig.IsPartial2Closed = true;
                        sig.Tp2Notified = true;
                        sig.CloseReason = "TP_B";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TP2";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf B (TP_B) [30% bağlandı, 30% trail]", exitPrice, pnl2);
                        }
                    }
                    // 3. Short Breakeven / Trail Hit (YALNIZ TP1-dən sonra qalan hissə BE/Trail stopuna dəyərsə)
                    else if (sig.Tp1Notified && !sig.OutcomeAlertSent && !sig.IsClosed && (snap.Last >= sig.StopLoss || snap.SessionHigh >= sig.StopLoss))
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;

                        bool isTrail = sig.StopLoss < sig.EntryPrice;
                        sig.CloseReason = isTrail ? "TRAIL" : "BE";

                        decimal exitPnl = Math.Round(((sig.EntryPrice - sig.StopLoss) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent += Math.Round(sig.RemainingPositionRatio * exitPnl, 2);
                        sig.RemainingPositionRatio = 0m;
                        sig.ResultPercent = Math.Round(sig.RealizedProfitPercent - 0.10m, 2);

                        sig.Status = (sig.ResultPercent >= 0) ? SignalStatus.Success : SignalStatus.Neutral;
                        string pnlSign = sig.ResultPercent >= 0 ? "+" : "";
                        string pnlFormatted = sig.ResultPercent.HasValue ? sig.ResultPercent.Value.ToString("F2", CultureInfo.InvariantCulture) : "0.00";
                        sig.OutcomeStatus = isTrail
                            ? $"Trailing Stop ilə Bağlandı (TRAIL {pnlSign}{pnlFormatted}%) ⚪"
                            : $"Qorunmuş Breakeven ilə Bağlandı (BE {pnlSign}{pnlFormatted}% NEYTRAL) ⚪";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_{(isTrail ? "TRAIL" : "BE")}";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            string alertText = isTrail
                                ? $"Trailing Stop (TRAIL {pnlSign}{pnlFormatted}%)"
                                : $"Breakeven (BE {pnlSign}{pnlFormatted}% - NEYTRAL)";
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, alertText, exitPrice, sig.ResultPercent.Value);
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
                    // 5. Short Time Expiry (Yalnız timeframe TTL çatanda)
                    else if (isMaxTimeReached && !sig.OutcomeAlertSent)
                    {
                        if (sig.Tp1Notified) return;
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
                    // BUG 2: Sayğac YALNIZ SignalAlertSent==true VƏ IsTest==false VƏ CloseReason SL/SL_RESTART_CATCHUP.
                    bool isHardStopLoss = sig.CloseReason == "SL" || sig.CloseReason == "SL_RESTART_CATCHUP";
                    if (sig.Status == SignalStatus.Failed && isHardStopLoss && sig.SignalAlertSent && !sig.IsTest)
                    {
                        int losses;
                        List<int> lossIdsSnapshot;
                        lock (_lossLock)
                        {
                            _consecutiveLossSignalIds.Add(sig.Id);
                            losses = _consecutiveLossSignalIds.Count;
                            lossIdsSnapshot = _consecutiveLossSignalIds.ToList();
                        }
                        Interlocked.Exchange(ref _consecutiveLosses, losses);

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
                                        const string cbAlertMsg = "⚠️ <b>RISK CIRCUIT BREAKER AKTİVLƏŞDİ:</b>\n\n" +
                                            "Ardıcıl 2 uğursuz əməliyyat (Stop Loss) qeydə alındı. Bazar skaneri kapitalı qorumaq üçün <b>4 saatlıq</b> müşahidə rejiminə keçdi.";
                                        await _telegramService.SendCircuitBreakerAlertAsync(cbAlertMsg, lossIdsSnapshot);
                                    }
                                    catch (Exception _ex) { Console.WriteLine($"[BackgroundMarketScanner] Swallowed exception: {_ex.Message}"); }
                                });
                            }
                        }
                    }
                    else if (sig.CloseReason == "TP_B" || sig.CloseReason == "TP3")
                    {
                        lock (_lossLock)
                        {
                            _consecutiveLossSignalIds.Clear();
                        }
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

            // Trail + delayed BE (only if !IsClosed && Tp1Notified)
            if (!sig.IsClosed && !sig.OutcomeAlertSent && sig.Tp1Notified)
            {
                decimal atr = ind.Atr > 0 ? ind.Atr : (sig.EntryPrice * (sig.AtrPercent > 0 ? sig.AtrPercent / 100m : 0.01m));
                decimal currentMove = isLong
                    ? (sig.EntryPrice > 0 ? ((currentLast - sig.EntryPrice) / sig.EntryPrice) * 100m : 0m)
                    : (sig.EntryPrice > 0 ? ((sig.EntryPrice - currentLast) / sig.EntryPrice) * 100m : 0m);

                // Fractal ±2 definition on closedCandles
                var swingHighs = new List<decimal>();
                var swingLows = new List<decimal>();
                for (int i = 2; i < closedCandles.Count - 2; i++)
                {
                    if (closedCandles[i].High > closedCandles[i - 1].High && closedCandles[i].High > closedCandles[i - 2].High &&
                        closedCandles[i].High > closedCandles[i + 1].High && closedCandles[i].High > closedCandles[i + 2].High)
                    {
                        swingHighs.Add(closedCandles[i].High);
                    }

                    if (closedCandles[i].Low < closedCandles[i - 1].Low && closedCandles[i].Low < closedCandles[i - 2].Low &&
                        closedCandles[i].Low < closedCandles[i + 1].Low && closedCandles[i].Low < closedCandles[i + 2].Low)
                    {
                        swingLows.Add(closedCandles[i].Low);
                    }
                }

                bool slChanged = false;

                // BE trigger (once)
                if (!sig.BreakevenTriggered)
                {
                    bool mfeBeTrigger = sig.MfePercent >= 1.5m * riskRPct && currentMove >= 1.2m * riskRPct;
                    bool fractalBeTrigger = isLong
                        ? ((swingLows.Count >= 2 && swingLows[^1] > swingLows[^2]) || (swingLows.Count >= 1 && sig.SignalSwingLow > 0 && swingLows[^1] > sig.SignalSwingLow))
                        : ((swingHighs.Count >= 2 && swingHighs[^1] < swingHighs[^2]) || (swingHighs.Count >= 1 && sig.SignalSwingHigh > 0 && swingHighs[^1] < sig.SignalSwingHigh));

                    if (mfeBeTrigger || fractalBeTrigger)
                    {
                        decimal beStop = isLong
                            ? SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice + BotConstants.Thresholds.BeBufferAtr * atr)
                            : SignalEngine.RoundToCoinPrecision(sig.EntryPrice, sig.EntryPrice - BotConstants.Thresholds.BeBufferAtr * atr);

                        bool isBeImprovement = isLong ? (beStop > sig.StopLoss) : (beStop < sig.StopLoss);
                        if (isBeImprovement)
                        {
                            sig.StopLoss = beStop;
                            slChanged = true;
                        }
                        sig.BreakevenTriggered = true;
                    }
                }

                // Trail (always after TP_A, ratchets only)
                if (isLong)
                {
                    decimal trailCandidate = sig.StopLoss;
                    decimal cand1 = lastClosed.High - (BotConstants.Thresholds.TrailAtr * atr);
                    if (cand1 > trailCandidate) trailCandidate = cand1;

                    if (swingLows.Count > 0)
                    {
                        decimal cand2 = swingLows[^1] - (BotConstants.Thresholds.SlBufferAtr * atr);
                        if (cand2 > trailCandidate) trailCandidate = cand2;
                    }

                    trailCandidate = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, trailCandidate);

                    // candidate must remain on the correct side of last price
                    if (trailCandidate < currentLast && trailCandidate > sig.StopLoss)
                    {
                        sig.StopLoss = trailCandidate;
                        sig.TrailPrice = trailCandidate;
                        slChanged = true;
                    }
                }
                else // Short
                {
                    decimal trailCandidate = sig.StopLoss;
                    decimal cand1 = lastClosed.Low + (BotConstants.Thresholds.TrailAtr * atr);
                    if (cand1 < trailCandidate) trailCandidate = cand1;

                    if (swingHighs.Count > 0)
                    {
                        decimal cand2 = swingHighs[^1] + (BotConstants.Thresholds.SlBufferAtr * atr);
                        if (cand2 < trailCandidate) trailCandidate = cand2;
                    }

                    trailCandidate = SignalEngine.RoundToCoinPrecision(sig.EntryPrice, trailCandidate);

                    // candidate must remain on the correct side of last price
                    if (trailCandidate > currentLast && trailCandidate < sig.StopLoss)
                    {
                        sig.StopLoss = trailCandidate;
                        sig.TrailPrice = trailCandidate;
                        slChanged = true;
                    }
                }

                if (slChanged)
                {
                    await unitOfWork.Signals.UpdateAsync(sig);
                    await unitOfWork.SaveChangesAsync(stoppingToken);
                }
            }
        }
    }
}
