using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Models;

namespace CryptoSense.Services
{
    public class SignalEngine
    {
        private readonly BinanceFuturesService _binanceService;
        private readonly IndicatorService _indicatorService;
        private readonly NewsService _newsService;

        private static int _nextSignalNumber = 150;
        private static readonly ConcurrentDictionary<string, FuturesSignal> _activeSignals = new();
        private static readonly List<FuturesSignal> _signalHistory = new();
        private static readonly object _lock = new();
        private static readonly string _storagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "active_signals.json");

        public SignalEngine(BinanceFuturesService binanceService, IndicatorService indicatorService, NewsService newsService)
        {
            _binanceService = binanceService;
            _indicatorService = indicatorService;
            _newsService = newsService;
            LoadPersistedSignals();
        }

        private static void LoadPersistedSignals()
        {
            try
            {
                if (File.Exists(_storagePath))
                {
                    var json = File.ReadAllText(_storagePath);
                    var list = JsonSerializer.Deserialize<List<FuturesSignal>>(json);
                    if (list != null)
                    {
                        foreach (var s in list)
                        {
                            if (!s.IsClosed)
                            {
                                var key = $"{s.Symbol}_{s.Timeframe}";
                                _activeSignals[key] = s;
                            }
                            _signalHistory.Add(s);
                        }
                    }
                }
            }
            catch { }
        }

        public static void PersistSignals()
        {
            try
            {
                lock (_lock)
                {
                    var all = _signalHistory.Take(100).ToList();
                    var json = JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_storagePath, json);
                }
            }
            catch { }
        }

        public static List<FuturesSignal> GetTrackedActiveSignals()
        {
            return _activeSignals.Values.Where(s => !s.IsClosed).ToList();
        }

        public static List<FuturesSignal> GetSignalHistory(int count = 15)
        {
            lock (_lock)
            {
                return _signalHistory.OrderByDescending(s => s.GeneratedAt).Take(count).ToList();
            }
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
            if (indicators.EmaTrend.Contains("Bullish")) score += 20;
            if (indicators.EmaTrend.Contains("Bearish")) score -= 20;
            if (indicators.MacdHist > 0) score += 15;
            else score -= 15;
            if (indicators.Rsi >= 50 && indicators.Rsi <= 68) score += 15;
            else if (indicators.Rsi < 48) score -= 15;

            compass.BullishScore = Math.Clamp(score, 5, 95);
            if (compass.BullishScore >= 60)
            {
                compass.Trend = "Y\u00DCKS\u018FL\u0130\u015E (BULLISH) \U0001F7E2";
                compass.Summary = "Bitcoin 15m/1h strukturu g\u00FCcl\u00FCd\u00FCr v\u0259 dinamik d\u0259st\u0259k s\u0259viyy\u0259si \u00FCz\u0259rind\u0259dir. Long \u0259m\u0259liyyatlar\u0131na \u00FCst\u00FCnl\u00FCk verilir.";
            }
            else if (compass.BullishScore <= 40)
            {
                compass.Trend = "EN\u0130\u015E (BEARISH) \U0001F534";
                compass.Summary = "Bitcoin sat\u0131\u015F t\u0259zyiqi alt\u0131ndad\u0131r v\u0259 EMA x\u0259tl\u0259rinin alt\u0131ndad\u0131r. Short \u0259m\u0259liyyatlar\u0131na \u00FCst\u00FCnl\u00FCk verilir.";
            }
            else
            {
                compass.Trend = "NEYTRAL (YAN H\u018FR\u018FK\u018FT) \u26AA";
                compass.Summary = "Bitcoin yan h\u0259r\u0259k\u0259td\u0259dir (konsolidasiya). Q\u0131sa scalping v\u0259 d\u0259qiq Stop-Loss t\u00F6vsiy\u0259 olunur.";
            }

            return compass;
        }

        public async Task<FuturesSignal> AnalyzeCoinAsync(string symbol, string timeframe = "15m")
        {
            symbol = symbol.ToUpper();
            if (!symbol.EndsWith("USDT")) symbol += "USDT";

            var cacheKey = $"{symbol}_{timeframe}";

            var klines = await _binanceService.GetKlinesAsync(symbol, timeframe, 100);
            if (klines.Count < 35)
            {
                return new FuturesSignal { Symbol = symbol, Timeframe = timeframe, SignalType = "MELUMAT AZDIR" };
            }

            var currentPrice = klines.Last().Close;
            var indicators = _indicatorService.CalculateIndicators(klines);
            var btcCompass = await GetBtcCompassAsync();
            var newsSummary = await _newsService.GetNewsAndSentimentAsync();

            var reasons = new List<string>();

            // 1. BTC Macro alignment
            if (btcCompass.BullishScore >= 55)
            {
                reasons.Add($"Bitcoin Kompas\u0131: {btcCompass.Trend} ({btcCompass.BullishScore}%)");
                indicators.BtcAlignment = "Bullish";
            }
            else if (btcCompass.BullishScore <= 45)
            {
                reasons.Add($"Bitcoin Kompas\u0131: {btcCompass.Trend} ({btcCompass.BullishScore}%)");
                indicators.BtcAlignment = "Bearish";
            }
            else
            {
                indicators.BtcAlignment = "Neytral";
            }

            // 2. Add Top Indicator Technical Findings
            reasons.Add($"RSI (14): {indicators.Rsi:F1} - {indicators.RsiStatus}");
            reasons.Add($"Stochastic RSI: K={indicators.StochRsiK:F1}, D={indicators.StochRsiD:F1} ({indicators.StochStatus})");
            reasons.Add($"MACD (12,26,9): Hist={indicators.MacdHist:F4} ({indicators.MacdStatus})");
            reasons.Add($"EMA Struktur (20/50): {indicators.EmaTrend} (EMA20: ${indicators.Ema20:F4})");
            reasons.Add($"SuperTrend (10,3): {indicators.SuperTrendDirection} (X\u0259tt: ${indicators.SuperTrend:F4})");
            reasons.Add($"Bollinger Bands (20,2): {indicators.BollingerStatus} (Bant Eni: {indicators.BollingerBandwidth}%)");
            reasons.Add($"ADX Trend G\u00FCc\u00FC (14): {indicators.Adx:F1} ({indicators.AdxTrendStrength})");
            reasons.Add($"H\u0259cm Analizi: {indicators.ObvTrend}, S\u0131\u00E7ray\u0131\u015F: {indicators.VolumeSurgeRatio:F1}x");
            reasons.Add($"Struktur: D\u0259st\u0259k ${indicators.SupportLevel:F4} | M\u00FCqavim\u0259t ${indicators.ResistanceLevel:F4}");

            // Quantitative Confluence Logic (No fake signals!)
            string determinedType = "NEYTRAL (G\u00D6ZL\u018FM\u018F) \u26AA";
            int confidence = 60;

            if (indicators.BullishIndicatorsCount >= 11 && indicators.BullishIndicatorsCount > indicators.BearishIndicatorsCount)
            {
                determinedType = "G\u00DCCL\u00DC LONG (ALI\u015E) \U0001F7E2";
                confidence = Math.Clamp(85 + (indicators.BullishIndicatorsCount - 10) * 2, 90, 96);
            }
            else if (indicators.BearishIndicatorsCount >= 11 && indicators.BearishIndicatorsCount > indicators.BullishIndicatorsCount)
            {
                determinedType = "G\u00DCCL\u00DC SHORT (SATI\u015E) \U0001F534";
                confidence = Math.Clamp(85 + (indicators.BearishIndicatorsCount - 10) * 2, 90, 96);
            }
            else if (indicators.BullishIndicatorsCount >= 9 && indicators.BullishIndicatorsCount > indicators.BearishIndicatorsCount)
            {
                determinedType = "G\u00DCCL\u00DC LONG (ALI\u015E) \U0001F7E2";
                confidence = 90;
            }
            else if (indicators.BearishIndicatorsCount >= 9 && indicators.BearishIndicatorsCount > indicators.BullishIndicatorsCount)
            {
                determinedType = "G\u00DCCL\u00DC SHORT (SATI\u015E) \U0001F534";
                confidence = 90;
            }
            else
            {
                determinedType = "NEYTRAL (G\u00D6ZL\u018FM\u018F) \u26AA";
                confidence = 65;
            }

            // Check if active unclosed signal already exists in cache
            if (_activeSignals.TryGetValue(cacheKey, out var existingSignal) && 
                existingSignal.SignalType == determinedType && 
                !existingSignal.IsClosed &&
                DateTime.UtcNow - existingSignal.GeneratedAt < TimeSpan.FromMinutes(45))
            {
                existingSignal.CurrentPrice = currentPrice;
                existingSignal.Indicators = indicators;
                existingSignal.BtcCompass = btcCompass;
                return existingSignal;
            }

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

            int sigNumber = Interlocked.Increment(ref _nextSignalNumber);

            var newSignal = new FuturesSignal
            {
                SignalNumber = sigNumber,
                Id = Guid.NewGuid().ToString("N"),
                Symbol = symbol,
                CurrentPrice = currentPrice,
                Timeframe = timeframe,
                SignalType = determinedType,
                Confidence = confidence,
                AnalysisReasons = reasons,
                Indicators = indicators,
                BtcCompass = btcCompass,
                NewsSentimentImpact = newsSummary.Status,
                GeneratedAt = DateTime.UtcNow,
                TimestampFormatted = DateTime.Now.ToString("dd.MM.yyyy | HH:mm:ss"),
                OutcomeStatus = "AKT\u0130V \U0001F7E1"
            };

            var isLong = determinedType.Contains("LONG");
            if (isLong)
            {
                newSignal.EntryLow = Math.Round(currentPrice * 0.998m, 4);
                newSignal.EntryHigh = Math.Round(currentPrice * 1.002m, 4);
                newSignal.TakeProfit1 = Math.Round(currentPrice + (atr * 1.0m), 4);
                newSignal.TakeProfit2 = Math.Round(currentPrice + (atr * 1.8m), 4);
                newSignal.TakeProfit3 = Math.Round(currentPrice + (atr * 2.8m), 4);
                newSignal.StopLoss = Math.Round(currentPrice - (atr * 1.2m), 4);
            }
            else
            {
                newSignal.EntryLow = Math.Round(currentPrice * 0.998m, 4);
                newSignal.EntryHigh = Math.Round(currentPrice * 1.002m, 4);
                newSignal.TakeProfit1 = Math.Round(currentPrice - (atr * 1.0m), 4);
                newSignal.TakeProfit2 = Math.Round(currentPrice - (atr * 1.8m), 4);
                newSignal.TakeProfit3 = Math.Round(currentPrice - (atr * 2.8m), 4);
                newSignal.StopLoss = Math.Round(currentPrice + (atr * 1.2m), 4);
            }

            if (newSignal.SignalType.Contains("LONG") || newSignal.SignalType.Contains("SHORT"))
            {
                _activeSignals[cacheKey] = newSignal;
                lock (_lock)
                {
                    _signalHistory.Insert(0, newSignal);
                    if (_signalHistory.Count > 100) _signalHistory.RemoveAt(_signalHistory.Count - 1);
                }
                PersistSignals();
            }

            return newSignal;
        }
    }
}