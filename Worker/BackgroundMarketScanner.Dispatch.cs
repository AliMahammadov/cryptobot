using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Services;
using CryptoSense.Domain.Common;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using CryptoSense.Infrastructure.Telegram;

namespace CryptoSense.Worker
{
    public partial class BackgroundMarketScanner
    {
        public static async Task<(bool Success, bool ShouldBreak, bool InvalidateArmed)> PersistOrRecoverSignalAsync(
            IUnitOfWork uow,
            FuturesSignal signal,
            ConcurrentDictionary<string, DateTime> lastAlertSent,
            ConcurrentDictionary<string, byte> coinLocks,
            string alertKey,
            string sym,
            CancellationToken ct)
        {
            // Check if existing delivered candle signal exists in DB
            var existingCandle = await uow.Signals.GetExistingCandleSignalAsync(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
            if (existingCandle != null && existingCandle.SignalAlertSent)
            {
                return (Success: false, ShouldBreak: true, InvalidateArmed: false);
            }

            // Check if unsent draft row exists for this candle + direction to reuse
            var unsentDraft = await uow.Signals.GetUnsentSignalForCandleAsync(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc, signal.Direction);
            if (unsentDraft != null)
            {
                signal.Id = unsentDraft.Id;
                signal.GeneratedAt = unsentDraft.GeneratedAt;
                Console.WriteLine($"[MarketScanner] Reusing existing unsent DB signal Id={unsentDraft.Id} for {signal.Symbol} {signal.Timeframe}");
                Console.WriteLine($"[EMIT_RETRY_ARMED] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} reason=TELEGRAM_FAIL_DB_REUSE");
                return (Success: true, ShouldBreak: false, InvalidateArmed: false);
            }

            if (signal.Id == 0)
            {
                try
                {
                    await uow.Signals.AddAsync(signal);
                    await uow.SaveChangesAsync(ct);
                    return (Success: true, ShouldBreak: false, InvalidateArmed: false);
                }
                catch (Exception dbEx)
                {
                    Console.WriteLine($"[MarketScanner] Duplicate signal insert collision for {signal.Symbol}: {dbEx.Message}");
                    
                    var recoveredUnsent = await uow.Signals.GetUnsentSignalForCandleAsync(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc, signal.Direction);
                    if (recoveredUnsent != null)
                    {
                        signal.Id = recoveredUnsent.Id;
                        Console.WriteLine($"[MarketScanner] Recovered unsent signal Id={recoveredUnsent.Id} after collision");
                        Console.WriteLine($"[EMIT_RETRY_ARMED] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} reason=TELEGRAM_FAIL_DB_REUSE");
                        return (Success: true, ShouldBreak: false, InvalidateArmed: false);
                    }

                    var delivered2 = await uow.Signals.GetExistingCandleSignalAsync(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                    if (delivered2 != null && delivered2.SignalAlertSent)
                    {
                        return (Success: false, ShouldBreak: true, InvalidateArmed: false);
                    }

                    SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                    Console.WriteLine($"[EMIT_RETRY_ARMED] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} reason=UNIQUE_COLLISION_NO_ROW");
                    return (Success: false, ShouldBreak: true, InvalidateArmed: true);
                }
            }

            return (Success: true, ShouldBreak: false, InvalidateArmed: false);
        }

        private async Task<(bool Dispatched, string? SkipReason)> TryDispatchQualifiedAsync(
            FuturesSignal signal,
            LivePriceSnapshot snap,
            IUnitOfWork uow,
            CancellationToken ct)
        {
            if (snap == null || snap.DataAgeMs > BotConstants.Thresholds.MaxDataAgeMs)
            {
                Interlocked.Increment(ref _hourlyTelemetry.SkipStale);
                SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                Console.WriteLine($"[MarketScanner] SKIP_STALE: {signal.Symbol} dataAgeMs={(snap?.DataAgeMs ?? -1)} source={snap?.Source}");
                return (false, "Stale");
            }

            signal.EntryPrice = snap.Last;
            signal.CurrentPrice = snap.Last;
            signal.PriceSource = snap.Source;
            signal.ExchangeTsMs = snap.ExchangeTsMs;
            signal.DataAgeMs = snap.DataAgeMs;
            var candleDuration = signal.Timeframe == "4h" ? TimeSpan.FromHours(4) : TimeSpan.FromHours(1);
            signal.CandleCloseTimeUtc = signal.SourceCandleOpenTimeUtc + candleDuration;
            _livePriceCache.ResetSession(signal.Symbol);

            var (gateOk, gateReason) = SignalEmitGates.Evaluate(signal, snap.Last, snap.DataAgeMs, signal.Timeframe);
            if (!gateOk)
            {
                SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                if (gateReason == "SL")
                {
                    Interlocked.Increment(ref _hourlyTelemetry.SkipSL);
                    Console.WriteLine($"[MarketScanner] SL too wide: {signal.Symbol} ({signal.Timeframe}). Trade skipped.");
                }
                else if (gateReason == "RR")
                {
                    Interlocked.Increment(ref _hourlyTelemetry.SkipRR);
                    Console.WriteLine($"[MarketScanner] R:R filter blocked: {signal.Symbol} ({signal.Timeframe}).");
                }
                else if (gateReason == "DataAge")
                {
                    Interlocked.Increment(ref _hourlyTelemetry.SkipStale);
                    Console.WriteLine($"[MarketScanner] SKIP_STALE: {signal.Symbol} dataAgeMs={snap.DataAgeMs}");
                }
                return (false, gateReason);
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
                var sym = signal.Symbol;

                // 0. Direction lock check
                if (_blockedDirection != null && signal.Direction == _blockedDirection.Value)
                {
                    Interlocked.Increment(ref _hourlyTelemetry.SkipDirLock);
                    Console.WriteLine($"[MarketScanner] SKIP_DIR_LOCK: {sym} direction {signal.Direction} is locked by CircuitBreaker.");
                    return (false, "DirLock");
                }

                // 1. Lock check
                if (_coinActiveLocks.ContainsKey(sym))
                {
                    Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                    return (false, "Lock");
                }
                if (_coinActiveLocks.Count >= MaxGlobalOpenPositions)
                {
                    Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                    return (false, "Lock");
                }
                if (await uow.Signals.HasActiveSignalForSymbolAsync(sym))
                {
                    _coinActiveLocks.TryAdd(sym, 1);
                    Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                    return (false, "Lock");
                }

                // 2. Cooldown check
                var lastClosedSig = await uow.Signals.GetLastClosedSignalForSymbolAsync(sym);
                if (lastClosedSig?.ClosedAt != null)
                {
                    // 6-hour same direction cooldown for 1h
                    if (signal.Timeframe == "1h" && lastClosedSig.Direction == signal.Direction)
                    {
                        if (DateTime.UtcNow - lastClosedSig.ClosedAt.Value < TimeSpan.FromHours(6))
                        {
                            Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                            Console.WriteLine($"[MarketScanner] 6-hour same direction cooldown active for {sym} ({signal.Direction}). Skipping.");
                            return (false, "Lock");
                        }
                    }

                    var candleCooldown = signal.Timeframe == "4h" ? TimeSpan.FromHours(4) : TimeSpan.FromHours(1);
                    if (DateTime.UtcNow - lastClosedSig.ClosedAt.Value < candleCooldown)
                    {
                        Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                        Console.WriteLine($"[MarketScanner] 1-candle cooldown active for {sym}. Skipping.");
                        return (false, "Lock");
                    }
                }

                // 3. Morning cap (05:00-07:00 Baku / UTC+4)
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
                            return (false, "HourCap");
                        }
                    }
                }

                // 4. Hour cap (max 3 per hour, max 3 same dir)
                var hourKey = signal.CandleCloseTimeUtc.ToString("yyyyMMdd_HH");
                var hourDispatches = _hourlyDispatches.GetOrAdd(hourKey, _ => new List<(string Symbol, SignalDirection Direction)>());
                lock (hourDispatches)
                {
                    if (hourDispatches.Count >= 3)
                    {
                        Interlocked.Increment(ref _hourlyTelemetry.SkipHourCap);
                        Console.WriteLine($"[MarketScanner] Hourly limit reached (3 signals sent for hour {hourKey}). Skipping {signal.Symbol}.");
                        return (false, "HourCap");
                    }

                    int sameDirectionCount = hourDispatches.Count(d => d.Direction == signal.Direction);
                    if (sameDirectionCount >= 3)
                    {
                        Interlocked.Increment(ref _hourlyTelemetry.SkipHourCap);
                        Console.WriteLine($"[MarketScanner] Max 3 same direction signals reached for hour {hourKey} ({signal.Direction}). Skipping {signal.Symbol}.");
                        return (false, "HourCap");
                    }
                }

                // 5. alertKey dedup
                var alertKey = $"{signal.Symbol}_{signal.Direction}_{signal.Timeframe}_{signal.SourceCandleOpenTimeUtc:yyyyMMddHHmmss}";
                if (_lastAlertSent.ContainsKey(alertKey) || signal.SignalAlertSent)
                {
                    return (false, "DuplicateAlertKey");
                }

                // Check DB explicitly for existing candle signal already delivered
                var existingCandle = await uow.Signals.GetExistingCandleSignalAsync(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                if (existingCandle != null && existingCandle.SignalAlertSent)
                {
                    _lastAlertSent[alertKey] = DateTime.UtcNow;
                    _lastSymbolAlertTime[signal.Symbol] = DateTime.UtcNow;
                    _coinActiveLocks.TryAdd(sym, 1);
                    return (false, "AlreadyDeliveredInDb");
                }

                signal.Number = 0;

                // 6. Persist
                var persistRes = await PersistOrRecoverSignalAsync(uow, signal, _lastAlertSent, _coinActiveLocks, alertKey, sym, ct);
                if (!persistRes.Success || persistRes.ShouldBreak)
                {
                    return (false, "PersistFailed");
                }

                // 7. Telegram Send
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
                    return (true, null);
                }
                else
                {
                    _lastAlertSent.TryRemove(alertKey, out _);
                    SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                    _coinActiveLocks.TryRemove(sym, out _);

                    Console.WriteLine($"[EMIT_RETRY_ARMED] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} reason=TELEGRAM_FAIL_DELETED");

                    Interlocked.Increment(ref _hourlyTelemetry.TelegramFail);
                    Console.WriteLine($"[MarketScanner] TELEGRAM_FAIL: {signal.Symbol} {signal.Timeframe} — SendSignalAlert false (DataAge? R:R? UserFilter?)");

                    // DELETE unsent signal from DB so unique index is freed and it never blocks future retry
                    try
                    {
                        await uow.Signals.DeleteUnsentForCandleAsync(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc, signal.Direction);
                    }
                    catch (Exception cleanEx)
                    {
                        Console.WriteLine($"[MarketScanner] Failed to delete unsent signal {signal.Id}: {cleanEx.Message}");
                    }
                    return (false, "TelegramFail");
                }
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }
    }
}
