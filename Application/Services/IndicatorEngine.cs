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

            // 5. MA / SMA (20, 50)
            res.Sma20 = closes.Skip(Math.Max(0, closes.Count - 20)).Average();
            res.Sma50 = closes.Skip(Math.Max(0, closes.Count - 50)).Average();
            if (lastClose > res.Sma20 && res.Sma20 >= res.Sma50)
            {
                res.SmaTrend = "Güclü Yüksəliş (Bullish)";
                res.SmaVote = IndicatorVote.Bullish;
            }
            else if (lastClose > res.Sma20)
            {
                res.SmaTrend = "Müsbət Trend";
                res.SmaVote = IndicatorVote.Bullish;
            }
            else if (lastClose < res.Sma20 && res.Sma20 <= res.Sma50)
            {
                res.SmaTrend = "Güclü Eniş (Bearish)";
                res.SmaVote = IndicatorVote.Bearish;
            }
            else
            {
                res.SmaTrend = "Mənfi Trend";
                res.SmaVote = IndicatorVote.Bearish;
            }

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
            
            // OBV slope: son 10 vs əvvəlki 10 OBV delta
            decimal slopeNow = 0m;
            decimal obvScale = 1m;
            if (klines.Count >= 20)
            {
                var obvSeries = new List<decimal>(klines.Count);
                decimal curObv = 0m;
                obvSeries.Add(curObv);
                for (int i = 1; i < klines.Count; i++)
                {
                    if (klines[i].Close > klines[i - 1].Close) curObv += klines[i].Volume;
                    else if (klines[i].Close < klines[i - 1].Close) curObv -= klines[i].Volume;
                    obvSeries.Add(curObv);
                }
                decimal obvNow = obvSeries[^1];
                decimal obv10Ago = obvSeries[^10];
                decimal obv20Ago = obvSeries[^20];
                slopeNow = obvNow - obv10Ago;
                decimal slopePrev = obv10Ago - obv20Ago;

                var recentVols = volumes.Skip(Math.Max(0, volumes.Count - 20)).ToList();
                obvScale = (recentVols.Count > 0 ? recentVols.Average() : 1m) * 5m;
                if (obvScale <= 0) obvScale = 1m;

                res.ObvTrend = slopeNow >= slopePrev ? "Həcm Toplanır (Akumulyasiya)" : "Həcm Çıxır (Distribusiya)";
                res.ObvVote = slopeNow >= slopePrev ? IndicatorVote.Bullish : IndicatorVote.Bearish;
            }
            else
            {
                res.ObvTrend = res.Obv >= 0 ? "Həcm Toplanır (Akumulyasiya)" : "Həcm Çıxır (Distribusiya)";
                res.ObvVote = res.Obv >= 0 ? IndicatorVote.Bullish : IndicatorVote.Bearish;
            }
            res.ObvSlopeFeature = (decimal)Math.Tanh((double)(slopeNow / obvScale));

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

            // 17. Core Confluence Scoring - Continuous Mathematics & Normalized Feature Space
            static decimal Tanh(decimal x) => (decimal)Math.Tanh((double)x);
            decimal atrSafe = Math.Max(res.Atr, lastClose * 0.0001m);

            // SKOR (SMA QATILMIR):
            decimal emaSpread = Tanh(((res.Ema20 - res.Ema50) / atrSafe) / 1.5m);
            decimal priceEma = Tanh(((lastClose - res.Ema20) / atrSafe) / 1.0m);
            decimal stSign = res.SuperTrendVote == IndicatorVote.Bullish ? 1.0m : (res.SuperTrendVote == IndicatorVote.Bearish ? -1.0m : 0.0m);
            decimal stFeat = stSign * Math.Max(0.35m, Tanh(Math.Abs(lastClose - res.SuperTrend) / atrSafe));
            decimal adxMult = Math.Clamp(res.Adx / 25m, 0.45m, 1.0m);

            res.EmaSpreadFeature = emaSpread;
            res.TrendScore = Math.Clamp(((0.45m * emaSpread) + (0.25m * priceEma) + (0.30m * stFeat)) * adxMult, -1m, 1m);

            decimal rsiFeat = Tanh((res.Rsi - 50m) / 12m);
            decimal macdFeat = Tanh(res.MacdHist / (atrSafe * 0.15m));
            decimal stochFeat = Tanh((res.StochRsiK - 50m) / 20m);
            res.RsiFeature = rsiFeat;
            res.MomentumScore = Math.Clamp((0.50m * rsiFeat) + (0.35m * macdFeat) + (0.15m * stochFeat), -1m, 1m);

            decimal vwapFeat = Tanh(((lastClose - res.Vwap) / atrSafe) / 1.0m);
            res.VwapFeature = vwapFeat;

            var last20Volumes = volumes.Skip(Math.Max(0, volumes.Count - 20)).ToList();
            decimal mean20 = last20Volumes.Count > 0 ? last20Volumes.Average() : volumes.Last();
            double sumSq = last20Volumes.Sum(v => Math.Pow((double)(v - mean20), 2));
            decimal std20 = (last20Volumes.Count > 1) ? (decimal)Math.Sqrt(sumSq / last20Volumes.Count) : 1m;
            if (std20 <= 0m) std20 = 1m;
            decimal volZ = (volumes.Last() - mean20) / std20;
            decimal candleSign = lastClose >= lastOpen ? 1m : -1m;

            res.VolumeScore = Math.Clamp((0.45m * vwapFeat) + (0.35m * res.ObvSlopeFeature) + (0.20m * Tanh(volZ / 1.5m) * candleSign), -1m, 1m);

            decimal bbSpan = res.BollingerUpper - res.BollingerLower;
            decimal bbPos = bbSpan > 0 ? Math.Clamp(2m * (lastClose - res.BollingerMiddle) / bbSpan, -1m, 1m) : 0m;
            res.BbPositionFeature = bbPos;
            res.VolatilityScore = bbPos;

            decimal fvgFeat = (res.HasBullishFvg && !res.HasBearishFvg) ? 0.08m : ((!res.HasBullishFvg && res.HasBearishFvg) ? -0.08m : 0m);

            res.BtcResidualFeature = 0m; // AnalyzeCoinAsync sonra yazar
            res.DirectionalRaw = Math.Clamp((0.40m * res.TrendScore) + (0.28m * res.MomentumScore) + (0.20m * res.VolumeScore) + (0.12m * res.VolatilityScore) + fvgFeat, -1m, 1m);
            res.ConfluenceScore = Math.Round(Math.Clamp(((res.DirectionalRaw + 1m) / 2m) * 100m, 5m, 98m), 1);

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
            if (klines.Count < period * 2) return (20, 20, 20);

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

            if (trList.Count < period) return (20, 20, 20);

            decimal smoothTr = trList.Take(period).Sum();
            decimal smoothPlusDm = plusDmList.Take(period).Sum();
            decimal smoothMinusDm = minusDmList.Take(period).Sum();

            var dxList = new List<decimal>();
            decimal lastPlusDi = 20;
            decimal lastMinusDi = 20;

            for (int i = period; i < trList.Count; i++)
            {
                smoothTr = smoothTr - (smoothTr / period) + trList[i];
                smoothPlusDm = smoothPlusDm - (smoothPlusDm / period) + plusDmList[i];
                smoothMinusDm = smoothMinusDm - (smoothMinusDm / period) + minusDmList[i];

                decimal plusDi = smoothTr > 0 ? (smoothPlusDm / smoothTr) * 100 : 0;
                decimal minusDi = smoothTr > 0 ? (smoothMinusDm / smoothTr) * 100 : 0;
                lastPlusDi = plusDi;
                lastMinusDi = minusDi;

                decimal diSum = plusDi + minusDi;
                decimal dx = diSum > 0 ? (Math.Abs(plusDi - minusDi) / diSum) * 100 : 0;
                dxList.Add(dx);
            }

            if (dxList.Count < period) return (Math.Round(dxList.LastOrDefault(), 2), Math.Round(lastPlusDi, 2), Math.Round(lastMinusDi, 2));

            decimal adx = dxList.Take(period).Average();
            for (int i = period; i < dxList.Count; i++)
            {
                adx = ((adx * (period - 1)) + dxList[i]) / period;
            }

            return (Math.Round(adx, 2), Math.Round(lastPlusDi, 2), Math.Round(lastMinusDi, 2));
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
            if (klines == null || klines.Count < period + 1)
                return (klines?.LastOrDefault()?.Close ?? 0, true);

            var trList = new List<decimal>();
            for (int i = 1; i < klines.Count; i++)
            {
                decimal tr = Math.Max(klines[i].High - klines[i].Low,
                    Math.Max(Math.Abs(klines[i].High - klines[i - 1].Close), Math.Abs(klines[i].Low - klines[i - 1].Close)));
                trList.Add(tr);
            }

            var atrValues = new List<decimal>();
            if (trList.Count >= period)
            {
                decimal currentAtr = trList.Take(period).Average();
                atrValues.Add(currentAtr);
                for (int i = period; i < trList.Count; i++)
                {
                    currentAtr = (currentAtr * (period - 1) + trList[i]) / period;
                    atrValues.Add(currentAtr);
                }
            }
            else
            {
                return (klines.Last().Close, true);
            }

            decimal finalUpper = 0;
            decimal finalLower = 0;
            decimal prevFinalUpper = 0;
            decimal prevFinalLower = 0;
            decimal superTrend = 0;
            bool isBullish = true;

            for (int i = 0; i < atrValues.Count; i++)
            {
                int candleIdx = i + period;
                var candle = klines[candleIdx];
                var prevCandle = klines[candleIdx - 1];
                decimal hl2 = (candle.High + candle.Low) / 2m;
                decimal basicUpper = hl2 + (multiplier * atrValues[i]);
                decimal basicLower = hl2 - (multiplier * atrValues[i]);

                if (i == 0)
                {
                    finalUpper = basicUpper;
                    finalLower = basicLower;
                    superTrend = candle.Close >= basicLower ? finalLower : finalUpper;
                    isBullish = candle.Close >= basicLower;
                }
                else
                {
                    if (basicUpper < prevFinalUpper || prevCandle.Close > prevFinalUpper)
                        finalUpper = basicUpper;
                    else
                        finalUpper = prevFinalUpper;

                    if (basicLower > prevFinalLower || prevCandle.Close < prevFinalLower)
                        finalLower = basicLower;
                    else
                        finalLower = prevFinalLower;

                    if (superTrend == prevFinalUpper)
                    {
                        superTrend = candle.Close > finalUpper ? finalLower : finalUpper;
                        isBullish = candle.Close > finalUpper;
                    }
                    else
                    {
                        superTrend = candle.Close < finalLower ? finalUpper : finalLower;
                        isBullish = candle.Close >= finalLower;
                    }
                }

                prevFinalUpper = finalUpper;
                prevFinalLower = finalLower;
            }

            return (Math.Round(superTrend, 4), isBullish);
        }

        public decimal CalculateVwap(List<Kline> klines)
        {
            if (klines == null || klines.Count == 0) return 0;
            
            var lastBar = klines.Last();
            var sessionDate = lastBar.Time.Date;
            var sessionKlines = klines.Where(k => k.Time.Date == sessionDate).ToList();
            if (sessionKlines.Count == 0)
            {
                sessionKlines = klines.TakeLast(Math.Min(klines.Count, 24)).ToList();
            }

            decimal cumPriceVol = 0;
            decimal cumVol = 0;

            foreach (var k in sessionKlines)
            {
                decimal tp = (k.High + k.Low + k.Close) / 3m;
                cumPriceVol += tp * k.Volume;
                cumVol += k.Volume;
            }

            return cumVol > 0 ? Math.Round(cumPriceVol / cumVol, 4) : lastBar.Close;
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
