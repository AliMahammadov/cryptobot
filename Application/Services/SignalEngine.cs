using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Common;
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
        public static DateTime ProcessStartTimeUtc { get; set; } = DateTime.UtcNow;

        public static void InvalidateCandleCache(string symbol, string timeframe, DateTime sourceCandleTime)
        {
            var candleKey = $"{symbol}_{timeframe}_{sourceCandleTime:yyyyMMddHHmmss}";
            _recentCandleSignals.TryRemove(candleKey, out _);
        }

        public static void RecordSentSignalCandle(string symbol, string timeframe, DateTime sourceCandleTime, FuturesSignal signal)
        {
            var candleKey = $"{symbol}_{timeframe}_{sourceCandleTime:yyyyMMddHHmmss}";
            signal.SignalAlertSent = true;
            _recentCandleSignals[candleKey] = signal;
        }

        public static bool IsInRecentCandleCache(string symbol, string timeframe, DateTime sourceCandleTime)
        {
            var candleKey = $"{symbol}_{timeframe}_{sourceCandleTime:yyyyMMddHHmmss}";
            return _recentCandleSignals.ContainsKey(candleKey);
        }

        public static TimeSpan GetMaxLiveDelay(string timeframe)
        {
            bool isBootWindow = (DateTime.UtcNow - ProcessStartTimeUtc).TotalMinutes <= 15;
            return timeframe switch
            {
                "4h" => TimeSpan.FromHours(3),
                _ => isBootWindow ? TimeSpan.FromMinutes(90) : TimeSpan.FromMinutes(50)
            };
        }

        public static long GetMaxLiveDelayMs(string timeframe)
        {
            return (long)GetMaxLiveDelay(timeframe).TotalMilliseconds;
        }

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

        public static decimal GetCoinTickSize(decimal basePrice)
        {
            if (basePrice >= 100m) return 0.01m;
            if (basePrice >= 1m) return 0.0001m;
            if (basePrice >= 0.01m) return 0.00001m;
            return 0.00000001m;
        }

        public static SrTargetResult CalculateSrTargetsAndStops(
            List<Kline> closedKlines,
            SignalDirection direction,
            decimal calculationRefPrice,
            decimal atr14,
            decimal vwap,
            string timeframe = "1h")
        {
            var fail = new SrTargetResult(false, null, 0, 0, 0, 0, 0, 0, 0, 0, new List<decimal>());
            if (closedKlines == null || closedKlines.Count < 10 || calculationRefPrice <= 0)
            {
                return fail with { SkipReason = "SKIP_INSUFFICIENT_DATA" };
            }

            var k50 = closedKlines.TakeLast(Math.Min(50, closedKlines.Count)).ToList();
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

            var yesterdayUtc = DateTime.UtcNow.Date.AddDays(-1);
            var prevDayKlines = closedKlines.Where(k => k.Time.Date == yesterdayUtc).ToList();
            decimal prevDayHigh = prevDayKlines.Count > 0 ? prevDayKlines.Max(k => k.High) : 0m;
            decimal prevDayLow = prevDayKlines.Count > 0 ? prevDayKlines.Min(k => k.Low) : 0m;

            var rawLevels = new List<decimal>();
            rawLevels.AddRange(swingHighs);
            rawLevels.AddRange(swingLows);
            if (prevDayHigh > 0) rawLevels.Add(prevDayHigh);
            if (prevDayLow > 0) rawLevels.Add(prevDayLow);
            if (vwap > 0) rawLevels.Add(vwap);

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

            decimal atrPct = (atr14 > 0 && calculationRefPrice > 0) ? (atr14 / calculationRefPrice) * 100m : 1.0m;
            fail = new SrTargetResult(false, null, 0, 0, 0, 0, 0, atrPct, signalSwingLow, signalSwingHigh, clusters);

            decimal slDistance;
            decimal sl;

            if (direction == SignalDirection.Buy) // LONG
            {
                decimal fractalSwing = swingLows.LastOrDefault(l => l < calculationRefPrice);
                if (fractalSwing == 0)
                {
                    return fail with { SkipReason = "SKIP_NO_SWING" };
                }
                sl = fractalSwing - BotConstants.Thresholds.SlBufferAtr * atr14;
                slDistance = calculationRefPrice - sl;
            }
            else // SHORT (SignalDirection.Sell)
            {
                decimal fractalSwing = swingHighs.LastOrDefault(h => h > calculationRefPrice);
                if (fractalSwing == 0)
                {
                    return fail with { SkipReason = "SKIP_NO_SWING" };
                }
                sl = fractalSwing + BotConstants.Thresholds.SlBufferAtr * atr14;
                slDistance = sl - calculationRefPrice;
            }

            if (slDistance < BotConstants.Thresholds.MinSlAtr * atr14)
            {
                return fail with { SkipReason = $"SKIP_SL_TOO_TIGHT (SL {slDistance:F4} < {BotConstants.Thresholds.MinSlAtr} * ATR)" };
            }

            if (slDistance > BotConstants.Thresholds.MaxSlAtr * atr14)
            {
                return fail with { SkipReason = $"SKIP_SL_TOO_WIDE (SL {slDistance:F4} > {BotConstants.Thresholds.MaxSlAtr} * ATR)" };
            }

            // 05:00–07:00 +4 pəncərəsində 1h: əlavə filtr — SL minimum 1.70 ATR (nazik kitab)
            var aztNowHour = DateTime.UtcNow.AddHours(4).Hour;
            if (timeframe == "1h" && aztNowHour >= 5 && aztNowHour < 7)
            {
                if (slDistance < BotConstants.Thresholds.ThinBookAtr * atr14)
                {
                    return fail with { SkipReason = $"SKIP_SL_TOO_TIGHT (Thin book SL {slDistance:F4} < {BotConstants.Thresholds.ThinBookAtr} * ATR)" };
                }
            }

            // BƏND 5: CHASE QADAĞA: son 3×1h şamda qiymət artıq TP istiqamətində ≥1.2% getmişsə 1h kart AçMA (XRP 03:00, AVAX/SOL 05:00).
            // BOUNCE MODELİ SAXLA (#78): dump olub, 1h pullback, sonra şam bağlananda short — bu KEÇİR.
            if (timeframe == "1h" && closedKlines.Count >= 4)
            {
                var c3 = closedKlines[^4];
                var c2 = closedKlines[^3];
                var c1 = closedKlines[^2];
                var c0 = closedKlines[^1];

                if (direction == SignalDirection.Sell) // SHORT
                {
                    decimal dropPct = c3.Open > 0 ? ((c3.Open - c0.Close) / c3.Open) * 100m : 0m;
                    if (dropPct >= 1.20m)
                    {
                        // Bounce/pullback model (#78): dump olub, pullback baş verib və c0 rejection şamı kimi bağlanıb
                        bool isBounceShort = (c1.Close > c1.Open || (c0.High > c1.Close && c0.Close < c0.Open && c1.Close > c2.Low));
                        if (!isBounceShort)
                        {
                            Console.WriteLine($"[SignalEngine] Chase Short blocked: 3h drop {dropPct:F2}% >= 1.20% with no bounce.");
                            return fail with { SkipReason = $"SKIP_CHASE_SHORT (Son 3h-də {dropPct:F2}% enib, bounce yoxdur)" };
                        }
                    }
                }
                else if (direction == SignalDirection.Buy) // LONG
                {
                    decimal risePct = c3.Open > 0 ? ((c0.Close - c3.Open) / c3.Open) * 100m : 0m;
                    if (risePct >= 1.20m)
                    {
                        // Dip/pullback model: pump olub, dip/pullback baş verib və c0 rejection/bounce şamı kimi bağlanıb
                        bool isDipLong = (c1.Close < c1.Open || (c0.Low < c1.Close && c0.Close > c0.Open && c1.Close < c2.High));
                        if (!isDipLong)
                        {
                            Console.WriteLine($"[SignalEngine] Chase Long blocked: 3h rise {risePct:F2}% >= 1.20% with no dip.");
                            return fail with { SkipReason = $"SKIP_CHASE_LONG (Son 3h-də {risePct:F2}% artıb, dip yoxdur)" };
                        }
                    }
                }
            }

            // BƏND 3 — TP = 1.5R PARTİAL + STRUKTUR
            decimal riskR = slDistance;

            decimal tp1, tp2;
            if (direction == SignalDirection.Buy) // LONG
            {
                tp1 = calculationRefPrice + BotConstants.Thresholds.Tp1R * riskR;
                var nextRes = clusters.Where(c => c > tp1 + (0.15m * riskR)).OrderBy(c => c).ToList();
                if (nextRes.Count == 0)
                {
                    return fail with { SkipReason = "SKIP_NO_STRUCTURE_TARGET" };
                }
                tp2 = nextRes.First();
                if (tp2 <= tp1)
                {
                    return fail with { SkipReason = "SKIP_NO_STRUCTURE_TARGET" };
                }
            }
            else // SHORT
            {
                tp1 = calculationRefPrice - BotConstants.Thresholds.Tp1R * riskR;
                var nextSup = clusters.Where(c => c < tp1 - (0.15m * riskR)).OrderByDescending(c => c).ToList();
                if (nextSup.Count == 0)
                {
                    return fail with { SkipReason = "SKIP_NO_STRUCTURE_TARGET" };
                }
                tp2 = nextSup.First();
                if (tp2 >= tp1)
                {
                    return fail with { SkipReason = "SKIP_NO_STRUCTURE_TARGET" };
                }
            }

            tp1 = RoundToCoinPrecision(calculationRefPrice, tp1);
            tp2 = RoundToCoinPrecision(calculationRefPrice, tp2);
            sl = RoundToCoinPrecision(calculationRefPrice, sl);

            decimal tp3 = 0m;

            decimal tp1DistActual = Math.Abs(tp1 - calculationRefPrice);
            decimal tp2DistActual = Math.Abs(tp2 - calculationRefPrice);
            decimal weightedTpDist = (BotConstants.Thresholds.Tp1Weight * tp1DistActual) + (BotConstants.Thresholds.Tp2Weight * tp2DistActual);
            decimal rrRatio = riskR > 0 ? (weightedTpDist / riskR) : 0m;

            if (rrRatio < BotConstants.Thresholds.MinRiskReward)
            {
                Console.WriteLine($"[SignalEngine] R:R filter blocked (Weighted R:R {rrRatio:F2} < {BotConstants.Thresholds.MinRiskReward:F2})");
                return fail with { SkipReason = $"SKIP_LOW_RR (Weighted R:R {rrRatio:F2} < {BotConstants.Thresholds.MinRiskReward:F2})" };
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
                    compass.BtcDominanceThreshold = macro.DynamicDominanceThreshold > 0 
                        ? macro.DynamicDominanceThreshold 
                        : (macro.BtcDominance > 0 ? Math.Round(macro.BtcDominance * 0.98m, 2) : 56.5m);
                    compass.UsdtDominance = macro.UsdtDominance;
                    compass.MarketCapChange24h = macro.MarketCapChange24h;
                }
                catch (Exception _ex) { Console.WriteLine($"[SignalEngine] Swallowed exception: {_ex.Message}"); }

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
                    compass.Btc1hAdx = ind1h.Adx;
                    compass.Btc1hCandleColor = (closed1h.Count > 0 && closed1h.Last().Close >= closed1h.Last().Open) ? "Green" : "Red";

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

                    // BTC 4h SuperTrend & Momentum Strength
                    try
                    {
                        var btc4hKlines = await _marketData.GetKlinesAsync("BTCUSDT", "4h", 40);
                        if (btc4hKlines.Count >= 20)
                        {
                            var closedBtc4h = btc4hKlines.Count >= 2 ? btc4hKlines.Take(btc4hKlines.Count - 1).ToList() : btc4hKlines;
                            var indBtc4h = _indicatorEngine.CalculateIndicators(closedBtc4h);
                            compass.IsBtc4hSuperTrendBullish = indBtc4h.SuperTrendVote == IndicatorVote.Bullish;
                            compass.Btc4hStrongShort =
                                indBtc4h.SuperTrendVote == IndicatorVote.Bearish &&
                                (indBtc4h.ConfluenceScore <= 40m || (indBtc4h.Ema20 < indBtc4h.Ema50 && closedBtc4h.Last().Close < indBtc4h.Ema50));
                            compass.Btc4hStrongLong =
                                indBtc4h.SuperTrendVote == IndicatorVote.Bullish &&
                                (indBtc4h.ConfluenceScore >= 60m || (indBtc4h.Ema20 > indBtc4h.Ema50 && closedBtc4h.Last().Close > indBtc4h.Ema50));
                        }
                    }
                    catch (Exception _ex) { Console.WriteLine($"[SignalEngine] BTC 4h compass calculation error: {_ex.Message}"); }
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

        public async Task<FuturesSignal> AnalyzeCoinAsync(string symbol, string timeframe = "1h", bool isLiveScan = false, CancellationToken ct = default)
        {
            symbol = symbol.ToUpper();
            if (!symbol.EndsWith("USDT")) symbol += "USDT";

            if (isLiveScan && timeframe != "1h" && timeframe != "4h")
            {
                var now = DateTime.UtcNow;
                return new FuturesSignal
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    Direction = SignalDirection.Buy,
                    SignalType = "GÖZLƏMƏ ⚪",
                    Status = SignalStatus.Open,
                    OutcomeStatus = "GÖZLƏMƏ ⚪",
                    GeneratedAt = now,
                    ExpiryTimeUtc = now.AddMinutes(90),
                    TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted,
                    AnalysisReasons = new List<string> { "Yalnız 1h və 4h şam bağlanışları dəstəklənir (15m/5m/3m/30m qadağandır)." }
                };
            }

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
                var maxLiveDelay = GetMaxLiveDelay(timeframe);

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

            // Check if existing signal for this candle already created & delivered (Physical Dedup)
            var existingSignal = await _unitOfWork.Signals.GetExistingCandleSignalAsync(symbol, timeframe, sourceCandleTime);
            if (existingSignal != null && existingSignal.SignalAlertSent)
            {
                existingSignal.CurrentPrice = calculationRefPrice;
                _recentCandleSignals.TryAdd(candleKey, existingSignal);

                if (isLiveScan)
                {
                    return new FuturesSignal
                    {
                        Symbol = symbol,
                        Timeframe = timeframe,
                        Direction = existingSignal.Direction,
                        SignalType = "GÖZLƏMƏ (ŞAM İŞLƏNİB) ⚪",
                        Status = existingSignal.Status,
                        OutcomeStatus = existingSignal.OutcomeStatus,
                        CurrentPrice = calculationRefPrice,
                        EntryPrice = existingSignal.EntryPrice,
                        ConfluenceScore = existingSignal.ConfluenceScore,
                        Confidence = 50,
                        SourceCandleOpenTimeUtc = sourceCandleTime,
                        GeneratedAt = existingSignal.GeneratedAt,
                        ExpiryTimeUtc = existingSignal.ExpiryTimeUtc,
                        TimestampFormatted = existingSignal.TimestampFormatted,
                        AnalysisReasons = new List<string> { $"Bu şam ({sourceCandleTime:dd.MM.yyyy HH:mm}) üzrə artıq siqnal formalaşdırılıb və izləmədədir." }
                    };
                }

                return existingSignal;
            }

            if (_recentCandleSignals.TryGetValue(candleKey, out var cachedSig))
            {
                if (cachedSig.SignalAlertSent)
                {
                    cachedSig.CurrentPrice = calculationRefPrice;
                    if (isLiveScan)
                    {
                        return new FuturesSignal
                        {
                            Symbol = symbol,
                            Timeframe = timeframe,
                            Direction = cachedSig.Direction,
                            SignalType = "GÖZLƏMƏ (ŞAM İŞLƏNİB) ⚪",
                            Status = cachedSig.Status,
                            OutcomeStatus = cachedSig.OutcomeStatus,
                            CurrentPrice = calculationRefPrice,
                            EntryPrice = cachedSig.EntryPrice,
                            ConfluenceScore = cachedSig.ConfluenceScore,
                            Confidence = 50,
                            SourceCandleOpenTimeUtc = sourceCandleTime,
                            GeneratedAt = cachedSig.GeneratedAt,
                            ExpiryTimeUtc = cachedSig.ExpiryTimeUtc,
                            TimestampFormatted = cachedSig.TimestampFormatted,
                            AnalysisReasons = new List<string> { $"Bu şam ({sourceCandleTime:dd.MM.yyyy HH:mm}) artıq keşdə mövcuddur." }
                        };
                    }
                    return cachedSig;
                }
                else
                {
                    _recentCandleSignals.TryRemove(candleKey, out _);
                }
            }

            var btcCompass = await GetBtcCompassAsync();
            var macroOverview = await _marketData.GetMacroMarketOverviewAsync();
            var indicators = _indicatorEngine.CalculateIndicators(closedKlines, btcCompass);

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
                    var btcKlines = await _marketData.GetKlinesAsync("BTCUSDT", timeframe, closedKlines.Count);
                    var closedBtc = btcKlines.Count >= 2 ? btcKlines.Take(btcKlines.Count - 1).ToList() : btcKlines;
                    if (closedBtc.Count >= 10)
                    {
                        int alignCount = Math.Min(closedKlines.Count, closedBtc.Count);
                        var altCloses = closedKlines.TakeLast(alignCount).Select(k => k.Close).ToList();
                        var btcCloses = closedBtc.TakeLast(alignCount).Select(k => k.Close).ToList();
                        altBtcCorr = CalculatePearsonCorrelation(altCloses, btcCloses);

                        var lastAlt = closedKlines.Last();
                        var lastBtc = closedBtc.Last();
                        decimal altChg = lastAlt.Open > 0 ? (lastAlt.Close - lastAlt.Open) / lastAlt.Open : 0m;
                        decimal btcChg = lastBtc.Open > 0 ? (lastBtc.Close - lastBtc.Open) / lastBtc.Open : 0m;
                        altRs = altChg - btcChg;
                    }
                }
                catch (Exception _ex) { Console.WriteLine($"[SignalEngine] Swallowed exception: {_ex.Message}"); }
            }

            // BTC QAPI: BTC özü kompas tərəfindən bloklanmır (öz SuperTrend/confluence saxlanılır).
            bool btc4hStrongLong = btcCompass.Btc4hStrongLong;
            bool btc4hStrongShort = btcCompass.Btc4hStrongShort;

            bool rangingBullCandle = isAltcoin
                && btcCompass.Regime == BtcMarketRegime.Ranging
                && closedCandle.Close > closedCandle.Open;
            bool rangingBearCandle = isAltcoin
                && btcCompass.Regime == BtcMarketRegime.Ranging
                && closedCandle.Close < closedCandle.Open;

            // BTCUSDT heç vaxt kompasla bloklanmır.
            // Bullish + 4h Strong Long → alt LONG olar.
            // Ranging + yaşıl şam (close>open) → alt LONG olar.
            // Bearish və ya Ranging qırmızı/doji → alt LONG YOX.
            bool btcConfirmsLong = !isAltcoin
                || (btcCompass.Regime == BtcMarketRegime.Bullish && btc4hStrongLong)
                || rangingBullCandle;

            // Bearish → SHORT olar.
            // Ranging + qırmızı şam (close<open) → SHORT olar.
            // Ranging yaşıl/doji → SHORT YOX.
            // Bullish (ranging deyil) → SHORT-u burada kəsmə; GATE B 4h Strong Long-dursa SHORT-u kəsəcək.
            bool btcConfirmsShort = !isAltcoin
                || btcCompass.Regime == BtcMarketRegime.Bearish
                || rangingBearCandle
                || (btcCompass.Regime == BtcMarketRegime.Bullish && !btc4hStrongLong);

            bool ethConfirmsLong = true;
            bool ethConfirmsShort = true;

            if (isAltcoin)
            {
                bool isHighCorr = altBtcCorr > 0.7m;
                bool altRsPositive = altRs > 0m;

                // BTC 1h bullish VƏ alt-BTC 15m corr>0.7 VƏ alt RS müsbət → alt SHORT yalnız struktur breakdown (lower high + close below swing). Mean-reversion SHORT yox.
                if (btcCompass.Regime == BtcMarketRegime.Bullish && isHighCorr && altRsPositive)
                {
                    btcConfirmsShort = isStructuralBreakdown;
                }

                // BƏND 5 Korrelyasiya: BTC və ETH eyni 1h-də siqnal istiqamətini təsdiqləmirsə alt SHORT/LONG AçMA (ADA #72 tipi).
                try
                {
                    var ethKlines1h = await _marketData.GetKlinesAsync("ETHUSDT", "1h", 10);
                    var closedEth1h = ethKlines1h.Count >= 2 ? ethKlines1h.Take(ethKlines1h.Count - 1).ToList() : ethKlines1h;
                    if (closedEth1h.Count > 0)
                    {
                        var lastEth = closedEth1h.Last();
                        bool isEthBullish = lastEth.Close > lastEth.Open;
                        bool isEthBearish = lastEth.Close < lastEth.Open;

                        if (isEthBullish && !isStructuralBreakdown)
                        {
                            ethConfirmsShort = false;
                        }
                        if (isEthBearish && !isStructuralBreakout)
                        {
                            ethConfirmsLong = false;
                        }
                    }
                }
                catch (Exception _ex) { Console.WriteLine($"[SignalEngine] Swallowed ETH exception: {_ex.Message}"); }
            }

            // Market Regime & Chop Filter (Minimum ADX required: 16 for 4h, 22 for 1h)
            decimal minAdxRequired = timeframe == "4h"
                ? CryptoSense.Domain.Common.BotConstants.Thresholds.MinAdx4h
                : CryptoSense.Domain.Common.BotConstants.Thresholds.MinAdx1h;
            bool hasValidMarketRegime = indicators.Adx >= minAdxRequired;

            // Candle Action Confirmations (Never enter against impulsive counter-trend bars)
            bool bullishCandleConfirmation = closedCandle.Close >= closedCandle.Open || buyerRejection;
            bool bearishCandleConfirmation = closedCandle.Close <= closedCandle.Open || sellerRejection;
            bool volumeConfirmed = indicators.VolumeSurgeRatio >= 1.0m;

            // Extreme Volatility & High-Risk Anomaly Check
            bool isExtremeVolatility = (indicators.Atr > 0 && currentPrice > 0 && (indicators.Atr / currentPrice) >= 0.055m) ||
                                       (closedCandle.High > 0 && closedCandle.Low > 0 && ((closedCandle.High - closedCandle.Low) / closedCandle.Low) >= 0.075m) ||
                                       (indicators.VolumeSurgeRatio >= 4.0m);

            // Confluence tələbi: Confluence < 75% siqnal YASAQ!
            decimal minLongScore = CryptoSense.Domain.Common.BotConstants.Thresholds.MinConfluence1h4h;
            decimal maxShortScore = 100m - CryptoSense.Domain.Common.BotConstants.Thresholds.MinConfluence1h4h;

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
                btcConfirmsLong &&
                ethConfirmsLong)
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
                     btcConfirmsShort &&
                     ethConfirmsShort)
            {
                direction = SignalDirection.Sell;
                determinedType = breakoutShort ? "GÜCLÜ BREAKDOWN SHORT 🔴" : (isPullbackZone ? "GÜCLÜ RETEST SHORT 🔴" : "GÜCLÜ TREND SHORT 🔴");
                confidence = (int)Math.Clamp(Math.Round(100m - indicators.ConfluenceScore), 50, 96);
            }
            else
            {
                determinedType = "GÖZLƏMƏ ⚪";
                confidence = 50;
                if (!hasValidMarketRegime) reasons.Add($"Rejim Filtri: ADX ({indicators.Adx:F1}) < {minAdxRequired:F1} (Bazar zəif/yan konsolidasiyadadır)");
                if (indicators.SuperTrendVote != IndicatorVote.Bullish && isUptrend) reasons.Add("SuperTrend təsdiqi yoxdur (Trend ziddiyyətlidir)");
            }

            // Alt LONG təhlükəsizlik baryeri: BTC 1h və ya 4h təsdiq etmirsə alt LONG qətiyyən buraxılmasın
            if (isAltcoin && determinedType.Contains("LONG"))
            {
                if (!btcConfirmsLong)
                {
                    determinedType = "GÖZLƏMƏ ⚪";
                    confidence = 50;
                    if (btcCompass.Regime == BtcMarketRegime.Bearish)
                        reasons.Add("SKIP_BTC_BEAR_LONG: BTC 1h Bearish — alt LONG bloklandı");
                    else if (btcCompass.Regime == BtcMarketRegime.Ranging)
                        reasons.Add("SKIP_BTC_RANGE: BTC 1h Ranging + qırmızı/doji şam — alt LONG bloklandı");
                    else
                        reasons.Add("SKIP_BTC_BEAR_LONG: BTC 1h Bullish və BTC 4h SuperTrend Bullish tələb olunur — alt LONG bloklandı");
                }
                else if (!ethConfirmsLong)
                {
                    determinedType = "GÖZLƏMƏ ⚪";
                    confidence = 50;
                    reasons.Add("ETH 1h Bearish: altcoin LONG üçün ETH təsdiqi yoxdur");
                }
            }

            if (timeframe == "1h" && (determinedType.Contains("LONG") || determinedType.Contains("SHORT")))
            {
                try
                {
                    var klines4h = await _marketData.GetKlinesAsync(symbol, "4h", 40);
                    if (klines4h.Count >= 20)
                    {
                        var closed4h = klines4h.Count >= 2 ? klines4h.Take(klines4h.Count - 1).ToList() : klines4h;
                        var ind4h = _indicatorEngine.CalculateIndicators(closed4h);
                        decimal lastClose4h = closed4h.Last().Close;

                        bool alignedLong = ind4h.SuperTrendVote == IndicatorVote.Bullish && lastClose4h > ind4h.Ema50;
                        bool alignedShort = ind4h.SuperTrendVote == IndicatorVote.Bearish && lastClose4h < ind4h.Ema50;

                        if (determinedType.Contains("LONG") && !alignedLong)
                        {
                            determinedType = "GÖZLƏMƏ ⚪";
                            confidence = 50;
                            reasons.Add("SKIP_HTF_OPPOSE: 1h LONG vs 4h not bullish");
                        }
                        else if (determinedType.Contains("SHORT") && !alignedShort)
                        {
                            determinedType = "GÖZLƏMƏ ⚪";
                            confidence = 50;
                            reasons.Add("SKIP_HTF_OPPOSE: 1h SHORT vs 4h not bullish");
                        }
                    }
                }
                catch (Exception _ex) { Console.WriteLine($"[SignalEngine] Swallowed exception: {_ex.Message}"); }
            }

            if (isAltcoin && determinedType.Contains("SHORT"))
            {
                if (!btcConfirmsShort)
                {
                    determinedType = "GÖZLƏMƏ ⚪";
                    confidence = 50;
                    if (btcCompass.Regime == BtcMarketRegime.Ranging)
                        reasons.Add("SKIP_BTC_RANGE: BTC 1h Ranging + yaşıl/doji şam — alt SHORT bloklandı");
                    else if (btcCompass.Regime == BtcMarketRegime.Bullish && altBtcCorr > 0.7m && altRs > 0m && !isStructuralBreakdown)
                        reasons.Add("BTC 1h Bullish, corr>0.7 və alt RS müsbət: Altcoin SHORT yalnız struktur breakdown olduqda açıla bilər (Mean-reversion SHORT bloklandı)");
                    else
                        reasons.Add("SKIP_BTC_RANGE: alt SHORT üçün BTC təsdiqi yoxdur");
                }
                else if (!ethConfirmsShort)
                {
                    determinedType = "GÖZLƏMƏ ⚪";
                    confidence = 50;
                    reasons.Add("ETH 1h Bullish: altcoin SHORT üçün ETH təsdiqi yoxdur (ADA #72 filtri)");
                }
            }

            // --- ADDITIVE GATE B: BTC 4h güclü əks istiqamət (1h və 4h alt tətbiq olunur) ---
            if (isLiveScan
                && (timeframe == "1h" || timeframe == "4h")
                && isAltcoin
                && (determinedType.Contains("LONG") || determinedType.Contains("SHORT")))
            {
                try
                {
                    if (determinedType.Contains("LONG") && btc4hStrongShort)
                    {
                        determinedType = "GÖZLƏMƏ ⚪";
                        confidence = 50;
                        reasons.Add($"SKIP_BTC_4H_OPPOSE: BTC 4h güclü SHORT — {timeframe} alt LONG bloklandı");
                    }
                    else if (determinedType.Contains("SHORT") && btc4hStrongLong)
                    {
                        determinedType = "GÖZLƏMƏ ⚪";
                        confidence = 50;
                        reasons.Add($"SKIP_BTC_4H_OPPOSE: BTC 4h güclü LONG — {timeframe} alt SHORT bloklandı");
                    }
                }
                catch (Exception _ex) { Console.WriteLine($"[SignalEngine] BTC4h gate swallowed: {_ex.Message}"); }
            }

            bool isTradeSignal = determinedType.Contains("LONG") || determinedType.Contains("SHORT");
            decimal directionalConfluence = isTradeSignal
                ? (direction == SignalDirection.Sell ? Math.Round(100m - indicators.ConfluenceScore, 1) : Math.Round(indicators.ConfluenceScore, 1))
                : Math.Round(Math.Max(indicators.ConfluenceScore, 100m - indicators.ConfluenceScore), 1);

            // Confluence < 75% siqnal YASAQ — yalnız real LONG/SHORT cəhdi olanda Confluence Filtri səbəbini yaz
            if (isTradeSignal && directionalConfluence < CryptoSense.Domain.Common.BotConstants.Thresholds.MinConfluence1h4h)
            {
                determinedType = "GÖZLƏMƏ ⚪";
                confidence = 50;
                reasons.Add($"Confluence Filtri: {directionalConfluence:F1}% < {CryptoSense.Domain.Common.BotConstants.Thresholds.MinConfluence1h4h:F1}% (Siqnal üçün minimal {CryptoSense.Domain.Common.BotConstants.Thresholds.MinConfluence1h4h:F0}% tələbi ödənmir)");
                isTradeSignal = false;
            }

            int durationMinutes = timeframe switch
            {
                "1m" => 15,
                "3m" => 60,
                "5m" => 60,
                "15m" => 90,
                "4h" => 4320,
                _ => 2160
            };

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
                NewsSentimentImpact = "Neytral ⚪",
                AnalysisReasons = reasons,
                Indicators = indicators,
                BtcCompass = btcCompass
            };

            if (isTradeSignal)
            {
                // S/R Target and Exit calculation (1h / 4h ATR Cap and R:R >= 1.30)
                var srResult = CalculateSrTargetsAndStops(closedKlines, direction, calculationRefPrice, indicators.Atr, indicators.Vwap, timeframe);
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
                newSignal.TakeProfit2 = RoundToCoinPrecision(calculationRefPrice, srResult.TakeProfit2);
                newSignal.TakeProfit3 = 0m;
                newSignal.StopLoss = RoundToCoinPrecision(calculationRefPrice, srResult.StopLoss);
                newSignal.InitialRiskR = srResult.InitialRiskR;
                newSignal.AtrPercent = srResult.AtrPercent;
                newSignal.SignalSwingLow = srResult.SignalSwingLow;
                newSignal.SignalSwingHigh = srResult.SignalSwingHigh;
            }

            if (newSignal.SignalAlertSent)
            {
                _recentCandleSignals[candleKey] = newSignal;
            }

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

        public async Task<PerformanceStats> GetPerformanceStatsAsync(
            string? specificTimeframe = null, 
            List<string>? userCoins = null, 
            DateTime? sinceUtc = null, 
            DateTime? untilUtc = null, 
            bool isAllTime = false)
        {
            return await _unitOfWork.Signals.GetPerformanceStatsAsync(specificTimeframe, userCoins, sinceUtc, untilUtc, isAllTime);
        }

        public async Task<PerformanceStats> GetUserPerformanceStatsAsync(
            string chatId, 
            string? specificTimeframe = null, 
            List<string>? userCoins = null, 
            DateTime? sinceUtc = null, 
            DateTime? untilUtc = null, 
            bool isAllTime = false)
        {
            return await _unitOfWork.Signals.GetUserPerformanceStatsAsync(chatId, specificTimeframe, userCoins, sinceUtc, untilUtc, isAllTime);
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

        private static int _nextSignalNumber = 0;
        public static int NextSignalNumber => _nextSignalNumber;

        public async Task ClearAllSignalsAsync()
        {
            await _unitOfWork.Signals.ClearAllSignalsAsync();
            _recentCandleSignals.Clear();
            ResetSignalCounter();
        }

        public static void ResetSignalCounter()
        {
            Interlocked.Exchange(ref _nextSignalNumber, 0);
        }
    }
}
