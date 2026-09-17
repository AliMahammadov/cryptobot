using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
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

            // Prioritet 1: Circuit breaker aktivdirsə, yeni skan dayandırılır
            if (DateTime.UtcNow < _circuitBreakerUntil)
            {
                Interlocked.Increment(ref _hourlyTelemetry.SkipCircuitBreaker);
                Console.WriteLine($"[SCAN_CYCLE_SKIP] reason=CIRCUIT_BREAKER cbUntil={_circuitBreakerUntil:HH:mm:ss}UTC");
                LatestTelemetrySnapshot = _hourlyTelemetry.Clone();
                await MaybeSendHourlyHeartbeatAsync(stoppingToken);
                LastScanUtc = DateTime.UtcNow;
                return;
            }
            else if (_circuitBreakerUntil != DateTime.MinValue)
            {
                lock (_lossLock)
                {
                    _consecutiveLossSignalIds.Clear();
                }
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
                LatestTelemetrySnapshot = _hourlyTelemetry.Clone();
                await MaybeSendHourlyHeartbeatAsync(stoppingToken);
                LastScanUtc = DateTime.UtcNow;
                return;
            }

            // Prioritet 2: Günlük -3.0% itki limiti çatdıqda yeni əməliyyat açılmır (Bakı vaxtı 00:00 ilə bağlı əməliyyatlar)
            try
            {
                var bakuDayStartUtc = DateTime.UtcNow.AddHours(4).Date.AddHours(-4);
                var todayClosedPnL = await unitOfWork.Signals.GetClosedPnlSinceAsync(bakuDayStartUtc);

                // MÜVƏQQƏTİ TEST: İstifadəçinin istəyi ilə yeni sistemin siqnallarını test etmək üçün günlük itki limiti müvəqqəti bypass edilir.
                // Sabah və ya test bitdikdən sonra şərt bərpa olunacaq.
                bool bypassDailyLossForTesting = true;
                if (!bypassDailyLossForTesting && todayClosedPnL <= BotConstants.Thresholds.DailyLossThreshold)
                {
                    Interlocked.Increment(ref _hourlyTelemetry.SkipDailyLoss);
                    Console.WriteLine($"[SCAN_CYCLE_SKIP] reason=DAILY_LOSS pnl={todayClosedPnL:F2}% threshold={BotConstants.Thresholds.DailyLossThreshold:F1}%");
                    LatestTelemetrySnapshot = _hourlyTelemetry.Clone();
                    await MaybeSendHourlyHeartbeatAsync(stoppingToken);
                    LastScanUtc = DateTime.UtcNow;
                    return;
                }
                else if (bypassDailyLossForTesting && todayClosedPnL <= BotConstants.Thresholds.DailyLossThreshold)
                {
                    Console.WriteLine($"[SCAN_CYCLE_TEST] DAILY_LOSS pnl={todayClosedPnL:F2}% threshold={BotConstants.Thresholds.DailyLossThreshold:F1}% (BYPASSED TEMPORARILY FOR TESTING)");
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

            // Filter coins at the root level before launching any analysis
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
                await Parallel.ForEachAsync(coinsToScan, new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = stoppingToken }, async (sym, ct) =>
                {
                    if (_coinActiveLocks.ContainsKey(sym) || !_scanningCoins.TryAdd(sym, 1))
                    {
                        Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                        return;
                    }

                    try
                    {
                        if (_coinActiveLocks.Count >= MaxGlobalOpenPositions)
                        {
                            Interlocked.Increment(ref _hourlyTelemetry.SkipMaxOpen);
                            return;
                        }

                        using var innerScope = _serviceProvider.CreateScope();
                        var engine = innerScope.ServiceProvider.GetRequiredService<ISignalEngine>();
                        var uow = innerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                        var innerMarketData = innerScope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

                        if (await uow.Signals.HasActiveSignalForSymbolAsync(sym))
                        {
                            _coinActiveLocks.TryAdd(sym, 1);
                            Interlocked.Increment(ref _hourlyTelemetry.SkipLock);
                            return;
                        }

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

                        var prioritizedTfs = new[] { "4h", "1h" }
                            .Where(tf => activeTimeframes.Contains(tf))
                            .ToList();

                        if (prioritizedTfs.Count == 0)
                        {
                            prioritizedTfs = new List<string> { "4h", "1h" };
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
                                Interlocked.Increment(ref _hourlyTelemetry.SkipMaxOpen);
                                break;
                            }

                            Interlocked.Increment(ref _hourlyTelemetry.CoinsScanned);

                            var signal = await engine.AnalyzeCoinAsync(sym, tf, isLiveScan: true, ct: ct);

                            // Thread-safe MaxConfluenceSeen update: directional max Max(score, 100 - score)
                            var confScore = Math.Max(signal.ConfluenceScore, 100m - signal.ConfluenceScore);
                            lock (_heartbeatLock)
                            {
                                if (confScore > _hourlyTelemetry.MaxConfluenceSeen)
                                {
                                    _hourlyTelemetry.MaxConfluenceSeen = confScore;
                                }
                            }

                            bool isTradeQualified = signal.Timeframe != "15m" && 
                                                    signal.Confidence >= BotConstants.Thresholds.MinConfluence1h4h &&
                                                    signal.SignalType != null &&
                                                    (signal.SignalType.Contains("LONG") || signal.SignalType.Contains("SHORT"));

                            if (!isTradeQualified)
                            {
                                // Strict single-bucket skip classification
                                // Priority: BtcGate > Range > Confluence > Volume > SL > RR > Chase > Gozleme
                                bool hasBtcGate = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("SKIP_BTC_BEAR_LONG") || r.Contains("SKIP_BTC_4H_OPPOSE") || r.Contains("SKIP_HTF_OPPOSE") || r.Contains("SKIP_BTC_RESIDUAL"));
                                bool hasBtcRange = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("SKIP_BTC_RANGE"));
                                bool hasConfluence = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("Confluence Filtri") || r.Contains("< 75.0%"));
                                bool hasVolume = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("SKIP_VOLUME"));
                                bool hasResidual = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("SKIP_BTC_RESIDUAL"));
                                bool hasEth = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("ETH SuperTrend") || r.Contains("ETH 1h"));
                                bool hasSL = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("SKIP_SL_TOO_WIDE") || r.Contains("SKIP_SL_TOO_TIGHT") || r.Contains("SKIP_NO_SWING"));
                                bool hasRR = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("SKIP_LOW_RR") || r.Contains("SKIP_NO_STRUCTURE_TARGET"));
                                bool hasChase = signal.AnalysisReasons != null && signal.AnalysisReasons.Any(r => r.Contains("SKIP_CHASE"));

                                if (hasBtcGate)
                                {
                                    if (signal.AnalysisReasons!.Any(r => r.Contains("SKIP_BTC_RESIDUAL")))
                                        Interlocked.Increment(ref _hourlyTelemetry.SkipBtcResidual);
                                    else if (signal.AnalysisReasons!.Any(r => r.Contains("SKIP_BTC_BEAR_LONG")))
                                        Interlocked.Increment(ref _hourlyTelemetry.SkipBtcBearLong);
                                    else
                                        Interlocked.Increment(ref _hourlyTelemetry.SkipBtc4hOppose);
                                }
                                else if (hasBtcRange)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipBtcRange);
                                }
                                else if (hasConfluence)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipConfluence);
                                }
                                else if (hasVolume)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipVolume);
                                }
                                else if (hasSL)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipSL);
                                }
                                else if (hasRR)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipRR);
                                }
                                else if (hasChase)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipChase);
                                }
                                else
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipGozleme);
                                }

                                var skipReason = (signal.AnalysisReasons != null && signal.AnalysisReasons.Count > 0)
                                    ? signal.AnalysisReasons[0]
                                    : $"conf={signal.ConfluenceScore:F1}%";
                                _overfilterDiag[$"{sym}_{tf}"] = ((double)signal.ConfluenceScore, skipReason);
                            }

                            var aztNow = CryptoSense.Domain.Common.TimeHelper.NowFormatted;
                            var indRes = signal.Indicators as CryptoSense.Application.DTOs.IndicatorResult;
                            decimal adxVal = indRes?.Adx ?? 0m;
                            string resultStatus = isTradeQualified ? "PASS" : "BLOCK";
                            string reasonDesc = isTradeQualified
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
                                    var ticker = await innerMarketData.Get24hTickerAsync(signal.Symbol);
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

                            // HIGH-CONVICTION TRADE DISPATCH
                            if (isTradeQualified)
                            {
                                var candleDuration = signal.Timeframe switch
                                {
                                    "4h" => TimeSpan.FromHours(4),
                                    _ => TimeSpan.FromHours(1)
                                };
                                var candleCloseUtc = signal.SourceCandleOpenTimeUtc + candleDuration;

                                var maxAllowedLagMs = SignalEngine.GetMaxLiveDelayMs(signal.Timeframe);
                                var emitLagMs = (DateTime.UtcNow - candleCloseUtc).TotalMilliseconds;
                                if (emitLagMs > maxAllowedLagMs)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipLag);
                                    SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                    Console.WriteLine($"[MarketScanner] SKIP_CYCLE_LAG: {signal.Symbol} lag={emitLagMs:F0}ms > {maxAllowedLagMs}ms");
                                    Console.WriteLine($"[EMIT_RETRY_ARMED] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} reason=CYCLE_LAG");
                                    continue;
                                }

                                var snap = _livePriceCache.GetSnapshot(signal.Symbol);
                                if (snap == null || snap.DataAgeMs > 1000)
                                {
                                    try
                                    {
                                        var lastAgg = await innerMarketData.GetLastAggTradeAsync(signal.Symbol);
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

                                if (snap == null || snap.DataAgeMs > BotConstants.Thresholds.MaxDataAgeMs)
                                {
                                    Interlocked.Increment(ref _hourlyTelemetry.SkipStale);
                                    SignalEngine.InvalidateCandleCache(signal.Symbol, signal.Timeframe, signal.SourceCandleOpenTimeUtc);
                                    Console.WriteLine($"[MarketScanner] SKIP_STALE: {signal.Symbol} dataAgeMs={(snap?.DataAgeMs ?? -1)} source={snap?.Source}");
                                    Console.WriteLine($"[EMIT_RETRY_ARMED] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} reason=STALE");
                                    continue;
                                }

                                await TryDispatchQualifiedAsync(signal, snap, uow, ct);
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

            // HƏR 1h BAĞLANIŞINDA KONSOL (JSON Telemetriya)
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
                    skipVolume = _hourlyTelemetry.SkipVolume,
                    skipBtcResidual = _hourlyTelemetry.SkipBtcResidual,
                    skipBtcBearLong = _hourlyTelemetry.SkipBtcBearLong,
                    skipBtcRange = _hourlyTelemetry.SkipBtcRange,
                    skipBtcGate = _hourlyTelemetry.SkipBtcGate,
                    skipHourCap = _hourlyTelemetry.SkipHourCap,
                    telegramFail = _hourlyTelemetry.TelegramFail,
                    skipCircuitBreaker = _hourlyTelemetry.SkipCircuitBreaker,
                    skipMaxOpen = _hourlyTelemetry.SkipMaxOpen,
                    skipDailyLoss = _hourlyTelemetry.SkipDailyLoss,
                    maxConfluenceSeen = _hourlyTelemetry.MaxConfluenceSeen,
                    dataAgeMsBtc = btcSnapForLog?.DataAgeMs ?? -1,
                    btcSource = btcSnapForLog?.Source ?? "no_snap",
                    cbActive = DateTime.UtcNow < _circuitBreakerUntil
                });
                Console.WriteLine(jsonTelemetry);

                int totalSkips = _hourlyTelemetry.SkipConfluence + _hourlyTelemetry.SkipGozleme +
                                 _hourlyTelemetry.SkipBtcBearLong + _hourlyTelemetry.SkipBtcRange +
                                 _hourlyTelemetry.SkipBtc4hOppose + _hourlyTelemetry.SkipBtcResidual +
                                 _hourlyTelemetry.SkipVolume + _hourlyTelemetry.SkipChase +
                                 _hourlyTelemetry.SkipSL + _hourlyTelemetry.SkipRR +
                                 _hourlyTelemetry.SkipLag + _hourlyTelemetry.SkipStale + _hourlyTelemetry.SkipHourCap +
                                 _hourlyTelemetry.SkipCircuitBreaker + _hourlyTelemetry.SkipMaxOpen + _hourlyTelemetry.SkipDailyLoss;
                if (_hourlyTelemetry.Sent == 0 && _hourlyTelemetry.CoinsScanned > 0 && totalSkips > 0)
                {
                    var skipCounts = new[]
                    {
                        ("CircuitBreaker", _hourlyTelemetry.SkipCircuitBreaker),
                        ("DailyLoss", _hourlyTelemetry.SkipDailyLoss),
                        ("MaxOpen", _hourlyTelemetry.SkipMaxOpen),
                        ("BtcGate", _hourlyTelemetry.SkipBtcGate),
                        ("BtcRange", _hourlyTelemetry.SkipBtcRange),
                        ("BtcResidual", _hourlyTelemetry.SkipBtcResidual),
                        ("Gozleme", _hourlyTelemetry.SkipGozleme),
                        ("Confluence", _hourlyTelemetry.SkipConfluence),
                        ("Volume", _hourlyTelemetry.SkipVolume),
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

            // HEARTBEAT: Saat başı, 1 ədəd.
            await MaybeSendHourlyHeartbeatAsync(stoppingToken);

            // Gündəlik hesabat (Günün sonu - Bakı vaxtı ilə 00:00 - 00:30 pəncərəsi)
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

            LastScanUtc = DateTime.UtcNow;
        }
    }
}
