using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Data;
using CryptoSense.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoSense.Services
{
    public class SignalEngine
    {
        private readonly BinanceFuturesService _binanceService;
        private readonly IndicatorService _indicatorService;
        private readonly NewsService _newsService;
        private readonly IServiceProvider _serviceProvider;

        private static int _nextSignalNumber = 160;
        private static readonly object _lock = new();

        public SignalEngine(
            BinanceFuturesService binanceService,
            IndicatorService indicatorService,
            NewsService newsService,
            IServiceProvider serviceProvider)
        {
            _binanceService = binanceService;
            _indicatorService = indicatorService;
            _newsService = newsService;
            _serviceProvider = serviceProvider;

            InitializeHighestSignalNumber();
        }

        private void InitializeHighestSignalNumber()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
                var maxNum = db.Signals.Max(s => (int?)s.SignalNumber) ?? 160;
                if (maxNum >= _nextSignalNumber) _nextSignalNumber = maxNum + 1;
            }
            catch { }
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
            var btcKlines = await _binanceService.GetKlinesAsync("BTCUSDT", "15m", 60);
            var compass = new BtcMarketCompass();
            compass.TimestampFormatted = DateTime.Now.ToString("dd.MM.yyyy | HH:mm:ss");

            if (btcKlines.Count == 0) return compass;

            var currentPrice = btcKlines.Last().Close;
            var openPrice = btcKlines.First().Open;
            compass.Price = currentPrice;
            compass.Change24h = Math.Round(((currentPrice - openPrice) / openPrice) * 100, 2);

            var indicators = _indicatorService.CalculateIndicators(btcKlines);
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

            var klines = await _binanceService.GetKlinesAsync(symbol, timeframe, 100);
            if (klines.Count < 35)
            {
                return new FuturesSignal { Symbol = symbol, Timeframe = timeframe, SignalType = "MƏLUMAT AZDIR" };
            }

            // Closed candle evaluation to prevent flickering
            var closedCandle = klines.Count >= 2 ? klines[klines.Count - 2] : klines.Last();
            var sourceCandleTime = closedCandle.Time;
            var currentPrice = klines.Last().Close;

            var btcCompass = await GetBtcCompassAsync();
            var indicators = _indicatorService.CalculateIndicators(klines, btcCompass);
            var newsSummary = await _newsService.GetNewsAndSentimentAsync();

            var reasons = new List<string>();

            // 1. BTC Macro alignment
            if (btcCompass.BullishScore >= 55)
            {
                reasons.Add($"Bitcoin Kompası: {btcCompass.Trend} ({btcCompass.BullishScore}%)");
                indicators.BtcAlignment = "Bullish";
            }
            else if (btcCompass.BullishScore <= 45)
            {
                reasons.Add($"Bitcoin Kompası: {btcCompass.Trend} ({btcCompass.BullishScore}%)");
                indicators.BtcAlignment = "Bearish";
            }
            else
            {
                indicators.BtcAlignment = "Neytral";
            }

            // 3. Section 5 Scoring Decision
            SignalDirection direction = SignalDirection.Buy;
            string determinedType = "NEYTRAL (GÖZLƏMƏ) ⚪";
            int confidence = 65;

            // Long condition (Confluence Score >= 75 and TrendScore >= 0)
            if (indicators.ConfluenceScore >= 75m && indicators.TrendScore >= 0)
            {
                direction = SignalDirection.Buy;
                determinedType = "GÜCLÜ LONG (ALIŞ) 🟢";
                confidence = Math.Clamp((int)indicators.ConfluenceScore, 78, 96);
            }
            // Short condition (Confluence Score <= 25 or bearish confluence >= 75% and TrendScore <= 0)
            else if (indicators.ConfluenceScore <= 25m && indicators.TrendScore <= 0)
            {
                direction = SignalDirection.Sell;
                determinedType = "GÜCLÜ SHORT (SATIŞ) 🔴";
                confidence = Math.Clamp((int)(100m - indicators.ConfluenceScore), 78, 96);
            }
            else if (indicators.BullishIndicatorsCount >= 9 && indicators.BullishIndicatorsCount > indicators.BearishIndicatorsCount)
            {
                direction = SignalDirection.Buy;
                determinedType = "GÜCLÜ LONG (ALIŞ) 🟢";
                confidence = 80;
            }
            else if (indicators.BearishIndicatorsCount >= 9 && indicators.BearishIndicatorsCount > indicators.BullishIndicatorsCount)
            {
                direction = SignalDirection.Sell;
                determinedType = "GÜCLÜ SHORT (SATIŞ) 🔴";
                confidence = 80;
            }

            decimal directionalConfluence = direction == SignalDirection.Sell 
                ? Math.Round(100m - indicators.ConfluenceScore, 1) 
                : Math.Round(indicators.ConfluenceScore, 1);

            // 2. Technical Findings
            reasons.Add($"Confluence Balı: {directionalConfluence}% (Trend: {indicators.TrendScore:+0.00;-0.00}, Momentum: {indicators.MomentumScore:+0.00;-0.00})");
            reasons.Add($"RSI (14): {indicators.Rsi:F1} - {indicators.RsiStatus}");
            reasons.Add($"MACD (12,26,9): Hist={indicators.MacdHist:F4} ({indicators.MacdStatus})");
            reasons.Add($"EMA Struktur (20/50): {indicators.EmaTrend} (EMA20: ${indicators.Ema20})");
            reasons.Add($"SuperTrend (10,3): {indicators.SuperTrendDirection} (Xətt: ${indicators.SuperTrend})");
            reasons.Add($"Bollinger Bands (20,2): {indicators.BollingerStatus} (Bant Eni: {indicators.BollingerBandwidth}%)");
            reasons.Add($"ADX Trend Gücü (14): {indicators.Adx:F1} ({indicators.AdxTrendStrength})");
            reasons.Add($"Həcm Analizi: {indicators.ObvTrend}, Sıçrayış: {indicators.VolumeSurgeRatio:F1}x");
            reasons.Add($"Struktur: Dəstək ${indicators.SupportLevel} | Müqavimət ${indicators.ResistanceLevel}");

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

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Check if existing signal for this candle already created
            var existingSignal = await db.Signals
                .Include(s => s.IndicatorSnapshots)
                .FirstOrDefaultAsync(s => s.Symbol == symbol && s.Timeframe == timeframe && s.SourceCandleOpenTimeUtc == sourceCandleTime);

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
                TimestampFormatted = DateTime.Now.ToString("dd.MM.yyyy | HH:mm:ss"),
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

            // Create indicator snapshots (Section 4 & 5)
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
                    db.Signals.Add(newSignal);
                    await db.SaveChangesAsync();
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
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Signals
                .Where(s => s.Status == SignalStatus.Open && !s.IsClosed)
                .OrderByDescending(s => s.GeneratedAt)
                .ToListAsync();
        }

        public async Task<List<FuturesSignal>> GetSignalHistoryAsync(int count = 25)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Signals
                .OrderByDescending(s => s.GeneratedAt)
                .Take(count)
                .ToListAsync();
        }

        public async Task<PerformanceStats> GetPerformanceStatsAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var all = await db.Signals.ToListAsync();
            var closed = all.Where(s => s.Status != SignalStatus.Open || s.IsClosed).ToList();

            var stats = new PerformanceStats
            {
                TotalSignals = all.Count,
                OpenSignals = all.Count(s => s.Status == SignalStatus.Open && !s.IsClosed),
                SuccessSignals = closed.Count(s => s.Status == SignalStatus.Success),
                FailedSignals = closed.Count(s => s.Status == SignalStatus.Failed),
                NeutralSignals = closed.Count(s => s.Status == SignalStatus.Neutral)
            };

            int decisiveTrades = stats.SuccessSignals + stats.FailedSignals;
            stats.WinRatePercent = decisiveTrades > 0 ? Math.Round(((decimal)stats.SuccessSignals / decisiveTrades) * 100, 1) : 0;

            var results = closed.Where(s => s.ResultPercent.HasValue).Select(s => s.ResultPercent!.Value).ToList();
            if (results.Count > 0)
            {
                stats.TotalNetProfitPercent = Math.Round(results.Sum(), 2);
                stats.AvgProfitPerTradePercent = Math.Round(results.Average(), 2);
                stats.BestTradePercent = Math.Round(results.Max(), 2);
                stats.WorstTradePercent = Math.Round(results.Min(), 2);
            }

            return stats;
        }
    }
}
