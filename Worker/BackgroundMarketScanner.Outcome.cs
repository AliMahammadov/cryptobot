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

                // DataAge > 3500: REST last gÃ¶tÃ¼r, skip etmÉ™ â€” SL buraxÄ±lmasÄ±n
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

                Console.WriteLine($"[StartupCatchUp] {openSignals.Count} aktiv siqnal Ã¼zrÉ™ restart catch-up yoxlanÄ±ÅŸÄ± baÅŸladÄ±...");

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
                        sig.OutcomeStatus = "Stop Loss (SL) (Restart Catch-up) âŒ";

                        decimal exitPnl = isLong
                            ? Math.Round(((sig.StopLoss - sig.EntryPrice) / sig.EntryPrice) * 100, 2)
                            : Math.Round(((sig.EntryPrice - sig.StopLoss) / sig.EntryPrice) * 100, 2);
                        sig.ResultPercent = Math.Round(exitPnl - 0.10m, 2);

                        await uow.Signals.UpdateAsync(sig);
                        await uow.SaveChangesAsync(CancellationToken.None);

                        _coinActiveLocks.TryRemove(sig.Symbol, out _);

                        if (sig.SignalAlertSent)
                        {
                            await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL) [Restart AÅŸkarlanmasÄ±]", sig.StopLoss, sig.ResultPercent.Value);
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
                            sig.OutcomeStatus = "HÉ™dÉ™f A (TP_A 1.0R) (TAM MÆNFÆÆT) [Restart Catch-up] âœ…";

                            await uow.Signals.UpdateAsync(sig);
                            await uow.SaveChangesAsync(CancellationToken.None);
                            _coinActiveLocks.TryRemove(sig.Symbol, out _);

                            if (sig.SignalAlertSent)
                            {
                                await _telegramService.SendOutcomeAlertAsync(sig, "HÉ™dÉ™f A (TP_A 1.0R) [Restart Catch-up]", sig.TakeProfit1, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            await uow.Signals.UpdateAsync(sig);
                            await uow.SaveChangesAsync(CancellationToken.None);
                            if (sig.SignalAlertSent)
                            {
                                await _telegramService.SendOutcomeAlertAsync(sig, "HÉ™dÉ™f A (TP_A 1.0R) [Restart Catch-up + BE Aktiv]", sig.TakeProfit1, pnl1);
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

                // Maksimum Ã¶mÃ¼r yalnÄ±z timeframe TTL ilÉ™: 1h -> 8 saat (480 dÉ™q), 4h -> 24 saat (1440 dÉ™q).
                int ttlMinutes = sig.Timeframe == "4h" ? 1440 : 480;
                DateTime expiryUtc = (sig.ExpiryTimeUtc != default && sig.ExpiryTimeUtc > sig.GeneratedAt)
                    ? sig.ExpiryTimeUtc
                    : sig.GeneratedAt.AddMinutes(ttlMinutes);

                // AÃ§Ä±q siqnallarÄ±n (o cÃ¼mlÉ™dÉ™n cari 4h BTC/DOGE) 3 saatda kÉ™silmÉ™mÉ™si Ã¼Ã§Ã¼n timeframe TTL tÉ™min edilir:
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
                    // 1. Long TP1 (TP_A 1.0R) Hit - 50% baÄŸlandÄ± + BE AktivlÉ™ÅŸdi
                    if (sig.TakeProfit1 > 0 && !sig.Tp1Notified && (snap.SessionHigh >= sig.TakeProfit1 || snap.Last >= (sig.TakeProfit1 - tickSize)))
                    {
                        sig.Tp1Notified = true;
                        sig.IsPartial1Closed = true;
                        sig.RemainingPositionRatio = 0.50m;
                        sig.CloseReason = "TP_A";

                        decimal pnl1 = Math.Round(((sig.TakeProfit1 - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent = Math.Round(0.50m * pnl1, 2);
                        sig.ProfitPercentAchieved = pnl1;

                        // BÆND 3: Qalan 50%: SL-i BE-yÉ™ Ã§É™k YALNIZ TP_A (1.0R) vurulandan SONRA.
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
                            sig.OutcomeStatus = "HÉ™dÉ™f A (TP_A 1.0R) (TAM MÆNFÆÆT) âœ…";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "HÉ™dÉ™f A (TP_A 1.0R) (TAM MÆNFÆÆT)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "HÉ™dÉ™f A (TP_A 1.0R) [50% Qazanc BaÄŸlandÄ± + BE Aktiv]", exitPrice, pnl1);
                            }
                        }
                    }
                    // 2. Long TP2 (TP_B) Hit - YalnÄ±z TP1-dÉ™n sonra qalan 50% baÄŸlanÄ±r
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
                        sig.OutcomeStatus = "HÉ™dÉ™f B (TP_B) (TAM MÆNFÆÆT) âœ…";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TP2";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "HÉ™dÉ™f B (TP_B) [Qalan 50% Tam MÉ™nfÉ™É™t]", exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 3. Long Breakeven Hit (YALNIZ TP1-dÉ™n sonra qalan 50% BE stopuna dÉ™yÉ™rsÉ™)
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

                        // BÆND 4: BE-yÉ™ qayÄ±dÄ±b baÄŸlandÄ± (NEYTRAL, net |PnL|<0.20% win rate-É™ yox)
                        sig.Status = SignalStatus.Neutral;
                        string pnlSign = sig.ResultPercent >= 0 ? "+" : "";
                        string pnlFormatted = sig.ResultPercent.HasValue ? sig.ResultPercent.Value.ToString("F2", CultureInfo.InvariantCulture) : "0.00";
                        sig.OutcomeStatus = $"QorunmuÅŸ Breakeven ilÉ™ BaÄŸlandÄ± (BE {pnlSign}{pnlFormatted}% NEYTRAL) âšª";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_BE";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            string beOutcomeText = $"Breakeven (BE {pnlSign}{pnlFormatted}% - NEYTRAL)";
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, beOutcomeText, exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 4. Long Initial Stop Loss Hit (TP1 vurulmadan É™vvÉ™l)
                    else if (!sig.Tp1Notified && !sig.OutcomeAlertSent && !sig.IsClosed && (snap.SessionLow <= sig.StopLoss || snap.Last <= sig.StopLoss))
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "SL";
                        sig.Status = SignalStatus.Failed;
                        sig.OutcomeStatus = "Stop Loss (SL) (UÄURSUZ) âŒ";
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
                    // 5. Long Time Expiry (YalnÄ±z timeframe TTL Ã§atanda)
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
                            sig.OutcomeStatus = $"{sig.Timeframe} MÃ¼ddÉ™ti Bitdi (TP1 SonrasÄ± TIME ilÉ™ Tam BaÄŸlandÄ±: +{sig.ResultPercent}%) âšª";
                        }
                        else
                        {
                            sig.ResultPercent = Math.Round(netPnl, 2);
                            if (netPnl > 0.2m)
                            {
                                sig.Status = SignalStatus.Success;
                                sig.OutcomeStatus = $"{sig.Timeframe} MÃ¼ddÉ™ti Bitdi (KiÃ§ik Bazar Ã‡Ä±xÄ±ÅŸÄ±: +{netPnl}%) âšª";
                            }
                            else if (Math.Abs(netPnl) <= 0.2m)
                            {
                                sig.Status = SignalStatus.Neutral;
                                sig.OutcomeStatus = $"{sig.Timeframe} MÃ¼ddÉ™ti Bitdi (Neytral/Konsolidasiya) âšª";
                            }
                            else
                            {
                                sig.Status = SignalStatus.Failed;
                                sig.OutcomeStatus = $"{sig.Timeframe} MÃ¼ddÉ™ti Bitdi (UÄURSUZ: {netPnl}%) âŒ";
                            }
                        }

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TIME";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            decimal finalPnl = sig.ResultPercent ?? netPnl;
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} MÃ¼ddÉ™ti Bitdi", exitPrice, finalPnl);
                        }
                    }
                }
                else // SHORT
                {
                    // 1. Short TP1 (TP_A 1.0R) Hit - 50% baÄŸlandÄ± + BE AktivlÉ™ÅŸdi
                    if (sig.TakeProfit1 > 0 && !sig.Tp1Notified && (snap.SessionLow <= sig.TakeProfit1 || snap.Last <= (sig.TakeProfit1 + tickSize)))
                    {
                        sig.Tp1Notified = true;
                        sig.IsPartial1Closed = true;
                        sig.RemainingPositionRatio = 0.50m;
                        sig.CloseReason = "TP_A";

                        decimal pnl1 = Math.Round(((sig.EntryPrice - sig.TakeProfit1) / sig.EntryPrice) * 100, 2);
                        sig.RealizedProfitPercent = Math.Round(0.50m * pnl1, 2);
                        sig.ProfitPercentAchieved = pnl1;

                        // BÆND 3: Qalan 50%: SL-i BE-yÉ™ Ã§É™k YALNIZ TP_A (1.0R) vurulandan SONRA.
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
                            sig.OutcomeStatus = "HÉ™dÉ™f A (TP_A 1.0R) (TAM MÆNFÆÆT) âœ…";

                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "HÉ™dÉ™f A (TP_A 1.0R) (TAM MÆNFÆÆT)", exitPrice, sig.ResultPercent.Value);
                            }
                        }
                        else
                        {
                            await unitOfWork.Signals.UpdateAsync(sig);
                            await unitOfWork.SaveChangesAsync(stoppingToken);

                            string dedupKey = $"{sig.Id}_TP1";
                            if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                            {
                                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "HÉ™dÉ™f A (TP_A 1.0R) [50% Qazanc BaÄŸlandÄ± + BE Aktiv]", exitPrice, pnl1);
                            }
                        }
                    }
                    // 2. Short TP2 (TP_B) Hit - YalnÄ±z TP1-dÉ™n sonra qalan 50% baÄŸlanÄ±r
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
                        sig.OutcomeStatus = "HÉ™dÉ™f B (TP_B) (TAM MÆNFÆÆT) âœ…";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TP2";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "HÉ™dÉ™f B (TP_B) [Qalan 50% Tam MÉ™nfÉ™É™t]", exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 3. Short Breakeven Hit (YALNIZ TP1-dÉ™n sonra qalan 50% BE stopuna dÉ™yÉ™rsÉ™)
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

                        // BÆND 4: BE-yÉ™ qayÄ±dÄ±b baÄŸlandÄ± (NEYTRAL, net |PnL|<0.20% win rate-É™ yox)
                        sig.Status = SignalStatus.Neutral;
                        string pnlSign = sig.ResultPercent >= 0 ? "+" : "";
                        string pnlFormatted = sig.ResultPercent.HasValue ? sig.ResultPercent.Value.ToString("F2", CultureInfo.InvariantCulture) : "0.00";
                        sig.OutcomeStatus = $"QorunmuÅŸ Breakeven ilÉ™ BaÄŸlandÄ± (BE {pnlSign}{pnlFormatted}% NEYTRAL) âšª";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_BE";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            string beOutcomeText = $"Breakeven (BE {pnlSign}{pnlFormatted}% - NEYTRAL)";
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, beOutcomeText, exitPrice, sig.ResultPercent.Value);
                        }
                    }
                    // 4. Short Initial Stop Loss Hit (TP1 vurulmadan É™vvÉ™l)
                    else if (!sig.Tp1Notified && !sig.OutcomeAlertSent && !sig.IsClosed && (snap.SessionHigh >= sig.StopLoss || snap.Last >= sig.StopLoss))
                    {
                        sig.OutcomeAlertSent = true;
                        sig.IsClosed = true;
                        sig.ClosePrice = exitPrice;
                        sig.ClosedAt = DateTime.UtcNow;
                        sig.CloseReason = "SL";
                        sig.Status = SignalStatus.Failed;
                        sig.OutcomeStatus = "Stop Loss (SL) (UÄURSUZ) âŒ";
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
                    // 5. Short Time Expiry (YalnÄ±z timeframe TTL Ã§atanda)
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
                            sig.OutcomeStatus = $"{sig.Timeframe} MÃ¼ddÉ™ti Bitdi (TP1 SonrasÄ± TIME ilÉ™ Tam BaÄŸlandÄ±: +{sig.ResultPercent}%) âšª";
                        }
                        else
                        {
                            sig.ResultPercent = Math.Round(netPnl, 2);
                            if (netPnl > 0.2m)
                            {
                                sig.Status = SignalStatus.Success;
                                sig.OutcomeStatus = $"{sig.Timeframe} MÃ¼ddÉ™ti Bitdi (KiÃ§ik Bazar Ã‡Ä±xÄ±ÅŸÄ±: +{netPnl}%) âšª";
                            }
                            else if (Math.Abs(netPnl) <= 0.2m)
                            {
                                sig.Status = SignalStatus.Neutral;
                                sig.OutcomeStatus = $"{sig.Timeframe} MÃ¼ddÉ™ti Bitdi (Neytral/Konsolidasiya) âšª";
                            }
                            else
                            {
                                sig.Status = SignalStatus.Failed;
                                sig.OutcomeStatus = $"{sig.Timeframe} MÃ¼ddÉ™ti Bitdi (UÄURSUZ: {netPnl}%) âŒ";
                            }
                        }

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);

                        string dedupKey = $"{sig.Id}_TIME";
                        if (_sentOutcomeDeduplication.TryAdd(dedupKey, DateTime.UtcNow))
                        {
                            decimal finalPnl = sig.ResultPercent ?? netPnl;
                            if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} MÃ¼ddÉ™ti Bitdi", exitPrice, finalPnl);
                        }
                    }
                }

                if (sig.IsClosed)
                {
                    // YalnÄ±z real Stop Loss streak-i breaker-É™ dÃ¼ÅŸÃ¼r.
                    // TIME / NO_EDGE / BE / ALERT_NEVER_SENT Failed sayÄ±lsa belÉ™ breaker-É™ GETMÆSÄ°N.
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
                                        await _telegramService.BroadcastSystemAlertAsync("âš ï¸ <b>RISK CIRCUIT BREAKER AKTÄ°VLÆÅDÄ°:</b>\n\n" +
                                            "ArdÄ±cÄ±l 2 uÄŸursuz É™mÉ™liyyat (Stop Loss) qeydÉ™ alÄ±ndÄ±. Bazar skaneri kapitalÄ± qorumaq Ã¼Ã§Ã¼n <b>4 saatlÄ±q</b> mÃ¼ÅŸahidÉ™ rejiminÉ™ keÃ§di.");
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

            // BÆND E: Alt LONG Ã¼Ã§Ã¼n BTC É™ks rejimi aÅŸkarlananda dÉ™rhal Ã§Ä±xÄ±ÅŸ (ETH daxil, BTCUSDT istisna)
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
                        sig.OutcomeStatus = "BTC É™ks rejim (INVALIDATION) âŒ";

                        await unitOfWork.Signals.UpdateAsync(sig);
                        await unitOfWork.SaveChangesAsync(stoppingToken);
                        if (sig.SignalAlertSent)
                        {
                            await _telegramService.SendOutcomeAlertAsync(sig, "BTC É™ks â€” Ã§Ä±xÄ±ÅŸ", exitPrice, netPnl);
                        }
                        return;
                    }
                }
                catch (Exception btcEx)
                {
                    Console.WriteLine($"[EarlyExit] Error checking BTC compass for {sig.Symbol}: {btcEx.Message}");
                }
            }

            // Simvol baÅŸÄ±na max 1 kline / 15m throttle
            // YalnÄ±z 15m ÅŸam qapanÄ±ÅŸÄ±nda vÉ™ ya É™n tez 30s-dÉ™n bir yoxla
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

            // Son qapalÄ± ÅŸamlar: forming candle-i Ã§Ä±xar
            var closedCandles = rawKlines.Take(rawKlines.Count - 1).ToList();
            if (closedCandles.Count < 20) return;

            var lastClosed = closedCandles[^1];
            var lastCandleTime = DateTimeOffset.FromUnixTimeMilliseconds(lastClosed.OpenTime).UtcDateTime;

            // ÆgÉ™r yeni ÅŸam qapanmayÄ±bsa, tÉ™krar hesablama
            if (sig.LastObservedCandleTime != default && lastCandleTime <= sig.LastObservedCandleTime)
            {
                return;
            }

            sig.LastObservedCandleTime = lastCandleTime;
            sig.CandlesObserved++;

            // NO_EDGE (Ã¶lÃ¼ edge) â€” timeframe-nisbi: (1h: 4 ÅŸam = 4 saat) vÉ™ ya (4h: 3 ÅŸam = 12 saat)
            // YALNIZ: MFE < 0.4R VÆ TP_A hit olmayÄ±b.
            // Vaxt kill TÆTBÄ°Q OLUNMASIN É™gÉ™r: MFE >= 0.4R VÆ ya qiymÉ™t TP_A-ya yaxÄ±ndÄ±r / artÄ±q +R-dÉ™dir VÆ ya TP_A artÄ±q vurulub.
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
                    sig.OutcomeStatus = $"{sig.Timeframe} HÉ™rÉ™kÉ™tsiz (NO_EDGE Neytral: {netPnl}%) âšª";
                }
                else
                {
                    sig.Status = SignalStatus.Failed;
                    sig.OutcomeStatus = $"{sig.Timeframe} HÉ™rÉ™kÉ™tsiz (NO_EDGE Donma Ã§Ä±xÄ±ÅŸÄ±: {netPnl}%) âŒ";
                }
                sig.CloseReason = "NO_EDGE";

                await unitOfWork.Signals.UpdateAsync(sig);
                await unitOfWork.SaveChangesAsync(stoppingToken);
                if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "NO_EDGE (Donma Ã§Ä±xÄ±ÅŸÄ±)", exitPrice, netPnl);
                return;
            }

            // Problem 5: Invalidation
            // LONG baÄŸla: close < siqnal swing low VÆ [(SuperTrend flip VÆ RSI close<50) VEYA (2 ardÄ±cÄ±l É™ks ÅŸam VÆ vol>1.3Ã—SMA20)].
            // SHORT gÃ¼zgÃ¼ (swing high, RSI>50).
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
                    sig.OutcomeStatus = "Struktur Pozuldu (INVALIDATION) âŒ";

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
                    sig.OutcomeStatus = "Struktur Pozuldu (INVALIDATION) âŒ";

                    await unitOfWork.Signals.UpdateAsync(sig);
                    await unitOfWork.SaveChangesAsync(stoppingToken);
                    if (sig.SignalAlertSent) await _telegramService.SendOutcomeAlertAsync(sig, "Struktur Pozuldu (INVALIDATION)", exitPrice, netPnl);
                    return;
                }
            }

            // Problem 6: TP2-dÉ™n sonra trail son 3 qapalÄ± swing Â± 0.3*ATR
            if (sig.Tp2Notified && !sig.OutcomeAlertSent && !sig.IsClosed)
            {
                decimal atrVal = ind.Atr > 0 ? ind.Atr : (sig.EntryPrice * (sig.AtrPercent > 0 ? sig.AtrPercent / 100m : 0.01m));
                if (isLong)
                {
                    // Son 3 qapalÄ± swing low tap
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
                    // Son 3 qapalÄ± swing high tap
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
    }
}
