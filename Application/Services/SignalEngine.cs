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

        private static int _nextSignalNumber = 1;
        private static bool _initializedNumber = false;
        private static readonly object _lock = new();

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

            InitializeHighestSignalNumber();
        }

        private void InitializeHighestSignalNumber()
        {
            if (_initializedNumber) return;
            lock (_lock)
            {
                if (_initializedNumber) return;
                try
                {
                    var maxNum = _unitOfWork.Signals.GetMaxSignalNumberAsync().GetAwaiter().GetResult();
                    if (maxNum >= _nextSignalNumber) _nextSignalNumber = maxNum + 1;
                    _initializedNumber = true;
                }
                catch
                {
                }
            }
        }

        public static decimal RoundToCoinPrecision(decimal basePrice, decimal value)
        {
            if (basePrice >= 100m) return Math.Round(value, 2);
            if (basePrice >= 1m) return Math.Round(value, 4);
            if (basePrice >= 0.01m) return Math.Round(value, 5);
            return Math.Round(value, 8);
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

                var btcKlines = await _marketData.GetKlinesAsync("BTCUSDT", "15m", 60);
                if (btcKlines.Count == 0)
                {
                    btcKlines = await _marketData.GetKlinesAsync("BTCUSDT", "3m", 60);
                }
                if (btcKlines.Count == 0)
                {
                    btcKlines = await _marketData.GetKlinesAsync("BTCUSDT", "1m", 60);
                }

                var compass = new BtcMarketCompass
                {
                    TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted
                };

                // 1. Live 24h Ticker & Dominance (always ensures live price even if klines lag)
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

                if (btcKlines.Count > 0)
                {
                    var currentPrice = btcKlines.Last().Close;
                    if (compass.Price == 0) compass.Price = currentPrice;

                    if (compass.Change24h == 0)
                    {
                        var openPrice = btcKlines.First().Open;
                        compass.Change24h = openPrice > 0 ? Math.Round(((currentPrice - openPrice) / openPrice) * 100, 2) : 0;
                        compass.High24h = btcKlines.Max(k => k.High);
                        compass.Low24h = btcKlines.Min(k => k.Low);
                        compass.VolumeQuote = btcKlines.Sum(k => k.Volume * k.Close);
                    }

                    var indicators = _indicatorEngine.CalculateIndicators(btcKlines);
                    compass.Rsi15m = indicators.Rsi;
                    compass.EmaStructure = indicators.EmaTrend;
                    compass.Ema20 = indicators.Ema20;
                    compass.Ema50 = indicators.Ema50;
                    compass.MacdHist = indicators.MacdHist;
                    compass.SuperTrend = indicators.SuperTrend;
                    compass.SupportLevel = indicators.SupportLevel;
                    compass.ResistanceLevel = indicators.ResistanceLevel;

                    int score = 50;
                    if (indicators.Ema20 > indicators.Ema50) score += 20;
                    else score -= 20;
                    if (indicators.MacdHist > 0) score += 15;
                    else score -= 15;
                    if (indicators.Rsi >= 50 && indicators.Rsi <= 68) score += 15;
                    else if (indicators.Rsi < 48) score -= 15;

                    compass.BullishScore = Math.Clamp(score, 5, 95);
                    if (compass.BullishScore >= 60)
                    {
                        compass.Trend = "YÜKSƏLİŞ (BULLISH) 🟢";
                        compass.Summary = "Bitcoin 15m/1h strukturu güclüdür və dinamik dəstək səviyyəsi üzərindədir. Long əməliyyatlarına üstünlük verilir.";
                    }
                    else if (compass.BullishScore <= 40)
                    {
                        compass.Trend = "ENİŞ (BEARISH) 🔴";
                        compass.Summary = "Bitcoin satış təzyiqi altındadır və EMA xətlərinin altındadır. Short əməliyyatlarına üstünlük verilir.";
                    }
                    else
                    {
                        compass.Trend = "NEYTRAL (YAN HƏRƏKƏT) ⚪";
                        compass.Summary = "Bitcoin yan hərəkətdədir (konsolidasiya). Qısa scalping və dəqiq Stop-Loss tövsiyə olunur.";
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

            // Closed candle evaluation to prevent flickering
            var closedCandle = klines.Count >= 2 ? klines[klines.Count - 2] : klines.Last();
            var sourceCandleTime = closedCandle.Time;
            var currentPrice = klines.Last().Close;

            var candleKey = $"{symbol}_{timeframe}_{sourceCandleTime:yyyyMMddHHmmss}";

            // Check if existing signal for this candle already created (Physical Dedup)
            if (isLiveScan)
            {
                var existingSignal = await _unitOfWork.Signals.GetExistingCandleSignalAsync(symbol, timeframe, sourceCandleTime);
                if (existingSignal != null)
                {
                    existingSignal.CurrentPrice = currentPrice;
                    return existingSignal;
                }
            }

            if (_recentCandleSignals.TryGetValue(candleKey, out var cachedSig))
            {
                cachedSig.CurrentPrice = currentPrice;
                return cachedSig;
            }

            var btcCompass = await GetBtcCompassAsync();
            var macroOverview = await _marketData.GetMacroMarketOverviewAsync();
            var indicators = _indicatorEngine.CalculateIndicators(klines, btcCompass);
            var newsSummary = await _newsService.GetNewsAndSentimentAsync();

            var reasons = new List<string>();

            // 1. Core Technical Indicators (EMA, MA, MACD, SuperTrend)
            reasons.Add($"EMA (20/50): ${indicators.Ema20} / ${indicators.Ema50} ({indicators.EmaTrend})");
            reasons.Add($"MA / SMA (20/50): ${indicators.Sma20} / ${indicators.Sma50} ({indicators.SmaTrend})");
            reasons.Add($"MACD (12,26,9): Hist={indicators.MacdHist:F4} ({indicators.MacdStatus})");
            reasons.Add($"SuperTrend: ${indicators.SuperTrend} ({indicators.SuperTrendDirection})");

            // 2. Macro Dominance & BTC Compass
            reasons.Add($"Bitcoin Kompası: {btcCompass.Trend} ({btcCompass.BullishScore}%)");
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

            // F. Macro & Bitcoin Compass Alignment
            bool isAltcoin = symbol != "BTCUSDT";
            bool highBtcDominance = macroOverview.BtcDominance >= 58.0m;
            bool btcConfirmsLong = isAltcoin ? (btcCompass.BullishScore >= 45 && !highBtcDominance) : (btcCompass.BullishScore >= 45);
            bool btcConfirmsShort = isAltcoin ? (btcCompass.BullishScore <= 55 || highBtcDominance) : (btcCompass.BullishScore <= 55);

            // =========================================================================
            // 🟢 PEŞƏKAR RETEST / BREAKOUT LONG
            // =========================================================================
            if ((isUptrend && (isPullbackZone || buyerRejection) && rsiAllowsLong && btcConfirmsLong) ||
                (breakoutLong && indicators.SuperTrendVote == IndicatorVote.Bullish && indicators.MacdHist > 0 && btcConfirmsLong))
            {
                direction = SignalDirection.Buy;
                determinedType = breakoutLong ? "PEŞƏKAR BREAKOUT LONG 🟢" : "PEŞƏKAR RETEST LONG 🟢";
                confidence = (int)Math.Clamp(Math.Round(indicators.ConfluenceScore), 50, 96);
            }
            // =========================================================================
            // 🔴 PEŞƏKAR RETEST / BREAKDOWN SHORT
            // =========================================================================
            else if ((isDowntrend && (isPullbackZone || sellerRejection) && rsiAllowsShort && btcConfirmsShort) ||
                     (breakoutShort && indicators.SuperTrendVote == IndicatorVote.Bearish && indicators.MacdHist < 0 && btcConfirmsShort))
            {
                direction = SignalDirection.Sell;
                determinedType = breakoutShort ? "PEŞƏKAR BREAKDOWN SHORT 🔴" : "PEŞƏKAR RETEST SHORT 🔴";
                confidence = (int)Math.Clamp(Math.Round(100m - indicators.ConfluenceScore), 50, 96);
            }
            // İkincili Yüksək Confluence Süzgəci:
            else if (indicators.ConfluenceScore >= 72m && btcConfirmsLong && rsiAllowsLong)
            {
                direction = SignalDirection.Buy;
                determinedType = "PEŞƏKAR TREND LONG 🟢";
                confidence = (int)Math.Clamp(Math.Round(indicators.ConfluenceScore), 50, 95);
            }
            else if (indicators.ConfluenceScore <= 28m && btcConfirmsShort && rsiAllowsShort)
            {
                direction = SignalDirection.Sell;
                determinedType = "PEŞƏKAR TREND SHORT 🔴";
                confidence = (int)Math.Clamp(Math.Round(100m - indicators.ConfluenceScore), 50, 95);
            }

            decimal directionalConfluence = direction == SignalDirection.Sell 
                ? Math.Round(100m - indicators.ConfluenceScore, 1) 
                : Math.Round(indicators.ConfluenceScore, 1);

            decimal minTfMultiplier = timeframe switch
            {
                "1m" => 0.005m,
                "3m" => 0.008m,
                "5m" => 0.010m,
                "15m" => 0.015m,
                "1h" => 0.030m,
                "4h" => 0.050m,
                _ => 0.015m
            };

            decimal atr = indicators.Atr > 0 ? indicators.Atr : (currentPrice * minTfMultiplier);
            decimal minRisk = currentPrice * minTfMultiplier;
            if (atr < minRisk) atr = minRisk;

            int durationMinutes = timeframe switch
            {
                "1m" or "3m" or "5m" => 240,  // 4 hours minimum safe market swing window
                "15m" => 720,                 // 12 hours
                "1h" => 1440,                 // 24 hours
                "4h" => 2880,                 // 48 hours
                _ => 720
            };

            bool isTradeSignal = determinedType.Contains("LONG") || determinedType.Contains("SHORT");
            int sigNumber = isTradeSignal ? Interlocked.Increment(ref _nextSignalNumber) : 0;

            var newSignal = new FuturesSignal
            {
                SignalNumber = sigNumber,
                Symbol = symbol,
                Direction = isTradeSignal ? direction : SignalDirection.Buy,
                SignalType = determinedType,
                Timeframe = timeframe,
                EntryPrice = currentPrice,
                CurrentPrice = currentPrice,
                ConfluenceScore = directionalConfluence,
                Confidence = confidence,
                Status = SignalStatus.Open,
                OutcomeStatus = isTradeSignal ? "AKTİV 🟡" : "NEYTRAL ⚪",
                SourceCandleOpenTimeUtc = sourceCandleTime,
                GeneratedAt = DateTime.UtcNow,
                ExpiryTimeUtc = DateTime.UtcNow.AddMinutes(durationMinutes),
                TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted,
                NewsSentimentImpact = newsSummary.Status,
                AnalysisReasons = reasons,
                Indicators = indicators,
                BtcCompass = btcCompass
            };

            // Lokal Dəstək və Müqavimət Səviyyələri üzrə TP və SL Təyini
            decimal localSupport = indicators.SupportLevel > 0 ? indicators.SupportLevel : (currentPrice - (atr * 1.2m));
            decimal localResistance = indicators.ResistanceLevel > 0 ? indicators.ResistanceLevel : (currentPrice + (atr * 1.2m));

            if (direction == SignalDirection.Buy)
            {
                decimal lowBound = buyerRejection ? closedCandle.Low : (currentPrice * 0.9985m);
                newSignal.EntryLow = RoundToCoinPrecision(currentPrice, lowBound);
                newSignal.EntryHigh = RoundToCoinPrecision(currentPrice, currentPrice * 1.0010m);

                // Stop-Loss: Lokal dəstəyin bir az altına
                decimal slTarget = localSupport * 0.998m;
                if (currentPrice - slTarget < (atr * 0.8m)) slTarget = currentPrice - (atr * 1.1m);
                newSignal.StopLoss = RoundToCoinPrecision(currentPrice, slTarget);

                decimal risk = currentPrice - newSignal.StopLoss;
                if (risk <= 0) risk = currentPrice * 0.01m;

                newSignal.TakeProfit1 = RoundToCoinPrecision(currentPrice, currentPrice + (risk * 1.2m));
                decimal tp2Candidate = localResistance > currentPrice ? localResistance : currentPrice + (risk * 2.0m);
                newSignal.TakeProfit2 = RoundToCoinPrecision(currentPrice, tp2Candidate);
                newSignal.TakeProfit3 = RoundToCoinPrecision(currentPrice, currentPrice + (risk * 3.0m));
            }
            else // SHORT
            {
                decimal highBound = sellerRejection ? closedCandle.High : (currentPrice * 1.0015m);
                newSignal.EntryLow = RoundToCoinPrecision(currentPrice, currentPrice * 0.9990m);
                newSignal.EntryHigh = RoundToCoinPrecision(currentPrice, highBound);

                // Stop-Loss: Lokal müqavimətin bir az üstünə
                decimal slTarget = localResistance * 1.002m;
                if (slTarget - currentPrice < (atr * 0.8m)) slTarget = currentPrice + (atr * 1.1m);
                newSignal.StopLoss = RoundToCoinPrecision(currentPrice, slTarget);

                decimal risk = newSignal.StopLoss - currentPrice;
                if (risk <= 0) risk = currentPrice * 0.01m;

                newSignal.TakeProfit1 = RoundToCoinPrecision(currentPrice, currentPrice - (risk * 1.2m));
                decimal tp2Candidate = localSupport < currentPrice ? localSupport : currentPrice - (risk * 2.0m);
                newSignal.TakeProfit2 = RoundToCoinPrecision(currentPrice, tp2Candidate);
                newSignal.TakeProfit3 = RoundToCoinPrecision(currentPrice, currentPrice - (risk * 3.0m));
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

            if (isLiveScan && (determinedType.Contains("LONG") || determinedType.Contains("SHORT")))
            {
                try
                {
                    await _unitOfWork.Signals.AddAsync(newSignal);
                    await _unitOfWork.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DB Persist warning: {ex.Message}");
                }
            }

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

        public async Task<List<CryptoSense.Application.DTOs.CoinPerformanceBreakdownDto>> GetCoinPerformanceBreakdownAsync(List<string>? monitoredCoins = null)
        {
            return await _unitOfWork.Signals.GetCoinPerformanceBreakdownAsync(monitoredCoins);
        }

        public async Task ClearAllSignalsAsync()
        {
            await _unitOfWork.Signals.ClearAllSignalsAsync();
            _recentCandleSignals.Clear();
            lock (_lock)
            {
                _nextSignalNumber = 1;
                _initializedNumber = true;
            }
        }

        public static void ResetSignalCounter()
        {
            lock (_lock)
            {
                _nextSignalNumber = 1;
                _initializedNumber = true;
            }
        }
    }
}
