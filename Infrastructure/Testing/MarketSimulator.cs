using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;

namespace CryptoSense.Infrastructure.Testing
{
    public class SimulationResult
    {
        public string Symbol { get; set; } = "";
        public string Timeframe { get; set; } = "";
        public int TotalSignals { get; set; }
        public int Tp1Hits { get; set; }
        public int Tp2Hits { get; set; }
        public int Tp3Hits { get; set; }
        public int StopLossHits { get; set; }
        public int BreakevenCloses { get; set; }
        public decimal WinRatePercent => TotalSignals > 0 ? Math.Round(((decimal)(Tp1Hits + Tp2Hits + Tp3Hits) / TotalSignals) * 100, 1) : 0;
        public decimal NetProfitPercent { get; set; }
    }

    public class MarketSimulator
    {
        private readonly IMarketDataProvider _marketData;
        private readonly IIndicatorEngine _indicatorEngine;

        public MarketSimulator(IMarketDataProvider marketData, IIndicatorEngine indicatorEngine)
        {
            _marketData = marketData;
            _indicatorEngine = indicatorEngine;
        }

        public async Task<List<SimulationResult>> RunSimulationAsync(List<string> coins, string[] timeframes, int klinesCount = 120)
        {
            var results = new List<SimulationResult>();

            foreach (var sym in coins)
            {
                foreach (var tf in timeframes)
                {
                    try
                    {
                        var klines = await _marketData.GetKlinesAsync(sym, tf, klinesCount);
                        if (klines.Count < 50) continue;

                        var res = SimulateOnKlines(sym, tf, klines);
                        if (res.TotalSignals > 0)
                        {
                            results.Add(res);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Sim error {sym} {tf}: {ex.Message}");
                    }
                }
            }

            return results;
        }

        public SimulationResult SimulateOnKlines(string symbol, string timeframe, List<Kline> klines)
        {
            var result = new SimulationResult
            {
                Symbol = symbol,
                Timeframe = timeframe
            };

            int minHistory = 45;
            int maxEvaluationIndex = klines.Count - 8; // Leave room for future candle resolution

            for (int i = minHistory; i <= maxEvaluationIndex; i++)
            {
                var historySlice = klines.Take(i + 1).ToList();
                var currentCandle = historySlice.Last();
                var currentPrice = currentCandle.Close;

                var indicators = _indicatorEngine.CalculateIndicators(historySlice);

                // Check Institutional Confluence criteria
                bool isLong = (indicators.ConfluenceScore >= CryptoSense.Domain.Common.BotConstants.Thresholds.MinConfluence1h4h && indicators.SuperTrendVote == IndicatorVote.Bullish && indicators.MacdHist > 0 && indicators.Rsi >= 38 && indicators.Rsi <= 68);
                bool isShort = (indicators.ConfluenceScore <= (100m - CryptoSense.Domain.Common.BotConstants.Thresholds.MinConfluence1h4h) && indicators.SuperTrendVote == IndicatorVote.Bearish && indicators.MacdHist < 0 && indicators.Rsi >= 32 && indicators.Rsi <= 62);

                if (!isLong && !isShort) continue;

                // Step sizing based on ATR and protective volatility buffer
                decimal minMultiplier = timeframe switch
                {
                    "1m" => 0.012m,
                    "3m" => 0.015m,
                    "5m" => 0.018m,
                    "15m" => 0.024m,
                    "1h" => 0.035m,
                    _ => 0.018m
                };
                decimal atr = indicators.Atr > 0 ? indicators.Atr : (currentPrice * minMultiplier);
                decimal risk = Math.Max(atr * 1.5m, currentPrice * minMultiplier);

                decimal entryPrice = currentPrice;
                decimal tp1 = isLong ? entryPrice + (risk * 1.15m) : entryPrice - (risk * 1.15m);
                decimal tp2 = isLong ? entryPrice + (risk * 1.85m) : entryPrice - (risk * 1.85m);
                decimal tp3 = isLong ? entryPrice + (risk * 2.80m) : entryPrice - (risk * 2.80m);
                decimal stopLoss = isLong ? entryPrice - risk : entryPrice + risk;

                result.TotalSignals++;

                // Forward Simulation (Walk forward through dynamic candle duration window)
                bool tp1Reached = false;
                bool tp2Reached = false;
                bool slReached = false;
                bool breakevenHit = false;
                decimal tradePnl = 0;

                int maxCandlesToWait = timeframe switch
                {
                    "1m" => 30,
                    "3m" => 30,
                    "5m" => 30,
                    "15m" => 24,
                    "1h" => 24,
                    _ => 20
                };
                int futureEnd = Math.Min(klines.Count - 1, i + maxCandlesToWait);
                for (int f = i + 1; f <= futureEnd; f++)
                {
                    var fc = klines[f];

                    if (isLong)
                    {
                        // Check TP1
                        if (!tp1Reached && fc.High >= tp1)
                        {
                            tp1Reached = true;
                            tradePnl = Math.Round(((tp1 - entryPrice) / entryPrice) * 100, 2);
                            stopLoss = entryPrice * 1.0005m; // Move to Breakeven with buffer
                        }

                        // Check TP2
                        if (tp1Reached && !tp2Reached && fc.High >= tp2)
                        {
                            tp2Reached = true;
                            tradePnl = Math.Round(((tp2 - entryPrice) / entryPrice) * 100, 2);
                            stopLoss = tp1; // Trailing stop to TP1
                        }

                        // Check StopLoss or Breakeven
                        if (fc.Low <= stopLoss)
                        {
                            if (tp1Reached)
                            {
                                breakevenHit = true;
                            }
                            else
                            {
                                slReached = true;
                                tradePnl = -Math.Round(((entryPrice - stopLoss) / entryPrice) * 100, 2);
                            }
                            break;
                        }
                    }
                    else // Short
                    {
                        // Check TP1
                        if (!tp1Reached && fc.Low <= tp1)
                        {
                            tp1Reached = true;
                            tradePnl = Math.Round(((entryPrice - tp1) / entryPrice) * 100, 2);
                            stopLoss = entryPrice * 0.9995m; // Move to Breakeven with buffer
                        }

                        // Check TP2
                        if (tp1Reached && !tp2Reached && fc.Low <= tp2)
                        {
                            tp2Reached = true;
                            tradePnl = Math.Round(((entryPrice - tp2) / entryPrice) * 100, 2);
                            stopLoss = tp1;
                        }

                        // Check StopLoss
                        if (fc.High >= stopLoss)
                        {
                            if (tp1Reached)
                            {
                                breakevenHit = true;
                            }
                            else
                            {
                                slReached = true;
                                tradePnl = -Math.Round(((stopLoss - entryPrice) / entryPrice) * 100, 2);
                            }
                            break;
                        }
                    }
                }

                if (tp2Reached) result.Tp2Hits++;
                else if (tp1Reached) result.Tp1Hits++;
                else if (slReached) result.StopLossHits++;
                else if (breakevenHit) result.BreakevenCloses++;
                else
                {
                    // Trade still resolving or timed out safely
                    if (tradePnl >= 0) result.Tp1Hits++;
                    else if (tradePnl >= -0.30m) result.BreakevenCloses++;
                    else result.StopLossHits++;
                }

                result.NetProfitPercent += tradePnl;

                // Advance index past resolved trade to avoid duplicate signals on identical trend
                i += 3;
            }

            return result;
        }
    }
}
