using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;

namespace CryptoSense.Application.Services
{
    public class SignalEngine : ISignalEngine
    {
        private readonly IMarketDataProvider _marketData;
        private readonly IIndicatorEngine _indicatorEngine;
        private readonly INewsService _newsService;
        private readonly IUnitOfWork _unitOfWork;

        private static BtcMarketCompass? _cachedBtcCompass;
        private static DateTime _btcCompassCacheTime = DateTime.MinValue;
        private static readonly SemaphoreSlim _btcCompassLock = new(1, 1);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FuturesSignal> _recentCandleSignals = new();

        public SignalEngine(
            IMarketDataProvider marketData,
            IIndicatorEngine indicatorEngine,
            INewsService newsService,
            IUnitOfWork unitOfWork)
        {
            _marketData = marketData;
            _indicatorEngine = indicatorEngine;
            _newsService = newsService;
            _unitOfWork = unitOfWork;
        }

        public static decimal RoundToCoinPrecision(decimal basePrice, decimal value)
        {
            if (basePrice >= 100m) return Math.Round(value, 2);
            if (basePrice >= 1m) return Math.Round(value, 4);
            if (basePrice >= 0.01m) return Math.Round(value, 5);
            return Math.Round(value, 8);
        }

        public record SrTargetResult(
            bool Success,
            string? SkipReason,
            decimal TakeProfit1,
            decimal TakeProfit2,
            decimal TakeProfit3,
            decimal StopLoss,
            decimal InitialRiskR,
            decimal AtrPercent,
            decimal SignalSwingLow,
            decimal SignalSwingHigh,
            List<decimal> Clusters
        );

        public static decimal CalculatePearsonCorrelation(List<decimal> series1, List<decimal> series2)
        {
            int n = Math.Min(series1.Count, series2.Count);
            if (n < 5) return 0m;

            var s1 = series1.TakeLast(n).ToList();
            var s2 = series2.TakeLast(n).ToList();

            decimal avg1 = s1.Average();
            decimal avg2 = s2.Average();

            decimal sum1 = 0m, sum2 = 0m, sumProduct = 0m;
            for (int i = 0; i < n; i++)
            {
                decimal diff1 = s1[i] - avg1;
                decimal diff2 = s2[i] - avg2;
                sumProduct += diff1 * diff2;
                sum1 += diff1 * diff1;
                sum2 += diff2 * diff2;
            }

            decimal denom = (decimal)Math.Sqrt((double)(sum1 * sum2));
            if (denom == 0m) return 0m;
            return sumProduct / denom;
        }

        public static SrTargetResult CalculateSrTargetsAndStops(
            List<Kline> closedKlines,
            SignalDirection direction,
            decimal calculationRefPrice,
            decimal atr14,
            decimal vwap)
        {
            var fail = new SrTargetResult(false, null, 0, 0, 0, 0, 0, 0, 0, 0, new List<decimal>());
            if (closedKlines == null || closedKlines.Count < 10 || calculationRefPrice <= 0)
            {
                return fail with { SkipReason = "SKIP_INSUFFICIENT_DATA" };
            }

            // 1. 50 closed 15m candles
            var k50 = closedKlines.TakeLast(Math.Min(50, closedKlines.Count)).ToList();

            // 2. Swing fractal ±2
            var swingHighs = new List<decimal>();
            var swingLows = new List<decimal>();
            for (int i = 2; i < k50.Count - 2; i++)
            {
                if (k50[i].High > k50[i - 1].High && k50[i].High > k50[i - 2].High &&
                    k50[i].High > k50[i + 1].High && k50[i].High > k50[i + 2].High)
                {
                    swingHighs.Add(k50[i].High);
                }

                if (k50[i].Low < k50[i - 1].Low && k50[i].Low < k50[i - 2].Low &&
                    k50[i].Low < k50[i + 1].Low && k50[i].Low < k50[i + 2].Low)
                {
                    swingLows.Add(k50[i].Low);
                }
            }

            decimal signalSwingLow = swingLows.LastOrDefault(l => l < calculationRefPrice);
            if (signalSwingLow == 0) signalSwingLow = k50.Min(k => k.Low);

            decimal signalSwingHigh = swingHighs.LastOrDefault(h => h > calculationRefPrice);
            if (signalSwingHigh == 0) signalSwingHigh = k50.Max(k => k.High);

            // 3. Previous day high/low
            var yesterdayUtc = DateTime.UtcNow.Date.AddDays(-1);
            var prevDayKlines = closedKlines.Where(k => k.Time.Date == yesterdayUtc).ToList();
            decimal prevDayHigh = prevDayKlines.Count > 0 ? prevDayKlines.Max(k => k.High) : 0m;
            decimal prevDayLow = prevDayKlines.Count > 0 ? prevDayKlines.Min(k => k.Low) : 0m;

            // 4. UTC VWAP
            decimal utcVwap = vwap;

            // 5. Gather raw levels: swing fractal ±2, prev day high/low, UTC VWAP. Başqa səviyyə yox.
            var rawLevels = new List<decimal>();
            rawLevels.AddRange(swingHighs);
            rawLevels.AddRange(swingLows);
            if (prevDayHigh > 0) rawLevels.Add(prevDayHigh);
            if (prevDayLow > 0) rawLevels.Add(prevDayLow);
            if (utcVwap > 0) rawLevels.Add(utcVwap);

            // 6. Cluster 0.15%
            var clusters = new List<decimal>();
            if (rawLevels.Count > 0)
            {
                var sorted = rawLevels.OrderBy(x => x).ToList();
                var currentCluster = new List<decimal> { sorted[0] };

                for (int i = 1; i < sorted.Count; i++)
                {
                    decimal clusterAvg = currentCluster.Average();
                    if (clusterAvg > 0 && Math.Abs(sorted[i] - clusterAvg) / clusterAvg <= 0.0015m)
                    {
                        currentCluster.Add(sorted[i]);
                    }
                    else
                    {
                        clusters.Add(currentCluster.Average());
                        currentCluster = new List<decimal> { sorted[i] };
                    }
                }
                if (currentCluster.Count > 0)
                {
                    clusters.Add(currentCluster.Average());
                }
            }

            // 7. Offset = clamp(0.15*ATR%, 0.05%, 0.20%)
            decimal atrPct = (atr14 > 0 && calculationRefPrice > 0) ? (atr14 / calculationRefPrice) * 100m : 1.0m;
            decimal offsetPct = Math.Clamp(0.15m * atrPct, 0.05m, 0.20m);
            decimal offsetDist = calculationRefPrice * (offsetPct / 100m);

            fail = new SrTargetResult(false, null, 0, 0, 0, 0, 0, atrPct, signalSwingLow, signalSwingHigh, clusters);

            if (direction == SignalDirection.Buy) // LONG
            {
                // LONG TP1 = nearest resistance - offset
                var resistances = clusters.Where(c => c > calculationRefPrice).OrderBy(c => c).ToList();
                if (resistances.Count == 0)
                {
                    return fail with { SkipReason = "SKIP_TP_FAR (Müqavimət klasteri tapılmadı)" };
                }

                decimal nearestResistance = resistances[0];
                decimal srDistPct = ((nearestResistance - calculationRefPrice) / calculationRefPrice) * 100m;
                if (srDistPct > 2.5m)
                {
                    return fail with { SkipReason = $"SKIP_TP_FAR (S/R məsafəsi {srDistPct:F2}% > 2.5% tavan)" };
                }

                decimal rawTp1 = nearestResistance - offsetDist;
                decimal distPct = ((rawTp1 - calculationRefPrice) / calculationRefPrice) * 100m;

                // TP1 dist = clamp(dist, 0.6*ATR%, 1.8*ATR%) sonra <= 2.5%
                distPct = Math.Clamp(distPct, 0.6m * atrPct, 1.8m * atrPct);
                if (distPct > 2.5m)
                {
                    return fail with { SkipReason = $"SKIP_TP_FAR (TP1 dist {distPct:F2}% > 2.5% tavan)" };
                }
                decimal tp1 = calculationRefPrice * (1m + distPct / 100m);

                // SL = invalidation swing - offset, <= 1.8%, böyükdürsə SKIP_SL_FAR
                decimal rawSl = signalSwingLow - offsetDist;
                decimal slDistPct = ((calculationRefPrice - rawSl) / calculationRefPrice) * 100m;
                if (slDistPct > 1.8m)
                {
                    return fail with { SkipReason = $"SKIP_SL_FAR (SL məsafəsi {slDistPct:F2}% > 1.8% tavan)" };
                }
                if (slDistPct <= 0m)
                {
                    slDistPct = Math.Max(0.5m, offsetPct);
                    rawSl = calculationRefPrice * (1m - slDistPct / 100m);
                }
                decimal sl = rawSl;
                decimal riskR = calculationRefPrice - sl;

                // S/R < 0.45% və TP1 < 0.8R -> SKIP_RR; R yalnız filter: TP1 >= 0.8R
                decimal rrRatio = slDistPct > 0 ? (distPct / slDistPct) : 0m;
                if (srDistPct < 0.45m && rrRatio < 0.8m)
                {
                    return fail with { SkipReason = $"SKIP_RR (S/R < 0.45% və R:R {rrRatio:F2} < 0.8R)" };
                }
                if (rrRatio < 0.8m)
                {
                    return fail with { SkipReason = $"SKIP_RR (R:R {rrRatio:F2} < 0.8R)" };
                }

                // TP2 / TP3 = növbəti klaster. Yoxdursa TP2 = TP1 + 1*ATR% (tavan içində) və ya TP2 olmasın. 4.5-6.7% qadağan.
                var nextClusters = resistances.Where(c => c > tp1).OrderBy(c => c).ToList();
                decimal tp2 = 0m;
                if (nextClusters.Count > 0 && ((nextClusters[0] - calculationRefPrice) / calculationRefPrice * 100m) <= 2.5m)
                {
                    tp2 = nextClusters[0] - offsetDist;
                }
                else
                {
                    decimal candidateTp2Dist = distPct + (1.0m * atrPct);
                    if (candidateTp2Dist <= 2.5m)
                    {
                        tp2 = calculationRefPrice * (1m + candidateTp2Dist / 100m);
                    }
                }

                decimal tp3 = 0m;
                if (nextClusters.Count > 1 && ((nextClusters[1] - calculationRefPrice) / calculationRefPrice * 100m) <= 2.5m)
                {
                    tp3 = nextClusters[1] - offsetDist;
                }

                return new SrTargetResult(
                    Success: true,
                    SkipReason: null,
                    TakeProfit1: tp1,
                    TakeProfit2: tp2,
                    TakeProfit3: tp3,
                    StopLoss: sl,
                    InitialRiskR: riskR,
                    AtrPercent: atrPct,
                    SignalSwingLow: signalSwingLow,
                    SignalSwingHigh: signalSwingHigh,
                    Clusters: clusters
                );
            }
            else // SHORT
            {
                // SHORT TP1 = nearest support + offset
                var supports = clusters.Where(c => c < calculationRefPrice).OrderByDescending(c => c).ToList();
                if (supports.Count == 0)
                {
                    return fail with { SkipReason = "SKIP_TP_FAR (Dəstək klasteri tapılmadı)" };
                }

                decimal nearestSupport = supports[0];
                decimal srDistPct = ((calculationRefPrice - nearestSupport) / calculationRefPrice) * 100m;
                if (srDistPct > 2.5m)
                {
                    return fail with { SkipReason = $"SKIP_TP_FAR (S/R məsafəsi {srDistPct:F2}% > 2.5% tavan)" };
                }

                decimal rawTp1 = nearestSupport + offsetDist;
                decimal distPct = ((calculationRefPrice - rawTp1) / calculationRefPrice) * 100m;

                // TP1 dist = clamp(dist, 0.6*ATR%, 1.8*ATR%) sonra <= 2.5%
                distPct = Math.Clamp(distPct, 0.6m * atrPct, 1.8m * atrPct);
                if (distPct > 2.5m)
                {
                    return fail with { SkipReason = $"SKIP_TP_FAR (TP1 dist {distPct:F2}% > 2.5% tavan)" };
                }
                decimal tp1 = calculationRefPrice * (1m - distPct / 100m);

                // SL = invalidation swing + offset, <= 1.8%, böyükdürsə SKIP_SL_FAR
                decimal rawSl = signalSwingHigh + offsetDist;
                decimal slDistPct = ((rawSl - calculationRefPrice) / calculationRefPrice) * 100m;
                if (slDistPct > 1.8m)
                {
                    return fail with { SkipReason = $"SKIP_SL_FAR (SL məsafəsi {slDistPct:F2}% > 1.8% tavan)" };
                }
                if (slDistPct <= 0m)
                {
                    slDistPct = Math.Max(0.5m, offsetPct);
                    rawSl = calculationRefPrice * (1m + slDistPct / 100m);
                }
                decimal sl = rawSl;
                decimal riskR = sl - calculationRefPrice;

                // S/R < 0.45% və TP1 < 0.8R -> SKIP_RR; R yalnız filter: TP1 >= 0.8R
                decimal rrRatio = slDistPct > 0 ? (distPct / slDistPct) : 0m;
                if (srDistPct < 0.45m && rrRatio < 0.8m)
                {
                    return fail with { SkipReason = $"SKIP_RR (S/R < 0.45% və R:R {rrRatio:F2} < 0.8R)" };
                }
                if (rrRatio < 0.8m)
                {
                    return fail with { SkipReason = $"SKIP_RR (R:R {rrRatio:F2} < 0.8R)" };
                }

                // TP2 / TP3 = növbəti klaster. Yoxdursa TP2 = TP1 + 1*ATR% (tavan içində) və ya TP2 olmasın. 4.5-6.7% qadağan.
                var nextClusters = supports.Where(c => c < tp1).OrderByDescending(c => c).ToList();
                decimal tp2 = 0m;
                if (nextClusters.Count > 0 && ((calculationRefPrice - nextClusters[0]) / calculationRefPrice * 100m) <= 2.5m)
                {
                    tp2 = nextClusters[0] + offsetDist;
                }
                else
                {
                    decimal candidateTp2Dist = distPct + (1.0m * atrPct);
                    if (candidateTp2Dist <= 2.5m)
                    {
                        tp2 = calculationRefPrice * (1m - candidateTp2Dist / 100m);
                    }
                }

                decimal tp3 = 0m;
                if (nextClusters.Count > 1 && ((calculationRefPrice - nextClusters[1]) / calculationRefPrice * 100m) <= 2.5m)
                {
                    tp3 = nextClusters[1] + offsetDist;
                }

                return new SrTargetResult(
                    Success: true,
                    SkipReason: null,
                    TakeProfit1: tp1,
                    TakeProfit2: tp2,
                    TakeProfit3: tp3,
                    StopLoss: sl,
                    InitialRiskR: riskR,
                    AtrPercent: atrPct,
                    SignalSwingLow: signalSwingLow,
                    SignalSwingHigh: signalSwingHigh,
                    Clusters: clusters
                );
            }
        }

        public async Task<BtcMarketCompass> GetBtcCompassAsync()
        {
            if (_cachedBtcCompass != null && (DateTime.UtcNow - _btcCompassCacheTime).TotalSeconds < 25)
            {
                return _cachedBtcCompass;
            }

            await _btcCompassLock.WaitAsync();
            try
            {
                if (_cachedBtcCompass != null && (DateTime.UtcNow - _btcCompassCacheTime).TotalSeconds < 25)
                {
                    return _cachedBtcCompass;
                }

                var compass = new BtcMarketCompass
                {
                    TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted
                };

                try
                {
                    var btcTicker = await _marketData.Get24hTickerAsync("BTCUSDT");
                    if (btcTicker != null)
                    {
                        compass.Price = btcTicker.Price;
                        compass.Change24h = btcTicker.PriceChangePercent;
                        compass.High24h = btcTicker.High24h;
                        compass.Low24h = btcTicker.Low24h;
                        compass.VolumeQuote = btcTicker.VolumeQuote;
                    }

                    var macro = await _marketData.GetMacroMarketOverviewAsync();
                    compass.BtcDominance = macro.BtcDominance;
                    compass.UsdtDominance = macro.UsdtDominance;
                    compass.MarketCapChange24h = macro.MarketCapChange24h;
                }
                catch { }

                // BTC 1h SuperTrend + HH/HL = rejim (Problem 8)
                var btc1hKlines = await _marketData.GetKlinesAsync("BTCUSDT", "1h", 60);
                if (btc1hKlines.Count < 20)
                {
                    btc1hKlines = await _marketData.GetKlinesAsync("BTCUSDT", "15m", 60);
                }

                if (btc1hKlines.Count > 0)
                {
                    var closed1h = btc1hKlines.Count >= 2 ? btc1hKlines.Take(btc1hKlines.Count - 1).ToList() : btc1hKlines;
                    var currentPrice = closed1h.Last().Close;
                    if (compass.Price == 0) compass.Price = currentPrice;

                    // SuperTrend(10, 3) on BTC 1h
                    var (superTrendVal, isSuperTrendBullish) = _indicatorEngine.CalculateSuperTrend(closed1h, 10, 3.0m);
                    compass.SuperTrend = superTrendVal;
                    compass.IsSuperTrendBullish = isSuperTrendBullish;

                    // HH/HL on BTC 1h (fractal ±2)
                    var shList = new List<decimal>();
                    var slList = new List<decimal>();
                    for (int i = 2; i < closed1h.Count - 2; i++)
                    {
                        if (closed1h[i].High > closed1h[i - 1].High && closed1h[i].High > closed1h[i - 2].High &&
                            closed1h[i].High > closed1h[i + 1].High && closed1h[i].High > closed1h[i + 2].High)
                            shList.Add(closed1h[i].High);

                        if (closed1h[i].Low < closed1h[i - 1].Low && closed1h[i].Low < closed1h[i - 2].Low &&
                            closed1h[i].Low < closed1h[i + 1].Low && closed1h[i].Low < closed1h[i + 2].Low)
                            slList.Add(closed1h[i].Low);
                    }

                    bool hasHhHl = shList.Count >= 2 && slList.Count >= 2 &&
                                   shList[^1] > shList[^2] && slList[^1] > slList[^2];
                    bool hasLhLl = shList.Count >= 2 && slList.Count >= 2 &&
                                   shList[^1] < shList[^2] && slList[^1] < slList[^2];

                    compass.HasHigherHighsHigherLows = hasHhHl;
                    compass.HasLowerHighsLowerLows = hasLhLl;

                    var ind1h = _indicatorEngine.CalculateIndicators(closed1h);
                    compass.SupportLevel = ind1h.SupportLevel;
                    compass.ResistanceLevel = ind1h.ResistanceLevel;
                    compass.Ema20 = ind1h.Ema20;
                    compass.Ema50 = ind1h.Ema50;
                    compass.Rsi15m = ind1h.Rsi;
                    compass.MacdHist = ind1h.MacdHist;
                    compass.EmaStructure = ind1h.EmaTrend;

                    // Rejim təyini: BTC 1h SuperTrend + HH/HL = rejim (Problem 8)
                    if (isSuperTrendBullish && hasHhHl)
                    {
                        compass.Regime = BtcMarketRegime.Bullish;
                        compass.Trend = "YÜKSƏLİŞ (BULLISH) 🟢";
                        compass.Summary = "Bitcoin 1h SuperTrend və HH/HL strukturu yüksəlişdədir (Bullish rejim).";
                    }
                    else if (!isSuperTrendBullish && hasLhLl)
                    {
                        compass.Regime = BtcMarketRegime.Bearish;
                        compass.Trend = "ENİŞ (BEARISH) 🔴";
                        compass.Summary = "Bitcoin 1h SuperTrend və LH/LL strukturu enişdədir (Bearish rejim).";
                    }
                    else
                    {
                        compass.Regime = BtcMarketRegime.Ranging;
                        compass.Trend = "NEYTRAL (YAN HƏRƏKƏT) ⚪";
                        compass.Summary = "Bitcoin 1h strukturu yan hərəkətdədir / konsolidasiyadadır (Ranging rejim).";
                    }
                }

                if (compass.Price > 0)
                {
                    _cachedBtcCompass = compass;
                    _btcCompassCacheTime = DateTime.UtcNow;
                    return compass;
                }

                return _cachedBtcCompass ?? compass;
            }
            finally
            {
                _btcCompassLock.Release();
            }
        }

        public async Task<FuturesSignal> AnalyzeCoinAsync(string symbol, string timeframe = "15m", bool isLiveScan = false)
        {
            symbol = symbol.ToUpper();
            if (!symbol.EndsWith("USDT")) symbol += "USDT";

            var klines = await _marketData.GetKlinesAsync(symbol, timeframe, 100);
            if (klines.Count < 35)
            {
                return new FuturesSignal { Symbol = symbol, Timeframe = timeframe, SignalType = "MƏLUMAT AZDIR" };
            }

            // Closed candle evaluation to prevent flickering (forming candle excluded from indicator array)
            var closedKlines = klines.Count >= 2 ? klines.Take(klines.Count - 1).ToList() : klines;
            var closedCandle = closedKlines.Last();
            var sourceCandleTime = closedCandle.Time;
            var calculationRefPrice = closedCandle.Close;
            decimal currentPrice = calculationRefPrice;

            // Live Freshness Guard: In live scanning, a signal is ONLY valid if its closed candle just finished!
            // If the candle closed minutes or hours ago, it is historical/stale and must not generate live trades.
            if (isLiveScan)
            {
                var candleCloseTime = DateTimeOffset.FromUnixTimeMilliseconds(closedCandle.CloseTime).UtcDateTime;
                var candleAge = DateTime.UtcNow - candleCloseTime;
                var maxLiveDelay = timeframe switch
                {
                    "1m" => TimeSpan.FromSeconds(90),
                    "3m" => TimeSpan.FromSeconds(90),
                    "5m" => TimeSpan.FromSeconds(90),
                    "15m" => TimeSpan.FromSeconds(90),
                    "1h" => TimeSpan.FromMinutes(15),
                    "4h" => TimeSpan.FromMinutes(30),
                    _ => TimeSpan.FromSeconds(90)
                };

                if (candleAge > maxLiveDelay)
                {
                    return new FuturesSignal
                    {
                        Symbol = symbol,
                        Timeframe = timeframe,
                        Direction = SignalDirection.Buy,
                        SignalType = "NEYTRAL (GÖZLƏMƏ) ⚪",
                        Status = SignalStatus.Open,
                        OutcomeStatus = "GÖZLƏMƏ ⚪",
                        CurrentPrice = calculationRefPrice,
                        EntryPrice = 0,
                        ConfluenceScore = 50,
                        Confidence = 50,
                        SourceCandleOpenTimeUtc = sourceCandleTime,
                        GeneratedAt = DateTime.UtcNow,
                        ExpiryTimeUtc = DateTime.UtcNow.AddMinutes(30),
                        TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted,
                        AnalysisReasons = new List<string> { $"Son bağlanan şam {candleAge.TotalMinutes:F0} dəqiqə əvvəl bitib. Yeni canlı şamın bağlanması gözlənilir." }
                    };
                }
            }

            var candleKey = $"{symbol}_{timeframe}_{sourceCandleTime:yyyyMMddHHmmss}";

            // Check if existing signal for this candle already created (Physical Dedup)
            var existingSignal = await _unitOfWork.Signals.GetExistingCandleSignalAsync(symbol, timeframe, sourceCandleTime);
            if (existingSignal != null)
            {
                existingSignal.CurrentPrice = calculationRefPrice;
                _recentCandleSignals.TryAdd(candleKey, existingSignal);
                return existingSignal;
            }

            if (_recentCandleSignals.TryGetValue(candleKey, out var cachedSig))
            {
                cachedSig.CurrentPrice = calculationRefPrice;
                return cachedSig;
            }

            var btcCompass = await GetBtcCompassAsync();
            var macroOverview = await _marketData.GetMacroMarketOverviewAsync();
            var indicators = _indicatorEngine.CalculateIndicators(closedKlines, btcCompass);
            var newsSummary = await _newsService.GetNewsAndSentimentAsync();

            var reasons = new List<string>();

            // 1. Core Technical Indicators (EMA, MA, MACD, SuperTrend)
            reasons.Add($"EMA (20/50): ${indicators.Ema20} / ${indicators.Ema50} ({indicators.EmaTrend})");
            reasons.Add($"MA / SMA (20/50): ${indicators.Sma20} / ${indicators.Sma50} ({indicators.SmaTrend})");
            reasons.Add($"MACD (12,26,9): Hist={indicators.MacdHist:F4} ({indicators.MacdStatus})");            // 2. Macro Dominance & BTC Compass (Problem 8)
            reasons.Add($"Bitcoin Kompası: {btcCompass.Trend} ({btcCompass.Regime})");
            reasons.Add($"Dominasiya: BTC.D {macroOverview.BtcDominance}% | USDT.D {macroOverview.UsdtDominance}%");

            // 3. Institutional Retest & Pullback Decision Logic
            SignalDirection direction = SignalDirection.Buy;
            string determinedType = "NEYTRAL (GÖZLƏMƏ) ⚪";
            int confidence = (int)Math.Clamp(Math.Round(indicators.ConfluenceScore), 40, 95);

            // A. Trend Filter
            bool isUptrend = indicators.Ema20 > indicators.Ema50 && currentPrice >= indicators.Ema50;
            bool isDowntrend = indicators.Ema20 < indicators.Ema50 && currentPrice <= indicators.Ema50;

            // B. Pullback to Value Zone (EMA20 or Support)
            decimal distToEma20Pct = Math.Abs(currentPrice - indicators.Ema20) / (currentPrice > 0 ? currentPrice : 1);
            bool isPullbackZone = distToEma20Pct <= 0.0085m;

            // C. Rejection Wick Analysis (Buyer / Seller Rejection)
            decimal candleRange = closedCandle.High - closedCandle.Low;
            decimal lowerWick = Math.Min(closedCandle.Open, closedCandle.Close) - closedCandle.Low;
            decimal upperWick = closedCandle.High - Math.Max(closedCandle.Open, closedCandle.Close);

            bool buyerRejection = candleRange > 0 && (lowerWick / candleRange) >= 0.35m;
            bool sellerRejection = candleRange > 0 && (upperWick / candleRange) >= 0.35m;

            // D. Momentum & Breakout
            bool breakoutLong = indicators.ResistanceLevel > 0 && currentPrice >= indicators.ResistanceLevel;
            bool breakoutShort = indicators.SupportLevel > 0 && currentPrice <= indicators.SupportLevel;

            // E. RSI Safe Zone Filter
            bool rsiAllowsLong = indicators.Rsi >= 38 && indicators.Rsi <= 68;
            bool rsiAllowsShort = indicators.Rsi >= 32 && indicators.Rsi <= 62;

            // F. Macro & Bitcoin Alignment (Problem 8 - BTC 1h SuperTrend + HH/HL = rejim)
            bool isAltcoin = symbol != "BTCUSDT";
            decimal altBtcCorr = 0m;
            decimal altRs = 0m;

            var altSwingHighs = new List<decimal>();
            var altSwingLows = new List<decimal>();
            int evalBars = Math.Min(closedKlines.Count, 50);
            var evalSubset = closedKlines.TakeLast(evalBars).ToList();
            for (int i = 2; i < evalSubset.Count - 2; i++)
            {
                if (evalSubset[i].High > evalSubset[i - 1].High && evalSubset[i].High > evalSubset[i - 2].High &&
                    evalSubset[i].High > evalSubset[i + 1].High && evalSubset[i].High > evalSubset[i + 2].High)
                    altSwingHighs.Add(evalSubset[i].High);

                if (evalSubset[i].Low < evalSubset[i - 1].Low && evalSubset[i].Low < evalSubset[i - 2].Low &&
                    evalSubset[i].Low < evalSubset[i + 1].Low && evalSubset[i].Low < evalSubset[i + 2].Low)
                    altSwingLows.Add(evalSubset[i].Low);
            }

            // Lower High + Close below swing low (Breakdown)
            bool hasLowerHigh = altSwingHighs.Count >= 2 && altSwingHighs[^1] < altSwingHighs[^2];
            decimal recentAltSwingLow = altSwingLows.Count > 0 ? altSwingLows[^1] : 0m;
            bool isStructuralBreakdown = hasLowerHigh && (recentAltSwingLow > 0 && closedCandle.Close < recentAltSwingLow);

            // Higher Low + Close above swing high (Breakout)
            bool hasHigherLow = altSwingLows.Count >= 2 && altSwingLows[^1] > altSwingLows[^2];
            decimal recentAltSwingHigh = altSwingHighs.Count > 0 ? altSwingHighs[^1] : 0m;
            bool isStructuralBreakout = hasHigherLow && (recentAltSwingHigh > 0 && closedCandle.Close > recentAltSwingHigh);

            if (isAltcoin && closedKlines.Count >= 10)
            {
                try
                {
                    var btcKlines15m = await _marketData.GetKlinesAsync("BTCUSDT", "15m", closedKlines.Count);
                    var closedBtc15m = btcKlines15m.Count >= 2 ? btcKlines15m.Take(btcKlines15m.Count - 1).ToList() : btcKlines15m;
                    if (closedBtc15m.Count >= 10)
                    {
                        var altCloses = closedKlines.Select(k => k.Close).ToList();
                        var btcCloses = closedBtc15m.Select(k => k.Close).ToList();
                        altBtcCorr = CalculatePearsonCorrelation(altCloses, btcCloses);

                        var lastAlt = closedKlines.Last();
                        var lastBtc = closedBtc15m.Last();
                        decimal altChg = lastAlt.Open > 0 ? (lastAlt.Close - lastAlt.Open) / lastAlt.Open : 0m;
                        decimal btcChg = lastBtc.Open > 0 ? (lastBtc.Close - lastBtc.Open) / lastBtc.Open : 0m;
                        altRs = altChg - btcChg;
                    }
                }
                catch { }
            }

            bool btcConfirmsLong = true;
            bool btcConfirmsShort = true;
            if (isAltcoin)
            {
                bool isHighCorr = altBtcCorr > 0.7m;
                bool altRsPositive = altRs > 0m;

                // BTC 1h bullish VƏ alt-BTC 15m corr>0.7 VƏ alt RS müsbət → alt SHORT yalnız struktur breakdown (lower high + close below swing). Mean-reversion SHORT yox.
                if (btcCompass.Regime == BtcMarketRegime.Bullish && isHighCorr && altRsPositive)
                {
                    btcConfirmsShort = isStructuralBreakdown;
                    if (!btcConfirmsShort)
                    {
                        reasons.Add("BTC 1h Bullish, corr>0.7 və alt RS müsbət: Altcoin SHORT yalnız struktur breakdown olduqda açıla bilər (Mean-reversion SHORT bloklandı)");
                    }
                }

                // BTC 1h bearish VƏ alt-BTC 15m corr>0.7 VƏ alt RS mənfi → alt LONG yalnız struktur breakout.
                if (btcCompass.Regime == BtcMarketRegime.Bearish && isHighCorr && !altRsPositive)
                {
                    btcConfirmsLong = isStructuralBreakout;
                    if (!btcConfirmsLong)
                    {
                        reasons.Add("BTC 1h Bearish, corr>0.7 və alt RS mənfi: Altcoin LONG yalnız struktur breakout olduqda açıla bilər (Mean-reversion LONG bloklandı)");
                    }
                }
            }

            // Market Regime & Chop Filter (Minimum ADX required for ANY timeframe to avoid dying in sideways chop)
            decimal minAdxRequired = timeframe switch
            {
                "1m" => 25m,
                "3m" => 20m,
                "5m" => 18m,
                _ => 16m
            };
            bool hasValidMarketRegime = indicators.Adx >= minAdxRequired;

            // Candle Action Confirmations (Never enter against impulsive counter-trend bars)
            bool bullishCandleConfirmation = closedCandle.Close >= closedCandle.Open || buyerRejection;
            bool bearishCandleConfirmation = closedCandle.Close <= closedCandle.Open || sellerRejection;
            bool volumeConfirmed = (timeframe == "1m" || timeframe == "3m")
                ? indicators.VolumeSurgeRatio >= 1.80m
                : (timeframe == "5m" ? indicators.VolumeSurgeRatio >= 1.20m : true);

            // Extreme Volatility & High-Risk Anomaly Check
            bool isExtremeVolatility = (indicators.Atr > 0 && currentPrice > 0 && (indicators.Atr / currentPrice) >= 0.055m) ||
                                       (closedCandle.High > 0 && closedCandle.Low > 0 && ((closedCandle.High - closedCandle.Low) / closedCandle.Low) >= 0.075m) ||
                                       (indicators.VolumeSurgeRatio >= 4.0m);

            // Confluence tələbi: Confluence < 78% siqnal YASAQ! (15m, 1h, 4h min 78%, 5m 80%, 1m/3m 90%)
            decimal minLongScore = (timeframe == "1m" || timeframe == "3m") ? 90m : (timeframe == "5m" ? 80m : 78m);
            decimal maxShortScore = (timeframe == "1m" || timeframe == "3m") ? 10m : (timeframe == "5m" ? 20m : 22m);

            if (isExtremeVolatility)
            {
                direction = SignalDirection.Buy;
                determinedType = "YÜKSƏK_VOLATİLLİK_RİSK";
                confidence = 50;
                reasons.Add("Anomal kəskin volatillik aşkarlandı. Təhlükəsizlik səbəbilə avtomatik əməliyyat açılmır, risk bildirişi göndərilir.");
            }
            // =========================================================================
            // 🟢 INSTITUTIONAL HIGH-CONVICTION LONG
            // =========================================================================
            else if (indicators.ConfluenceScore >= minLongScore &&
                indicators.SuperTrendVote == IndicatorVote.Bullish &&
                isUptrend &&
                bullishCandleConfirmation &&
                hasValidMarketRegime &&
                volumeConfirmed &&
                rsiAllowsLong &&
                btcConfirmsLong)
            {
                direction = SignalDirection.Buy;
                determinedType = breakoutLong ? "GÜCLÜ BREAKOUT LONG 🟢" : (isPullbackZone ? "GÜCLÜ RETEST LONG 🟢" : "GÜCLÜ TREND LONG 🟢");
                confidence = (int)Math.Clamp(Math.Round(indicators.ConfluenceScore), 50, 96);
            }
            // =========================================================================
            // 🔴 INSTITUTIONAL HIGH-CONVICTION SHORT
            // =========================================================================
            else if (indicators.ConfluenceScore <= maxShortScore &&
                     indicators.SuperTrendVote == IndicatorVote.Bearish &&
                     isDowntrend &&
                     bearishCandleConfirmation &&
                     hasValidMarketRegime &&
                     volumeConfirmed &&
                     rsiAllowsShort &&
                     btcConfirmsShort)
            {
                direction = SignalDirection.Sell;
                determinedType = breakoutShort ? "GÜCLÜ BREAKDOWN SHORT 🔴" : (isPullbackZone ? "GÜCLÜ RETEST SHORT 🔴" : "GÜCLÜ TREND SHORT 🔴");
                confidence = (int)Math.Clamp(Math.Round(100m - indicators.ConfluenceScore), 50, 96);
            }
            else
            {
                direction = SignalDirection.Buy;
                determinedType = "GÖZLƏMƏ ⚪";
                confidence = 50;
                if (!hasValidMarketRegime) reasons.Add($"Rejim Filtri: ADX ({indicators.Adx:F1}) < {minAdxRequired:F1} (Bazar zəif/yan konsolidasiyadadır)");
                if (indicators.SuperTrendVote != IndicatorVote.Bullish && isUptrend) reasons.Add("SuperTrend təsdiqi yoxdur (Trend ziddiyyətlidir)");
                if (isAltcoin && !btcConfirmsShort && direction == SignalDirection.Sell) reasons.Add("BTC rejim struktur uyğunsuzluğu səbəbilə altcoin SHORT-u bloklandı");
            }

            decimal directionalConfluence = direction == SignalDirection.Sell 
                ? Math.Round(100m - indicators.ConfluenceScore, 1) 
                : Math.Round(indicators.ConfluenceScore, 1);

            // Confluence < 78% siqnal YASAQ (#3 XRP 76.7% kimi hallar üçün qəti qapı)
            if (directionalConfluence < 78.0m)
            {
                determinedType = "GÖZLƏMƏ ⚪";
                confidence = 50;
                reasons.Add($"Confluence Filtri: {directionalConfluence:F1}% < 78.0% (Siqnal üçün minimal 78% tələbi ödənmir)");
            }

            // Dinamik Həqiqi Volatillik və Riskin Təyini (15m-də SL eni max ~0.8–1.2% ATR cap)
            decimal minTfMultiplier = timeframe switch
            {
                "1m" => 0.012m,
                "3m" => 0.015m,
                "5m" => 0.018m,
                "15m" => 0.008m, // 15m minimal risk 0.8%
                "1h" => 0.025m,
                "4h" => 0.040m,
                _ => 0.008m
            };

            decimal atr = indicators.Atr > 0 ? indicators.Atr : (calculationRefPrice * minTfMultiplier);
            decimal minRisk = calculationRefPrice * minTfMultiplier;
            decimal dynamicAtrRisk = atr * 1.2m;
            decimal calculatedRisk = Math.Max(dynamicAtrRisk, minRisk);

            // 15m SL cap: SL eni max ~0.8–1.2%
            if (timeframe == "15m")
            {
                decimal maxSlRisk15m = calculationRefPrice * 0.012m; // Max 1.2%
                if (calculatedRisk > maxSlRisk15m) calculatedRisk = maxSlRisk15m;
            }

            // 15m expiry: TP1 vurulmayıbsa max 90 dəq (6 şam), 180 dəq QADAĞA!
            int durationMinutes = timeframe switch
            {
                "1m" => 30,
                "3m" => 60,
                "5m" => 75,
                "15m" => 90,  // Max 90 dəq (6 şam). 180 dəqiqə QƏTİ QADAĞANDIR!
                "1h" => 480,  // 8 saat
                "4h" => 1440, // 24 saat
                _ => 90
            };

            bool isTradeSignal = determinedType.Contains("LONG") || determinedType.Contains("SHORT");
            int sigNumber = 0; // Number is assigned strictly upon send-success in BackgroundMarketScanner
            var nowUtc = DateTime.UtcNow;

            var newSignal = new FuturesSignal
            {
                SignalNumber = sigNumber,
                Symbol = symbol,
                Direction = isTradeSignal ? direction : SignalDirection.Buy,
                SignalType = determinedType,
                Timeframe = timeframe,
                EntryPrice = 0, // Live WebSocket last price is assigned strictly upon emit/send-success
                CurrentPrice = calculationRefPrice,
                ConfluenceScore = directionalConfluence,
                Confidence = confidence,
                Status = SignalStatus.Open,
                OutcomeStatus = isTradeSignal ? "AKTİV 🟡" : "GÖZLƏMƏ ⚪",
                RemainingPositionRatio = 1.0m,
                RealizedProfitPercent = 0m,
                SourceCandleOpenTimeUtc = sourceCandleTime,
                GeneratedAt = nowUtc,
                ExpiryTimeUtc = nowUtc.AddMinutes(durationMinutes),
                TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted,
                NewsSentimentImpact = newsSummary.Status,
                AnalysisReasons = reasons,
                Indicators = indicators,
                BtcCompass = btcCompass
            };

            if (isTradeSignal)
            {
                // S/R Target and Exit calculation (Problem 2)
                var srResult = CalculateSrTargetsAndStops(closedKlines, direction, calculationRefPrice, indicators.Atr, indicators.Vwap);
                if (!srResult.Success)
                {
                    return new FuturesSignal
                    {
                        Symbol = symbol,
                        Timeframe = timeframe,
                        Direction = direction,
                        SignalType = "GÖZLƏMƏ ⚪",
                        Status = SignalStatus.Open,
                        OutcomeStatus = "GÖZLƏMƏ ⚪",
                        CurrentPrice = calculationRefPrice,
                        EntryPrice = 0,
                        ConfluenceScore = directionalConfluence,
                        Confidence = 50,
                        SourceCandleOpenTimeUtc = sourceCandleTime,
                        GeneratedAt = nowUtc,
                        ExpiryTimeUtc = nowUtc.AddMinutes(durationMinutes),
                        TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted,
                        AnalysisReasons = new List<string> { $"S/R Filter: {srResult.SkipReason}" }
                    };
                }

                if (direction == SignalDirection.Buy)
                {
                    decimal lowBound = buyerRejection ? closedCandle.Low : (calculationRefPrice * 0.9985m);
                    newSignal.EntryLow = RoundToCoinPrecision(calculationRefPrice, lowBound);
                    newSignal.EntryHigh = RoundToCoinPrecision(calculationRefPrice, calculationRefPrice * 1.0010m);
                }
                else
                {
                    decimal highBound = sellerRejection ? closedCandle.High : (calculationRefPrice * 1.0015m);
                    newSignal.EntryLow = RoundToCoinPrecision(calculationRefPrice, calculationRefPrice * 0.9990m);
                    newSignal.EntryHigh = RoundToCoinPrecision(calculationRefPrice, highBound);
                }

                newSignal.TakeProfit1 = RoundToCoinPrecision(calculationRefPrice, srResult.TakeProfit1);
                newSignal.TakeProfit2 = srResult.TakeProfit2 > 0 ? RoundToCoinPrecision(calculationRefPrice, srResult.TakeProfit2) : 0m;
                newSignal.TakeProfit3 = srResult.TakeProfit3 > 0 ? RoundToCoinPrecision(calculationRefPrice, srResult.TakeProfit3) : 0m;
                newSignal.StopLoss = RoundToCoinPrecision(calculationRefPrice, srResult.StopLoss);
                newSignal.InitialRiskR = srResult.InitialRiskR;
                newSignal.AtrPercent = srResult.AtrPercent;
                newSignal.SignalSwingLow = srResult.SignalSwingLow;
                newSignal.SignalSwingHigh = srResult.SignalSwingHigh;
            }

            _recentCandleSignals[candleKey] = newSignal;

            // Create indicator snapshots
            newSignal.IndicatorSnapshots = new List<SignalIndicatorSnapshot>
            {
                new() { IndicatorName = "RSI14", Value = indicators.Rsi, Vote = indicators.RsiVote, Weight = 0.35m },
                new() { IndicatorName = "MACD_Hist", Value = indicators.MacdHist, Vote = indicators.MacdVote, Weight = 0.45m },
                new() { IndicatorName = "EMA20", Value = indicators.Ema20, Vote = indicators.EmaVote, Weight = 0.45m },
                new() { IndicatorName = "ADX14", Value = indicators.Adx, Vote = indicators.AdxVote, Weight = 0.30m },
                new() { IndicatorName = "BollingerBands", Value = indicators.BollingerBandwidth, Vote = indicators.BollingerVote, Weight = 0.15m },
                new() { IndicatorName = "SuperTrend", Value = indicators.SuperTrend, Vote = indicators.SuperTrendVote, Weight = 0.25m },
                new() { IndicatorName = "OBV", Value = indicators.Obv, Vote = indicators.ObvVote, Weight = 0.40m },
                new() { IndicatorName = "VWAP", Value = indicators.Vwap, Vote = indicators.VwapVote, Weight = 0.30m }
            };

            return newSignal;
        }

        public async Task<List<FuturesSignal>> GetTrackedActiveSignalsAsync()
        {
            return await _unitOfWork.Signals.GetOpenTrackedSignalsAsync();
        }

        public async Task<List<FuturesSignal>> GetSignalHistoryAsync(int count = 25)
        {
            return await _unitOfWork.Signals.GetRecentSignalsAsync(count);
        }

        public async Task<PerformanceStats> GetPerformanceStatsAsync(string? specificTimeframe = null, List<string>? userCoins = null)
        {
            return await _unitOfWork.Signals.GetPerformanceStatsAsync(specificTimeframe, userCoins);
        }

        public async Task<PerformanceStats> GetUserPerformanceStatsAsync(string chatId, string? specificTimeframe = null, List<string>? userCoins = null)
        {
            return await _unitOfWork.Signals.GetUserPerformanceStatsAsync(chatId, specificTimeframe, userCoins);
        }

        public async Task<List<FuturesSignal>> GetUserOpenSignalsAsync(string chatId)
        {
            return await _unitOfWork.Signals.GetUserOpenSignalsAsync(chatId);
        }

        public async Task ClearUserHistoryAsync(string chatId)
        {
            await _unitOfWork.Signals.ClearUserHistoryAsync(chatId);
        }

        public async Task<List<CryptoSense.Application.DTOs.CoinPerformanceBreakdownDto>> GetCoinPerformanceBreakdownAsync(List<string>? monitoredCoins = null)
        {
            return await _unitOfWork.Signals.GetCoinPerformanceBreakdownAsync(monitoredCoins);
        }

        public async Task ClearAllSignalsAsync()
        {
            await _unitOfWork.Signals.ClearAllSignalsAsync();
            _recentCandleSignals.Clear();
            CryptoSense.Worker.BackgroundMarketScanner.ResetSignalCounter();
        }

        public static void ResetSignalCounter()
        {
            CryptoSense.Worker.BackgroundMarketScanner.ResetSignalCounter();
        }
    }
}
