using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Common;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Interfaces;
using CryptoSense.Infrastructure.Telegram;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoSense.Worker
{
    public partial class BackgroundMarketScanner
    {
        private async Task WaitUntilWsHealthyAsync(CancellationToken stoppingToken)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"[BackgroundMarketScanner] WebSocket sağlamlığı gözlənilir (BTCUSDT DataAge <= {BotConstants.Thresholds.MaxDataAgeMs}ms)...");

            while (sw.ElapsedMilliseconds < 45000 && !stoppingToken.IsCancellationRequested)
            {
                var snap = _livePriceCache.GetSnapshot("BTCUSDT");
                if (snap != null && snap.DataAgeMs <= BotConstants.Thresholds.MaxDataAgeMs)
                {
                    Console.WriteLine($"[WS_READY] dataAgeMs={snap.DataAgeMs} elapsed={sw.ElapsedMilliseconds}ms source={snap.Source}");
                    return;
                }
                await Task.Delay(1000, stoppingToken);
            }

            // Timeout olarsa: REST ilə Default40 + BTC koinlərinin qiymətini çəkib LivePriceCache doldur
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
                var coinsToSeed = new HashSet<string>(TelegramBotService.Default40Coins, StringComparer.OrdinalIgnoreCase);
                coinsToSeed.Add("BTCUSDT");

                var seedTasks = coinsToSeed.Select(async coin =>
                {
                    try
                    {
                        var lastAgg = await marketData.GetLastAggTradeAsync(coin);
                        if (lastAgg.HasValue)
                        {
                            _livePriceCache.UpdateFromAggTrade(coin, lastAgg.Value.Price, lastAgg.Value.ExchangeTsMs, isRestFallback: false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WS_TIMEOUT_REST_SEED] Failed {coin}: {ex.Message}");
                    }
                });
                await Task.WhenAll(seedTasks);

                var snap = _livePriceCache.GetSnapshot("BTCUSDT");
                Console.WriteLine($"[WS_READY] REST fallback seeded {coinsToSeed.Count} coins. BTC dataAgeMs={snap?.DataAgeMs ?? -1}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WS_READY] Failed to seed coins via REST: {ex.Message}");
            }
        }

        private async Task PerformStartupMarketCatchUpAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[BackgroundMarketScanner] Startup Market Catch-Up skanı başladı...");
            var emittedSignals = new List<FuturesSignal>();
            var skippedPassSignals = new List<(string Symbol, string Timeframe, string Direction, decimal EntryPrice, string Reason)>();
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
                                signal = await engine.AnalyzeCoinAsync(sym, tf, isLiveScan: true, ct: stoppingToken);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[StartupMarketCatchUp] AnalyzeCoinAsync error for {sym} {tf}: {ex.Message}");
                                continue;
                            }

                            if (signal == null) continue;

                            bool isTradeQualified = signal.Timeframe != "15m" && 
                                                    signal.Confidence >= BotConstants.Thresholds.MinConfluence1h4h &&
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

                            // Trade qualified PASS! Fetch live price snapshot and dispatch via unified method
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

                            var (dispatched, skipReason) = await TryDispatchQualifiedAsync(signal, snap!, uow, stoppingToken);
                            if (dispatched)
                            {
                                emittedSignals.Add(signal);
                                Console.WriteLine($"[BOOT_CATCHUP_EMIT] {signal.Symbol} {signal.Timeframe} candle={signal.SourceCandleOpenTimeUtc:yyyy-MM-dd HH:mm} Entry={signal.EntryPrice}");
                            }
                            else
                            {
                                string reason = skipReason ?? "Other";
                                skipCounts.AddOrUpdate(reason, 1, (_, v) => v + 1);
                                skippedPassSignals.Add((signal.Symbol, signal.Timeframe, signal.Direction.ToString(), signal.EntryPrice, reason));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StartupMarketCatchUp] Catch-up error: {ex.Message}");
            }

            // Compose and send Boot Briefing to SuperAdmin
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

                    // Fetch last 12 closed BTC 1h candles
                    List<Kline>? recentBtc1hCandles = null;
                    try
                    {
                        var btc1hKlines = await marketData.GetKlinesAsync("BTCUSDT", "1h", 15);
                        if (btc1hKlines != null && btc1hKlines.Count > 1)
                        {
                            recentBtc1hCandles = btc1hKlines.Take(btc1hKlines.Count - 1).TakeLast(12).ToList();
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
                        nextCheckMinutes: nextCheckMinutes,
                        recentBtc1hCandles: recentBtc1hCandles,
                        skippedPassSignals: skippedPassSignals
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
    }
}
