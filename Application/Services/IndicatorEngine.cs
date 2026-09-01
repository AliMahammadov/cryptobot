using System;
using System.Collections.Generic;
using System.Linq;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;

namespace CryptoSense.Application.Services
{
    public class IndicatorEngine : IIndicatorEngine
    {
        public IndicatorResult CalculateIndicators(List<Kline> klines, BtcMarketCompass? btcCompass = null)
        {
            var res = new IndicatorResult();
            if (klines == null || klines.Count < 35) return res;

            var closes = klines.Select(k => k.Close).ToList();
            var highs = klines.Select(k => k.High).ToList();
            var lows = klines.Select(k => k.Low).ToList();
            var volumes = klines.Select(k => k.Volume).ToList();
            var lastClose = closes.Last();
            var lastOpen = klines.Last().Open;

            // 1. RSI (14)
            res.Rsi = CalculateRsi(closes, 14);
            if (res.Rsi <= 32)
            {
                res.RsiStatus = "Həddindən Artıq Satış (Oversold Dip)";
                res.RsiVote = IndicatorVote.Bullish;
            }
            else if (res.Rsi >= 68)
            {
                res.RsiStatus = "Həddindən Artıq Alış (Overbought Top)";
                res.RsiVote = IndicatorVote.Bearish;
            }
            else if (res.Rsi >= 50)
            {
                res.RsiStatus = "Müsbət Alış Zonasında";
                res.RsiVote = IndicatorVote.Bullish;
            }
            else
            {
                res.RsiStatus = "Satış Meylli";
                res.RsiVote = IndicatorVote.Bearish;
            }

            // 2. Stochastic RSI (14, 14, 3, 3)
            var (stochK, stochD) = CalculateStochRsi(closes, 14, 3, 3);
            res.StochRsiK = stochK;
            res.StochRsiD = stochD;
            if (stochK <= 20 && stochK > stochD)
            {
                res.StochStatus = "Bullish Cross (Dipdən Qayıdış)";
                res.StochVote = IndicatorVote.Bullish;
            }
            else if (stochK >= 80 && stochK < stochD)
            {
                res.StochStatus = "Bearish Cross (Zirvədən Düzəliş)";
                res.StochVote = IndicatorVote.Bearish;
            }
            else if (stochK > stochD)
            {
                res.StochStatus = "Alış İmpulsu";
                res.StochVote = IndicatorVote.Bullish;
            }
            else
            {
                res.StochStatus = "Satış İmpulsu";
                res.StochVote = IndicatorVote.Bearish;
            }

            // 3. MACD (12, 26, 9)
            var (macd, signal, hist) = CalculateMacd(closes);
            res.Macd = macd;
            res.MacdSignal = signal;
            res.MacdHist = hist;
            if (hist > 0 && macd > signal)
            {
                res.MacdStatus = "Güclü Bullish Momentum";
                res.MacdVote = IndicatorVote.Bullish;
            }
            else if (hist < 0 && macd < signal)
            {
                res.MacdStatus = "Güclü Bearish Momentum";
                res.MacdVote = IndicatorVote.Bearish;
            }
            else
            {
                res.MacdStatus = "Kəsişmə Ərəfəsində";
                res.MacdVote = IndicatorVote.Neutral;
            }

            // 4. EMAs (9, 20, 50, 200)
            res.Ema9 = CalculateEma(closes, 9);
            res.Ema20 = CalculateEma(closes, 20);
            res.Ema50 = CalculateEma(closes, 50);
            res.Ema200 = klines.Count >= 200 ? CalculateEma(closes, 200) : res.Ema50;

            if (res.Ema20 > res.Ema50 && lastClose > res.Ema20)
            {
                res.EmaTrend = "Güclü Yüksəliş (Bullish)";
                res.EmaVote = IndicatorVote.Bullish;
            }
            else if (res.Ema20 > res.Ema50)
            {
                res.EmaTrend = "Müsbət Trend";
                res.EmaVote = IndicatorVote.Bullish;
            }
            else if (res.Ema20 < res.Ema50 && lastClose < res.Ema20)
            {
                res.EmaTrend = "Güclü Eniş (Bearish)";
                res.EmaVote = IndicatorVote.Bearish;
            }
            else
            {
                res.EmaTrend = "Mənfi Trend";
                res.EmaVote = IndicatorVote.Bearish;
            }

            // 5. SMA 20
            res.Sma20 = closes.Skip(Math.Max(0, closes.Count - 20)).Average();

            // 6. Bollinger Bands (20, 2)
            var (bUpper, bLower, bBandwidth) = CalculateBollingerBands(closes, 20, 2m);
            res.BollingerUpper = bUpper;
            res.BollingerLower = bLower;
            res.BollingerMiddle = res.Sma20;
            res.BollingerBandwidth = bBandwidth;
            if (lastClose <= bLower)
            {
                res.BollingerStatus = "Aşağı Band Toxunuşu (Alış Reaksiyası)";
                res.BollingerVote = IndicatorVote.Bullish;
            }
            else if (lastClose >= bUpper)
            {
                res.BollingerStatus = "Yuxarı Band Toxunuşu (Satış Reaksiyası)";
                res.BollingerVote = IndicatorVote.Bearish;
            }
            else if (lastClose > res.BollingerMiddle)
            {
                res.BollingerStatus = "Orta Xətt Üzərində (Müsbət)";
                res.BollingerVote = IndicatorVote.Bullish;
            }
            else
            {
                res.BollingerStatus = "Orta Xətt Altında (Mənfi)";
                res.BollingerVote = IndicatorVote.Bearish;
            }

            // 7. ATR (14)
            res.Atr = CalculateAtr(klines, 14);

            // 8. ADX (14)
            var (adx, pDi, mDi) = CalculateAdx(klines, 14);
            res.Adx = adx;
            res.PlusDi = pDi;
            res.MinusDi = mDi;
            if (adx >= 22 && pDi > mDi)
            {
                res.AdxTrendStrength = "Güclü Yüksəliş Trendi";
                res.AdxVote = IndicatorVote.Bullish;
            }
            else if (adx >= 22 && mDi > pDi)
            {
                res.AdxTrendStrength = "Güclü Düşüş Trendi";
                res.AdxVote = IndicatorVote.Bearish;
            }
            else
            {
                res.AdxTrendStrength = "Zəif / Konsolidasiya";
                res.AdxVote = IndicatorVote.Neutral;
            }

            // 9. CCI (20)
            res.Cci = CalculateCci(klines, 20);
            if (res.Cci <= -100)
            {
                res.CciStatus = "Dərin Satış Zonasında";
                res.CciVote = IndicatorVote.Bullish;
            }
            else if (res.Cci >= 100)
            {
                res.CciStatus = "Güclü Alış Zonasında";
                res.CciVote = IndicatorVote.Bearish;
            }
            else if (res.Cci > 0)
            {
                res.CciStatus = "Müsbət İmpuls";
                res.CciVote = IndicatorVote.Bullish;
            }
            else
            {
                res.CciStatus = "Mənfi İmpuls";
                res.CciVote = IndicatorVote.Bearish;
            }

            // 10. Williams %R (14)
            res.WilliamsR = CalculateWilliamsR(klines, 14);
            if (res.WilliamsR <= -80)
            {
                res.WilliamsRStatus = "Aşağı Hadisə (Dibin Seçimi)";
                res.WilliamsRVote = IndicatorVote.Bullish;
            }
            else if (res.WilliamsR >= -20)
            {
                res.WilliamsRStatus = "Zirvə Hadisəsi";
                res.WilliamsRVote = IndicatorVote.Bearish;
            }
            else
            {
                res.WilliamsRStatus = "Neytral";
                res.WilliamsRVote = IndicatorVote.Neutral;
            }

            // 11. SuperTrend (10, 3)
            var (superTrend, isBullish) = CalculateSuperTrend(klines, 10, 3.0m);
            res.SuperTrend = superTrend;
            res.SuperTrendDirection = isBullish ? "YÜKSƏLİŞ (BULLISH) 🟢" : "ENİŞ (BEARISH) 🔴";
            res.SuperTrendVote = isBullish ? IndicatorVote.Bullish : IndicatorVote.Bearish;

            // 12. VWAP
            res.Vwap = CalculateVwap(klines);
            res.VwapStatus = lastClose >= res.Vwap ? "VWAP Üzərində (Alıcı Tərəf)" : "VWAP Altında (Satıcı Tərəf)";
            res.VwapVote = lastClose >= res.Vwap ? IndicatorVote.Bullish : IndicatorVote.Bearish;

            // 13. OBV
            res.Obv = CalculateObv(klines);
            res.ObvTrend = res.Obv >= 0 ? "Həcm Toplanır (Akumulyasiya)" : "Həcm Çıxır (Distribusiya)";
            res.ObvVote = res.Obv >= 0 ? IndicatorVote.Bullish : IndicatorVote.Bearish;

            // 14. Volume Surge (20)
            res.VolumeEma20 = CalculateEma(volumes, 20);
            res.VolumeSurgeRatio = res.VolumeEma20 > 0 ? Math.Round(volumes.Last() / res.VolumeEma20, 2) : 1.0m;
            res.IsHighVolume = res.VolumeSurgeRatio >= 1.3m;
            if (res.IsHighVolume)
            {
                res.VolumeVote = lastClose >= lastOpen ? IndicatorVote.Bullish : IndicatorVote.Bearish;
            }
            else
            {
                res.VolumeVote = IndicatorVote.Neutral;
            }

            // 15. Support & Resistance Pivots
            var recentLows = lows.Skip(Math.Max(0, lows.Count - 35)).ToList();
            var recentHighs = highs.Skip(Math.Max(0, highs.Count - 35)).ToList();
            res.SupportLevel = Math.Round(recentLows.OrderBy(l => l).Take(3).Average(), 4);
            res.ResistanceLevel = Math.Round(recentHighs.OrderByDescending(h => h).Take(3).Average(), 4);
            res.PivotPoint = Math.Round((highs.Last() + lows.Last() + closes.Last()) / 3, 4);

            // 16. Fair Value Gaps (FVG)
            if (klines.Count >= 5)
            {
                var i = klines.Count - 1;
                if (klines[i].Low > klines[i - 2].High) res.HasBullishFvg = true;
                if (klines[i].High < klines[i - 2].Low) res.HasBearishFvg = true;
            }

            // 17. Multi-Category Confluence Scoring (Section 5 Specification)
            decimal emaScore = res.EmaVote == IndicatorVote.Bullish ? 1.0m : (res.EmaVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal adxScore = res.AdxVote == IndicatorVote.Bullish ? 1.0m : (res.AdxVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal superTrendScore = res.SuperTrendVote == IndicatorVote.Bullish ? 1.0m : -1.0m;
            res.TrendScore = Math.Round((emaScore * 0.45m) + (adxScore * 0.30m) + (superTrendScore * 0.25m), 3);

            decimal rsiScore = res.RsiVote == IndicatorVote.Bullish ? 1.0m : (res.RsiVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal macdScore = res.MacdVote == IndicatorVote.Bullish ? 1.0m : (res.MacdVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal stochScore = res.StochVote == IndicatorVote.Bullish ? 1.0m : (res.StochVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            res.MomentumScore = Math.Round((rsiScore * 0.35m) + (macdScore * 0.45m) + (stochScore * 0.20m), 3);

            decimal bbScore = res.BollingerVote == IndicatorVote.Bullish ? 1.0m : (res.BollingerVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            res.VolatilityScore = bbScore;

            decimal obvScore = res.ObvVote == IndicatorVote.Bullish ? 1.0m : (res.ObvVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal volScore = res.VolumeVote == IndicatorVote.Bullish ? 1.0m : (res.VolumeVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal vwapScore = res.VwapVote == IndicatorVote.Bullish ? 1.0m : -1.0m;
            res.VolumeScore = Math.Round((obvScore * 0.40m) + (volScore * 0.30m) + (vwapScore * 0.30m), 3);

            decimal rawScore = (res.TrendScore * 0.35m) + (res.MomentumScore * 0.30m) + (res.VolatilityScore * 0.15m) + (res.VolumeScore * 0.20m);

            decimal mtfFactor = 1.0m;
            if (btcCompass != null)
            {
                if (rawScore > 0 && btcCompass.BullishScore >= 55) mtfFactor = 1.0m;
                else if (rawScore < 0 && btcCompass.BullishScore <= 45) mtfFactor = 1.0m;
                else mtfFactor = 0.85m;
            }
            res.MtfFactor = mtfFactor;

            decimal finalScore = rawScore * mtfFactor;
            res.ConfluenceScore = Math.Round(Math.Clamp(((finalScore + 1.0m) / 2.0m) * 100m, 5m, 98m), 1);

            var votes = new[] { res.RsiVote, res.StochVote, res.MacdVote, res.EmaVote, res.BollingerVote, res.AdxVote, res.CciVote, res.WilliamsRVote, res.SuperTrendVote, res.VwapVote, res.ObvVote, res.VolumeVote };
            res.BullishIndicatorsCount = votes.Count(v => v == IndicatorVote.Bullish);
            res.BearishIndicatorsCount = votes.Count(v => v == IndicatorVote.Bearish);
            res.NeutralIndicatorsCount = votes.Count(v => v == IndicatorVote.Neutral);

            return res;
        }

        public decimal CalculateEma(List<decimal> prices, int period)
        {
            if (prices.Count < period) return prices.LastOrDefault();
            decimal multiplier = 2m / (period + 1);
            decimal ema = prices.Take(period).Average();

            for (int i = period; i < prices.Count; i++)
            {
                ema = (prices[i] - ema) * multiplier + ema;
            }
            return Math.Round(ema, 6);
        }

        public decimal CalculateRsi(List<decimal> prices, int period = 14)
        {
            if (prices.Count <= period) return 50;

            decimal gainSum = 0;
            decimal lossSum = 0;

            for (int i = 1; i <= period; i++)
            {
                decimal diff = prices[i] - prices[i - 1];
                if (diff >= 0) gainSum += diff;
                else lossSum += Math.Abs(diff);
            }

            decimal avgGain = gainSum / period;
            decimal avgLoss = lossSum / period;

            for (int i = period + 1; i < prices.Count; i++)
            {
                decimal diff = prices[i] - prices[i - 1];
                if (diff >= 0)
                {
                    avgGain = (avgGain * (period - 1) + diff) / period;
                    avgLoss = (avgLoss * (period - 1)) / period;
                }
                else
                {
                    avgGain = (avgGain * (period - 1)) / period;
                    avgLoss = (avgLoss * (period - 1) + Math.Abs(diff)) / period;
                }
            }

            if (avgLoss == 0) return 100;
            decimal rs = avgGain / avgLoss;
            return Math.Round(100 - (100 / (1 + rs)), 2);
        }

        public (decimal StochK, decimal StochD) CalculateStochRsi(List<decimal> prices, int rsiPeriod = 14, int stochPeriod = 14, int kPeriod = 3)
        {
            if (prices.Count < rsiPeriod + stochPeriod) return (50, 50);

            var rsiList = new List<decimal>();
            for (int i = rsiPeriod; i < prices.Count; i++)
            {
                var sub = prices.Take(i + 1).ToList();
                rsiList.Add(CalculateRsi(sub, rsiPeriod));
            }

            if (rsiList.Count < stochPeriod) return (50, 50);

            var stochRsiValues = new List<decimal>();
            for (int i = stochPeriod - 1; i < rsiList.Count; i++)
            {
                var window = rsiList.Skip(i - stochPeriod + 1).Take(stochPeriod).ToList();
                decimal minRsi = window.Min();
                decimal maxRsi = window.Max();
                decimal currentRsi = rsiList[i];

                decimal stochRsi = maxRsi != minRsi ? ((currentRsi - minRsi) / (maxRsi - minRsi)) * 100 : 50;
                stochRsiValues.Add(stochRsi);
            }

            decimal k = stochRsiValues.Skip(Math.Max(0, stochRsiValues.Count - kPeriod)).Average();
            decimal d = stochRsiValues.Skip(Math.Max(0, stochRsiValues.Count - (kPeriod + 2))).Take(3).Average();

            return (Math.Round(k, 2), Math.Round(d, 2));
        }

        public (decimal Macd, decimal Signal, decimal Hist) CalculateMacd(List<decimal> prices)
        {
            if (prices.Count < 26) return (0, 0, 0);

            var macdLine = new List<decimal>();
            for (int i = 26; i <= prices.Count; i++)
            {
                var sub = prices.Take(i).ToList();
                decimal ema12 = CalculateEma(sub, 12);
                decimal ema26 = CalculateEma(sub, 26);
                macdLine.Add(ema12 - ema26);
            }

            decimal currentMacd = macdLine.LastOrDefault();
            decimal signalLine = macdLine.Count >= 9 ? CalculateEma(macdLine, 9) : currentMacd;
            decimal hist = currentMacd - signalLine;

            return (Math.Round(currentMacd, 4), Math.Round(signalLine, 4), Math.Round(hist, 4));
        }

        public (decimal Upper, decimal Lower, decimal Bandwidth) CalculateBollingerBands(List<decimal> prices, int period = 20, decimal multiplier = 2m)
        {
            if (prices.Count < period) return (prices.LastOrDefault(), prices.LastOrDefault(), 0);

            var recent = prices.Skip(prices.Count - period).ToList();
            decimal sma = recent.Average();
            decimal sumSquares = recent.Sum(p => (p - sma) * (p - sma));
            decimal stdDev = (decimal)Math.Sqrt((double)(sumSquares / period));

            decimal upper = sma + (multiplier * stdDev);
            decimal lower = sma - (multiplier * stdDev);
            decimal bandwidth = sma > 0 ? ((upper - lower) / sma) * 100 : 0;

            return (Math.Round(upper, 4), Math.Round(lower, 4), Math.Round(bandwidth, 2));
        }

        public decimal CalculateAtr(List<Kline> klines, int period = 14)
        {
            if (klines.Count < period + 1) return 0;

            var trList = new List<decimal>();
            for (int i = 1; i < klines.Count; i++)
            {
                decimal high = klines[i].High;
                decimal low = klines[i].Low;
                decimal prevClose = klines[i - 1].Close;

                decimal tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
                trList.Add(tr);
            }

            if (trList.Count < period) return trList.Average();
            return Math.Round(CalculateEma(trList, period), 6);
        }

        public (decimal Adx, decimal PlusDi, decimal MinusDi) CalculateAdx(List<Kline> klines, int period = 14)
        {
            if (klines.Count < period + 2) return (20, 20, 20);

            var trList = new List<decimal>();
            var plusDmList = new List<decimal>();
            var minusDmList = new List<decimal>();

            for (int i = 1; i < klines.Count; i++)
            {
                decimal high = klines[i].High;
                decimal low = klines[i].Low;
                decimal prevHigh = klines[i - 1].High;
                decimal prevLow = klines[i - 1].Low;
                decimal prevClose = klines[i - 1].Close;

                decimal tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
                trList.Add(tr);

                decimal upMove = high - prevHigh;
                decimal downMove = prevLow - low;

                if (upMove > downMove && upMove > 0) plusDmList.Add(upMove);
                else plusDmList.Add(0);

                if (downMove > upMove && downMove > 0) minusDmList.Add(downMove);
                else minusDmList.Add(0);
            }

            decimal atr = CalculateEma(trList, period);
            if (atr == 0) return (20, 20, 20);

            decimal smoothedPlusDm = CalculateEma(plusDmList, period);
            decimal smoothedMinusDm = CalculateEma(minusDmList, period);

            decimal plusDi = (smoothedPlusDm / atr) * 100;
            decimal minusDi = (smoothedMinusDm / atr) * 100;

            decimal diSum = plusDi + minusDi;
            decimal dx = diSum > 0 ? (Math.Abs(plusDi - minusDi) / diSum) * 100 : 0;

            return (Math.Round(dx, 2), Math.Round(plusDi, 2), Math.Round(minusDi, 2));
        }

        public decimal CalculateCci(List<Kline> klines, int period = 20)
        {
            if (klines.Count < period) return 0;

            var tpList = klines.Select(k => (k.High + k.Low + k.Close) / 3).ToList();
            var recentTp = tpList.Skip(tpList.Count - period).ToList();
            decimal smaTp = recentTp.Average();

            decimal meanDev = recentTp.Sum(tp => Math.Abs(tp - smaTp)) / period;
            if (meanDev == 0) return 0;

            decimal cci = (tpList.Last() - smaTp) / (0.015m * meanDev);
            return Math.Round(cci, 2);
        }

        public decimal CalculateWilliamsR(List<Kline> klines, int period = 14)
        {
            if (klines.Count < period) return -50;

            var window = klines.Skip(klines.Count - period).ToList();
            decimal highestHigh = window.Max(k => k.High);
            decimal lowestLow = window.Min(k => k.Low);
            decimal close = klines.Last().Close;

            if (highestHigh == lowestLow) return -50;
            decimal wr = ((highestHigh - close) / (highestHigh - lowestLow)) * -100;
            return Math.Round(wr, 2);
        }

        public (decimal SuperTrend, bool IsBullish) CalculateSuperTrend(List<Kline> klines, int period = 10, decimal multiplier = 3m)
        {
            if (klines.Count < period + 1) return (klines.LastOrDefault()?.Close ?? 0, true);

            decimal atr = CalculateAtr(klines, period);
            decimal upperBand = ((klines.Last().High + klines.Last().Low) / 2) + (multiplier * atr);
            decimal lowerBand = ((klines.Last().High + klines.Last().Low) / 2) - (multiplier * atr);
            decimal lastClose = klines.Last().Close;

            bool isBullish = lastClose >= lowerBand;
            decimal val = isBullish ? lowerBand : upperBand;
            return (Math.Round(val, 4), isBullish);
        }

        public decimal CalculateVwap(List<Kline> klines)
        {
            if (klines == null || klines.Count == 0) return 0;
            decimal cumPriceVol = 0;
            decimal cumVol = 0;

            foreach (var k in klines)
            {
                decimal tp = (k.High + k.Low + k.Close) / 3;
                cumPriceVol += tp * k.Volume;
                cumVol += k.Volume;
            }

            return cumVol > 0 ? Math.Round(cumPriceVol / cumVol, 4) : klines.Last().Close;
        }

        public decimal CalculateObv(List<Kline> klines)
        {
            if (klines.Count < 2) return 0;
            decimal obv = 0;

            for (int i = 1; i < klines.Count; i++)
            {
                if (klines[i].Close > klines[i - 1].Close) obv += klines[i].Volume;
                else if (klines[i].Close < klines[i - 1].Close) obv -= klines[i].Volume;
            }

            return Math.Round(obv, 2);
        }
    }
}
