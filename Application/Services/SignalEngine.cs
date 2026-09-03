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

        private static int _nextSignalNumber = 160;
        private static bool _initializedNumber = false;
        private static readonly object _lock = new();

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
            var btcKlines = await _marketData.GetKlinesAsync("BTCUSDT", "15m", 60);
            var compass = new BtcMarketCompass
            {
                TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted
            };

            if (btcKlines.Count == 0) return compass;

            var currentPrice = btcKlines.Last().Close;
            var openPrice = btcKlines.First().Open;
            compass.Price = currentPrice;
            compass.Change24h = openPrice > 0 ? Math.Round(((currentPrice - openPrice) / openPrice) * 100, 2) : 0;

            var indicators = _indicatorEngine.CalculateIndicators(btcKlines);
            compass.Rsi15m = indicators.Rsi;
            compass.EmaStructure = indicators.EmaTrend;

            int score = 50;
            if (indicators.EmaTrend.Contains("Bullish") || indicators.EmaTrend.Contains("Müsbət")) score += 20;
            if (indicators.EmaTrend.Contains("Bearish") || indicators.EmaTrend.Contains("Mənfi")) score -= 20;
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

            return compass;
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

            var btcCompass = await GetBtcCompassAsync();
            var macroOverview = await _marketData.GetMacroMarketOverviewAsync();
            var indicators = _indicatorEngine.CalculateIndicators(klines, btcCompass);
            var newsSummary = await _newsService.GetNewsAndSentimentAsync();

            var reasons = new List<string>();

            // 1. Core Technical Indicators (EMA, MA, MACD)
            reasons.Add($"EMA (20/50): ${indicators.Ema20} / ${indicators.Ema50} ({indicators.EmaTrend})");
            reasons.Add($"MA / SMA (20/50): ${indicators.Sma20} / ${indicators.Sma50} ({indicators.SmaTrend})");
            reasons.Add($"MACD (12,26,9): Hist={indicators.MacdHist:F4} ({indicators.MacdStatus})");

            // 2. Macro Dominance & BTC Compass
            reasons.Add($"Bitcoin Kompası: {btcCompass.Trend} ({btcCompass.BullishScore}%)");
            reasons.Add($"Dominasiya: BTC.D {macroOverview.BtcDominance}% | USDT.D {macroOverview.UsdtDominance}%");

            // 3. High-Probability Decision Logic (Target: >=70% Win-Rate)
            SignalDirection direction = SignalDirection.Buy;
            string determinedType = "NEYTRAL (GÖZLƏMƏ) ⚪";
            int confidence = 65;

            bool emaBullish = currentPrice > indicators.Ema20 && indicators.Ema20 >= indicators.Ema50;
            bool smaBullish = currentPrice > indicators.Sma20;
            bool macdBullish = indicators.MacdHist > 0 && indicators.Macd >= indicators.MacdSignal;
            bool btcNotBearish = btcCompass.BullishScore >= 45;

            bool emaBearish = currentPrice < indicators.Ema20 && indicators.Ema20 <= indicators.Ema50;
            bool smaBearish = currentPrice < indicators.Sma20;
            bool macdBearish = indicators.MacdHist < 0 && indicators.Macd <= indicators.MacdSignal;
            bool btcNotBullish = btcCompass.BullishScore <= 55;

            // Long Setup: EMA Bullish + SMA Bullish + MACD Bullish + BTC not bearish
            if (emaBullish && smaBullish && macdBullish && btcNotBearish)
            {
                direction = SignalDirection.Buy;
                determinedType = "GÜCLÜ LONG (ALIŞ) 🟢";
                confidence = Math.Clamp((int)indicators.ConfluenceScore, 78, 96);
                if (confidence < 80) confidence = 80;
            }
            // Short Setup: EMA Bearish + SMA Bearish + MACD Bearish + BTC not bullish
            else if (emaBearish && smaBearish && macdBearish && btcNotBullish)
            {
                direction = SignalDirection.Sell;
                determinedType = "GÜCLÜ SHORT (SATIŞ) 🔴";
                confidence = Math.Clamp((int)(100m - indicators.ConfluenceScore), 78, 96);
                if (confidence < 80) confidence = 80;
            }
            // Secondary High-Confluence confirmation
            else if (indicators.ConfluenceScore >= 80m && btcNotBearish && indicators.MacdHist >= 0)
            {
                direction = SignalDirection.Buy;
                determinedType = "GÜCLÜ LONG (ALIŞ) 🟢";
                confidence = (int)indicators.ConfluenceScore;
            }
            else if (indicators.ConfluenceScore <= 20m && btcNotBullish && indicators.MacdHist <= 0)
            {
                direction = SignalDirection.Sell;
                determinedType = "GÜCLÜ SHORT (SATIŞ) 🔴";
                confidence = (int)(100m - indicators.ConfluenceScore);
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

            var durationMinutes = timeframe switch
            {
                "1m" => 1,
                "3m" => 3,
                "5m" => 5,
                "15m" => 15,
                "1h" => 60,
                "4h" => 240,
                _ => 15
            };

            // Check if existing signal for this candle already created (Physical Dedup)
            var existingSignal = await _unitOfWork.Signals.GetExistingCandleSignalAsync(symbol, timeframe, sourceCandleTime);

            if (existingSignal != null)
            {
                existingSignal.CurrentPrice = currentPrice;
                existingSignal.Indicators = indicators;
                existingSignal.BtcCompass = btcCompass;
                existingSignal.AnalysisReasons = reasons;
                existingSignal.ConfluenceScore = directionalConfluence;
                return existingSignal;
            }

            int sigNumber = Interlocked.Increment(ref _nextSignalNumber);

            var newSignal = new FuturesSignal
            {
                SignalNumber = sigNumber,
                Symbol = symbol,
                Direction = direction,
                SignalType = determinedType,
                Timeframe = timeframe,
                EntryPrice = currentPrice,
                CurrentPrice = currentPrice,
                ConfluenceScore = directionalConfluence,
                Confidence = confidence,
                Status = SignalStatus.Open,
                OutcomeStatus = "AKTİV 🟡",
                SourceCandleOpenTimeUtc = sourceCandleTime,
                GeneratedAt = DateTime.UtcNow,
                ExpiryTimeUtc = DateTime.UtcNow.AddMinutes(durationMinutes),
                TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted,
                NewsSentimentImpact = newsSummary.Status,
                AnalysisReasons = reasons,
                Indicators = indicators,
                BtcCompass = btcCompass
            };

            // Entry range & TP/SL setup
            newSignal.EntryLow = RoundToCoinPrecision(currentPrice, currentPrice * 0.9985m);
            newSignal.EntryHigh = RoundToCoinPrecision(currentPrice, currentPrice * 1.0015m);

            if (direction == SignalDirection.Buy)
            {
                newSignal.TakeProfit1 = RoundToCoinPrecision(currentPrice, currentPrice + (atr * 1.0m));
                newSignal.TakeProfit2 = RoundToCoinPrecision(currentPrice, currentPrice + (atr * 1.8m));
                newSignal.TakeProfit3 = RoundToCoinPrecision(currentPrice, currentPrice + (atr * 2.8m));
                newSignal.StopLoss = RoundToCoinPrecision(currentPrice, currentPrice - (atr * 1.2m));
            }
            else
            {
                newSignal.TakeProfit1 = RoundToCoinPrecision(currentPrice, currentPrice - (atr * 1.0m));
                newSignal.TakeProfit2 = RoundToCoinPrecision(currentPrice, currentPrice - (atr * 1.8m));
                newSignal.TakeProfit3 = RoundToCoinPrecision(currentPrice, currentPrice - (atr * 2.8m));
                newSignal.StopLoss = RoundToCoinPrecision(currentPrice, currentPrice + (atr * 1.2m));
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

            if (determinedType.Contains("LONG") || determinedType.Contains("SHORT"))
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

        public async Task<PerformanceStats> GetPerformanceStatsAsync()
        {
            return await _unitOfWork.Signals.GetPerformanceStatsAsync();
        }
    }
}
