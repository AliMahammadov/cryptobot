using System;
using System.Collections.Generic;
using System.Linq;
using CryptoSense.Models;

namespace CryptoSense.Services
{
    public class IndicatorService
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
                if (klines[i].Low > klines[i - 2].High)
                {
                    res.HasBullishFvg = true;
                }
                if (klines[i].High < klines[i - 2].Low)
                {
                    res.HasBearishFvg = true;
                }
            }

            // 17. Multi-Category Confluence Scoring (Section 5 Specification)
            // Category 1: Trend (Weight 0.35)
            decimal emaScore = res.EmaVote == IndicatorVote.Bullish ? 1.0m : (res.EmaVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal adxScore = res.AdxVote == IndicatorVote.Bullish ? 1.0m : (res.AdxVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal superTrendScore = res.SuperTrendVote == IndicatorVote.Bullish ? 1.0m : -1.0m;
            res.TrendScore = Math.Round((emaScore * 0.45m) + (adxScore * 0.30m) + (superTrendScore * 0.25m), 3);

            // Category 2: Momentum (Weight 0.30)
            decimal rsiScore = res.RsiVote == IndicatorVote.Bullish ? 1.0m : (res.RsiVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal macdScore = res.MacdVote == IndicatorVote.Bullish ? 1.0m : (res.MacdVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal stochScore = res.StochVote == IndicatorVote.Bullish ? 1.0m : (res.StochVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            res.MomentumScore = Math.Round((rsiScore * 0.35m) + (macdScore * 0.45m) + (stochScore * 0.20m), 3);

            // Category 3: Volatility (Weight 0.15)
            decimal bbScore = res.BollingerVote == IndicatorVote.Bullish ? 1.0m : (res.BollingerVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            res.VolatilityScore = bbScore;

            // Category 4: Volume (Weight 0.20)
            decimal obvScore = res.ObvVote == IndicatorVote.Bullish ? 1.0m : (res.ObvVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal volScore = res.VolumeVote == IndicatorVote.Bullish ? 1.0m : (res.VolumeVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal vwapScore = res.VwapVote == IndicatorVote.Bullish ? 1.0m : -1.0m;
            res.VolumeScore = Math.Round((obvScore * 0.40m) + (volScore * 0.30m) + (vwapScore * 0.30m), 3);

            // Raw Weighted Confluence Score (-1.0 to +1.0)
            decimal rawScore = (res.TrendScore * 0.35m) + (res.MomentumScore * 0.30m) + (res.VolatilityScore * 0.15m) + (res.VolumeScore * 0.20m);

            // Category 5: Multi-Timeframe (MTF) & BTC Compass Factor
            decimal mtfFactor = 1.0m;
            if (btcCompass != null)
            {
                if (rawScore > 0 && btcCompass.BullishScore >= 55) mtfFactor = 1.0m;
                else if (rawScore < 0 && btcCompass.BullishScore <= 45) mtfFactor = 1.0m;
                else mtfFactor = 0.85m;
            }
            res.MtfFactor = mtfFactor;

            decimal finalScore = rawScore * mtfFactor;
            // Scale -1..+1 to 0..100
            res.ConfluenceScore = Math.Round(Math.Clamp(((finalScore + 1.0m) / 2.0m) * 100m, 5m, 98m), 1);

            // Counts of bullish/bearish indicators
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
                decimal currentGain = diff >= 0 ? diff : 0;
                decimal currentLoss = diff < 0 ? Math.Abs(diff) : 0;

                avgGain = ((avgGain * (period - 1)) + currentGain) / period;
                avgLoss = ((avgLoss * (period - 1)) + currentLoss) / period;
            }

            if (avgLoss == 0) return 100;
            decimal rs = avgGain / avgLoss;
            return Math.Round(100 - (100 / (1 + rs)), 2);
        }

        public (decimal K, decimal D) CalculateStochRsi(List<decimal> prices, int rsiPeriod = 14, int stochPeriod = 14, int kPeriod = 3, int dPeriod = 3)
        {
            if (prices.Count < rsiPeriod + stochPeriod) return (50, 50);

            var rsiSeries = new List<decimal>();
            for (int i = rsiPeriod; i < prices.Count; i++)
            {
                var subList = prices.Take(i + 1).ToList();
                rsiSeries.Add(CalculateRsi(subList, rsiPeriod));
            }

            if (rsiSeries.Count < stochPeriod) return (50, 50);

            var rawStoch = new List<decimal>();
            for (int i = stochPeriod - 1; i < rsiSeries.Count; i++)
            {
                var window = rsiSeries.Skip(i - stochPeriod + 1).Take(stochPeriod).ToList();
                decimal minRsi = window.Min();
                decimal maxRsi = window.Max();
                decimal currentRsi = window.Last();

                decimal stoch = (maxRsi - minRsi) == 0 ? 50 : ((currentRsi - minRsi) / (maxRsi - minRsi)) * 100;
                rawStoch.Add(stoch);
            }

            if (rawStoch.Count < kPeriod) return (50, 50);
            decimal k = rawStoch.Skip(rawStoch.Count - kPeriod).Average();

            decimal d = k;
            if (rawStoch.Count >= kPeriod + dPeriod)
            {
                var kSeries = new List<decimal>();
                for (int i = kPeriod - 1; i < rawStoch.Count; i++)
                {
                    kSeries.Add(rawStoch.Skip(i - kPeriod + 1).Take(kPeriod).Average());
                }
                d = kSeries.Skip(Math.Max(0, kSeries.Count - dPeriod)).Average();
            }

            return (Math.Round(k, 2), Math.Round(d, 2));
        }

        public (decimal Macd, decimal Signal, decimal Hist) CalculateMacd(List<decimal> prices, int fast = 12, int slow = 26, int signal = 9)
        {
            if (prices.Count < slow + signal) return (0, 0, 0);

            var macdLine = new List<decimal>();
            for (int i = slow; i <= prices.Count; i++)
            {
                var sub = prices.Take(i).ToList();
                var fastEma = CalculateEma(sub, fast);
                var slowEma = CalculateEma(sub, slow);
                macdLine.Add(fastEma - slowEma);
            }

            var signalLine = CalculateEma(macdLine, signal);
            var currentMacd = macdLine.Last();
            var hist = currentMacd - signalLine;

            return (Math.Round(currentMacd, 6), Math.Round(signalLine, 6), Math.Round(hist, 6));
        }

        public (decimal Upper, decimal Lower, decimal Bandwidth) CalculateBollingerBands(List<decimal> prices, int period = 20, decimal multiplier = 2)
        {
            if (prices.Count < period) return (0, 0, 0);

            var window = prices.Skip(prices.Count - period).Take(period).ToList();
            decimal sma = window.Average();
            decimal sumSquares = window.Sum(p => (p - sma) * (p - sma));
            decimal stdDev = (decimal)Math.Sqrt((double)(sumSquares / period));

            decimal upper = sma + (stdDev * multiplier);
            decimal lower = sma - (stdDev * multiplier);
            decimal bandwidth = sma > 0 ? Math.Round(((upper - lower) / sma) * 100, 2) : 0;

            return (Math.Round(upper, 6), Math.Round(lower, 6), bandwidth);
        }

        public decimal CalculateAtr(List<Kline> klines, int period = 14)
        {
            if (klines.Count < period + 1) return 0;

            var trList = new List<decimal>();
            for (int i = 1; i < klines.Count; i++)
            {
                var h = klines[i].High;
                var l = klines[i].Low;
                var prevC = klines[i - 1].Close;

                var tr = Math.Max(h - l, Math.Max(Math.Abs(h - prevC), Math.Abs(l - prevC)));
                trList.Add(tr);
            }

            decimal atr = trList.Take(period).Average();
            for (int i = period; i < trList.Count; i++)
            {
                atr = ((atr * (period - 1)) + trList[i]) / period;
            }

            return Math.Round(atr, 6);
        }

        public (decimal Adx, decimal PlusDi, decimal MinusDi) CalculateAdx(List<Kline> klines, int period = 14)
        {
            if (klines.Count < period * 2) return (20, 20, 20);

            var plusDm = new List<decimal>();
            var minusDm = new List<decimal>();
            var trList = new List<decimal>();

            for (int i = 1; i < klines.Count; i++)
            {
                var hDiff = klines[i].High - klines[i - 1].High;
                var lDiff = klines[i - 1].Low - klines[i].Low;

                plusDm.Add((hDiff > lDiff && hDiff > 0) ? hDiff : 0);
                minusDm.Add((lDiff > hDiff && lDiff > 0) ? lDiff : 0);

                var tr = Math.Max(klines[i].High - klines[i].Low, Math.Max(Math.Abs(klines[i].High - klines[i - 1].Close), Math.Abs(klines[i].Low - klines[i - 1].Close)));
                trList.Add(tr);
            }

            decimal trSmooth = trList.Take(period).Sum();
            decimal plusDmSmooth = plusDm.Take(period).Sum();
            decimal minusDmSmooth = minusDm.Take(period).Sum();

            var dxList = new List<decimal>();

            for (int i = period; i < trList.Count; i++)
            {
                trSmooth = trSmooth - (trSmooth / period) + trList[i];
                plusDmSmooth = plusDmSmooth - (plusDmSmooth / period) + plusDm[i];
                minusDmSmooth = minusDmSmooth - (minusDmSmooth / period) + minusDm[i];

                decimal pDi = trSmooth == 0 ? 0 : (plusDmSmooth / trSmooth) * 100;
                decimal mDi = trSmooth == 0 ? 0 : (minusDmSmooth / trSmooth) * 100;

                decimal dx = (pDi + mDi) == 0 ? 0 : (Math.Abs(pDi - mDi) / (pDi + mDi)) * 100;
                dxList.Add(dx);
            }

            if (dxList.Count < period) return (20, 20, 20);
            decimal adx = dxList.Skip(dxList.Count - period).Average();

            decimal lastPdi = trSmooth == 0 ? 0 : (plusDmSmooth / trSmooth) * 100;
            decimal lastMdi = trSmooth == 0 ? 0 : (minusDmSmooth / trSmooth) * 100;

            return (Math.Round(adx, 2), Math.Round(lastPdi, 2), Math.Round(lastMdi, 2));
        }

        public decimal CalculateCci(List<Kline> klines, int period = 20)
        {
            if (klines.Count < period) return 0;
            var window = klines.Skip(klines.Count - period).Take(period).ToList();
            var tpList = window.Select(k => (k.High + k.Low + k.Close) / 3).ToList();
            decimal tpSma = tpList.Average();
            decimal meanDev = tpList.Sum(tp => Math.Abs(tp - tpSma)) / period;

            if (meanDev == 0) return 0;
            decimal cci = (tpList.Last() - tpSma) / (0.015m * meanDev);
            return Math.Round(cci, 2);
        }

        public decimal CalculateWilliamsR(List<Kline> klines, int period = 14)
        {
            if (klines.Count < period) return -50;
            var window = klines.Skip(klines.Count - period).Take(period).ToList();
            decimal highestHigh = window.Max(k => k.High);
            decimal lowestLow = window.Min(k => k.Low);
            decimal lastClose = window.Last().Close;

            if (highestHigh == lowestLow) return -50;
            decimal wr = ((highestHigh - lastClose) / (highestHigh - lowestLow)) * -100;
            return Math.Round(wr, 2);
        }

        public (decimal Value, bool IsBullish) CalculateSuperTrend(List<Kline> klines, int period = 10, decimal multiplier = 3.0m)
        {
            if (klines.Count < period + 1) return (0, true);

            var atr = CalculateAtr(klines, period);
            var last = klines.Last();
            var hl2 = (last.High + last.Low) / 2;

            var upperBand = hl2 + (multiplier * atr);
            var lowerBand = hl2 - (multiplier * atr);

            bool isBullish = last.Close >= hl2;
            decimal superTrendVal = isBullish ? lowerBand : upperBand;
            return (Math.Round(superTrendVal, 6), isBullish);
        }

        public decimal CalculateVwap(List<Kline> klines)
        {
            if (klines.Count == 0) return 0;
            decimal cumVolume = 0;
            decimal cumPriceVol = 0;

            foreach (var k in klines)
            {
                decimal typPrice = (k.High + k.Low + k.Close) / 3;
                cumPriceVol += typPrice * k.Volume;
                cumVolume += k.Volume;
            }

            return cumVolume > 0 ? Math.Round(cumPriceVol / cumVolume, 6) : klines.Last().Close;
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
