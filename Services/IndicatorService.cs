using System;
using System.Collections.Generic;
using System.Linq;
using CryptoSense.Models;

namespace CryptoSense.Services
{
    public class IndicatorService
    {
        public IndicatorResult CalculateIndicators(List<Kline> klines)
        {
            var res = new IndicatorResult();
            if (klines == null || klines.Count < 35) return res;

            var closes = klines.Select(k => k.Close).ToList();
            var highs = klines.Select(k => k.High).ToList();
            var lows = klines.Select(k => k.Low).ToList();
            var volumes = klines.Select(k => k.Volume).ToList();
            var lastClose = closes.Last();

            // 1. RSI (14)
            res.Rsi = CalculateRsi(closes, 14);
            if (res.Rsi <= 32) res.RsiStatus = "H\u0259ddind\u0259n Art\u0131q Sat\u0131\u015F (Oversold Dip)";
            else if (res.Rsi >= 68) res.RsiStatus = "H\u0259ddind\u0259n Art\u0131q Al\u0131\u015F (Overbought Top)";
            else if (res.Rsi >= 52) res.RsiStatus = "Sa\u011Flam Al\u0131\u015F Zonas\u0131";
            else res.RsiStatus = "Sat\u0131\u015F Meylli";

            // 2. Stochastic RSI (14, 14, 3, 3)
            var (stochK, stochD) = CalculateStochRsi(closes, 14, 3, 3);
            res.StochRsiK = stochK;
            res.StochRsiD = stochD;
            if (stochK <= 20 && stochK > stochD) res.StochStatus = "Bullish Cross (Dipd\u0259n Qay\u0131d\u0131\u015F)";
            else if (stochK >= 80 && stochK < stochD) res.StochStatus = "Bearish Cross (Zirv\u0259d\u0259n D\u00FCz\u0259li\u015F)";
            else if (stochK > stochD) res.StochStatus = "Al\u0131\u015F \u0130mpulsu";
            else res.StochStatus = "Sat\u0131\u015F \u0130mpulsu";

            // 3. MACD (12, 26, 9)
            var (macd, signal, hist) = CalculateMacd(closes);
            res.Macd = macd;
            res.MacdSignal = signal;
            res.MacdHist = hist;
            if (hist > 0 && macd > signal) res.MacdStatus = "G\u00FCcl\u00FC Bullish Momentum";
            else if (hist < 0 && macd < signal) res.MacdStatus = "G\u00FCcl\u00FC Bearish Momentum";
            else res.MacdStatus = "K\u0259si\u015Fm\u0259 \u018Fr\u0259f\u0259sind\u0259";

            // 4, 5, 6, 7. EMAs
            res.Ema9 = CalculateEma(closes, 9);
            res.Ema20 = CalculateEma(closes, 20);
            res.Ema50 = CalculateEma(closes, 50);
            res.Ema200 = klines.Count >= 200 ? CalculateEma(closes, 200) : res.Ema50;

            if (res.Ema20 > res.Ema50 && lastClose > res.Ema20) res.EmaTrend = "G\u00FCcl\u00FC Y\u00FCks\u0259li\u015F (Bullish)";
            else if (res.Ema20 > res.Ema50) res.EmaTrend = "M\u00FCsb\u0259t Trend";
            else if (res.Ema20 < res.Ema50 && lastClose < res.Ema20) res.EmaTrend = "G\u00FCcl\u00FC Eni\u015F (Bearish)";
            else res.EmaTrend = "M\u0259nfi Trend";

            // 8. SMA 20
            res.Sma20 = closes.Skip(Math.Max(0, closes.Count - 20)).Average();

            // 9. Bollinger Bands (20, 2)
            var (bUpper, bLower, bBandwidth) = CalculateBollingerBands(closes, 20, 2m);
            res.BollingerUpper = bUpper;
            res.BollingerLower = bLower;
            res.BollingerMiddle = res.Sma20;
            res.BollingerBandwidth = bBandwidth;
            if (lastClose <= bLower) res.BollingerStatus = "A\u015Fa\u011F\u0131 Band Toxunu\u015Fu (Al\u0131\u015F Reaksiyas\u0131)";
            else if (lastClose >= bUpper) res.BollingerStatus = "Yuxar\u0131 Band Toxunu\u015Fu (Sat\u0131\u015F Reaksiyas\u0131)";
            else res.BollingerStatus = "Normal Band Aral\u0131\u011F\u0131";

            // 10. ATR (14)
            res.Atr = CalculateAtr(klines, 14);

            // 11. ADX (14) Trend Strength
            var (adx, pDi, mDi) = CalculateAdx(klines, 14);
            res.Adx = adx;
            res.PlusDi = pDi;
            res.MinusDi = mDi;
            if (adx >= 25 && pDi > mDi) res.AdxTrendStrength = "G\u00FCcl\u00FC Y\u00FCks\u0259li\u015F Trendi";
            else if (adx >= 25 && mDi > pDi) res.AdxTrendStrength = "G\u00FCcl\u00FC D\u00FC\u015F\u00FC\u015F Trendi";
            else res.AdxTrendStrength = "Z\u0259if / Konsolidasiya";

            // 12. CCI (20)
            res.Cci = CalculateCci(klines, 20);
            if (res.Cci <= -100) res.CciStatus = "D\u0259rin Sat\u0131\u015F Zonas\u0131";
            else if (res.Cci >= 100) res.CciStatus = "G\u00FCcl\u00FC Al\u0131\u015F Zonas\u0131";
            else res.CciStatus = "Neytral S\u0259viyy\u0259";

            // 13. Williams %R (14)
            res.WilliamsR = CalculateWilliamsR(klines, 14);
            if (res.WilliamsR <= -80) res.WilliamsRStatus = "A\u015Fa\u011F\u0131 Hadis\u0259 (Dibin Se\u00E7imi)";
            else if (res.WilliamsR >= -20) res.WilliamsRStatus = "Zirv\u0259 Hadis\u0259si";
            else res.WilliamsRStatus = "Neytral";

            // 14. SuperTrend (10, 3)
            var (superTrend, isBullish) = CalculateSuperTrend(klines, 10, 3.0m);
            res.SuperTrend = superTrend;
            res.SuperTrendDirection = isBullish ? "Y\u00DCKS\u018FL\u0130\u015E (BULLISH) \U0001F7E2" : "EN\u0130\u015E (BEARISH) \U0001F534";

            // 15. VWAP
            res.Vwap = CalculateVwap(klines);
            res.VwapStatus = lastClose >= res.Vwap ? "VWAP \u00DCz\u0259rind\u0259 (Al\u0131c\u0131 T\u0259r\u0259f)" : "VWAP Alt\u0131nda (Sat\u0131c\u0131 T\u0259r\u0259f)";

            // 16. OBV (On-Balance Volume)
            res.Obv = CalculateObv(klines);
            res.ObvTrend = res.Obv >= 0 ? "H\u0259cm Toplan\u0131r (Akumulyasiya)" : "H\u0259cm \u00C7\u0131x\u0131r (Distribusiya)";

            // 17. Volume Surge (20)
            res.VolumeEma20 = CalculateEma(volumes, 20);
            res.VolumeSurgeRatio = res.VolumeEma20 > 0 ? Math.Round(volumes.Last() / res.VolumeEma20, 2) : 1.0m;
            res.IsHighVolume = res.VolumeSurgeRatio >= 1.4m;

            // 18. Support & Resistance Pivots
            var recentLows = lows.Skip(Math.Max(0, lows.Count - 35)).ToList();
            var recentHighs = highs.Skip(Math.Max(0, highs.Count - 35)).ToList();
            res.SupportLevel = Math.Round(recentLows.OrderBy(l => l).Take(3).Average(), 4);
            res.ResistanceLevel = Math.Round(recentHighs.OrderByDescending(h => h).Take(3).Average(), 4);
            res.PivotPoint = Math.Round((highs.Last() + lows.Last() + closes.Last()) / 3, 4);

            // 19. Fair Value Gaps (FVG)
            if (klines.Count >= 5)
            {
                var i = klines.Count - 1;
                if (klines[i].Low > klines[i - 2].High)
                {
                    res.HasBullishFvg = true;
                    res.FvgBottom = klines[i - 2].High;
                    res.FvgTop = klines[i].Low;
                }
                if (klines[i].High < klines[i - 2].Low)
                {
                    res.HasBearishFvg = true;
                    res.FvgTop = klines[i - 2].Low;
                    res.FvgBottom = klines[i].High;
                }
            }

            // 20. Confluence Voting Matrix
            int bullVotes = 0;
            int bearVotes = 0;

            if (res.Rsi <= 35 || (res.Rsi >= 50 && res.Rsi <= 65)) bullVotes++;
            else if (res.Rsi >= 65 || res.Rsi < 48) bearVotes++;

            if (res.StochStatus.Contains("Bullish") || res.StochStatus.Contains("Al\u0131\u015F")) bullVotes++;
            else if (res.StochStatus.Contains("Bearish") || res.StochStatus.Contains("Sat\u0131\u015F")) bearVotes++;

            if (res.MacdHist > 0 && res.Macd > res.MacdSignal) bullVotes++;
            else if (res.MacdHist < 0 && res.Macd < res.MacdSignal) bearVotes++;

            if (res.EmaTrend.Contains("Bullish") || res.EmaTrend.Contains("M\u00FCsb\u0259t")) bullVotes++;
            else if (res.EmaTrend.Contains("Bearish") || res.EmaTrend.Contains("M\u0259nfi")) bearVotes++;

            if (lastClose > res.Ema200) bullVotes++;
            else bearVotes++;

            if (lastClose <= res.BollingerLower || lastClose > res.BollingerMiddle) bullVotes++;
            else if (lastClose >= res.BollingerUpper || lastClose < res.BollingerMiddle) bearVotes++;

            if (res.PlusDi > res.MinusDi) bullVotes++;
            else bearVotes++;

            if (res.Cci >= -100 && res.Cci <= 120 && res.Cci > 0) bullVotes++;
            else if (res.Cci < 0) bearVotes++;

            if (res.WilliamsR >= -60 && res.WilliamsR <= -20) bullVotes++;
            else if (res.WilliamsR < -60) bearVotes++;

            if (res.SuperTrendDirection.Contains("BULLISH")) bullVotes++;
            else bearVotes++;

            if (res.VwapStatus.Contains("Al\u0131c\u0131")) bullVotes++;
            else bearVotes++;

            if (res.ObvTrend.Contains("Akumulyasiya")) bullVotes++;
            else bearVotes++;

            if (res.IsHighVolume) { if (lastClose >= klines.Last().Open) bullVotes++; else bearVotes++; }

            if (lastClose >= res.PivotPoint) bullVotes++;
            else bearVotes++;

            if (res.HasBullishFvg) bullVotes++;
            if (res.HasBearishFvg) bearVotes++;

            res.BullishIndicatorsCount = bullVotes;
            res.BearishIndicatorsCount = bearVotes;
            res.NeutralIndicatorsCount = 20 - (bullVotes + bearVotes);

            int total = bullVotes + bearVotes;
            if (total > 0)
            {
                res.ConfluenceScore = (int)Math.Round(((decimal)(bullVotes - bearVotes) / total) * 100);
            }

            return res;
        }

        public decimal CalculateEma(List<decimal> prices, int period)
        {
            if (prices.Count < period) return prices.LastOrDefault();
            decimal multiplier = 2.0m / (period + 1);
            decimal ema = prices.Take(period).Average();

            for (int i = period; i < prices.Count; i++)
            {
                ema = ((prices[i] - ema) * multiplier) + ema;
            }
            return Math.Round(ema, 4);
        }

        public decimal CalculateRsi(List<decimal> prices, int period = 14)
        {
            if (prices.Count <= period) return 50m;
            decimal gains = 0m, losses = 0m;

            for (int i = 1; i <= period; i++)
            {
                decimal diff = prices[i] - prices[i - 1];
                if (diff >= 0) gains += diff;
                else losses += Math.Abs(diff);
            }

            decimal avgGain = gains / period;
            decimal avgLoss = losses / period;

            for (int i = period + 1; i < prices.Count; i++)
            {
                decimal diff = prices[i] - prices[i - 1];
                if (diff >= 0)
                {
                    avgGain = ((avgGain * (period - 1)) + diff) / period;
                    avgLoss = (avgLoss * (period - 1)) / period;
                }
                else
                {
                    avgGain = (avgGain * (period - 1)) / period;
                    avgLoss = ((avgLoss * (period - 1)) + Math.Abs(diff)) / period;
                }
            }

            if (avgLoss == 0) return 100m;
            decimal rs = avgGain / avgLoss;
            return Math.Round(100m - (100m / (1m + rs)), 2);
        }

        public (decimal stochK, decimal stochD) CalculateStochRsi(List<decimal> prices, int rsiPeriod = 14, int kPeriod = 3, int dPeriod = 3)
        {
            if (prices.Count < rsiPeriod + kPeriod + dPeriod) return (50, 50);

            var rsiList = new List<decimal>();
            for (int i = rsiPeriod; i <= prices.Count; i++)
            {
                rsiList.Add(CalculateRsi(prices.Take(i).ToList(), rsiPeriod));
            }

            var stochList = new List<decimal>();
            for (int i = rsiPeriod; i <= rsiList.Count; i++)
            {
                var window = rsiList.Skip(i - rsiPeriod).Take(rsiPeriod).ToList();
                decimal minRsi = window.Min();
                decimal maxRsi = window.Max();
                decimal diff = maxRsi - minRsi;
                decimal stoch = diff == 0 ? 50 : ((window.Last() - minRsi) / diff) * 100m;
                stochList.Add(stoch);
            }

            decimal k = stochList.Skip(Math.Max(0, stochList.Count - kPeriod)).Average();
            decimal d = stochList.Skip(Math.Max(0, stochList.Count - (kPeriod + dPeriod))).Take(dPeriod).Average();

            return (Math.Round(k, 2), Math.Round(d, 2));
        }

        public (decimal macd, decimal signal, decimal hist) CalculateMacd(List<decimal> prices)
        {
            if (prices.Count < 35) return (0, 0, 0);

            var macdLine = new List<decimal>();
            for (int i = 26; i <= prices.Count; i++)
            {
                var subList = prices.Take(i).ToList();
                decimal ema12 = CalculateEma(subList, 12);
                decimal ema26 = CalculateEma(subList, 26);
                macdLine.Add(ema12 - ema26);
            }

            decimal currentMacd = macdLine.Last();
            decimal signalLine = CalculateEma(macdLine, 9);
            decimal hist = currentMacd - signalLine;

            return (Math.Round(currentMacd, 4), Math.Round(signalLine, 4), Math.Round(hist, 4));
        }

        public (decimal upper, decimal lower, decimal bandwidth) CalculateBollingerBands(List<decimal> prices, int period = 20, decimal numStd = 2.0m)
        {
            if (prices.Count < period) return (prices.LastOrDefault(), prices.LastOrDefault(), 0);
            var window = prices.Skip(prices.Count - period).Take(period).ToList();
            decimal mean = window.Average();
            decimal sumSquares = window.Sum(p => (p - mean) * (p - mean));
            decimal stdDev = (decimal)Math.Sqrt((double)(sumSquares / period));

            decimal upper = mean + (numStd * stdDev);
            decimal lower = mean - (numStd * stdDev);
            decimal bandwidth = mean > 0 ? Math.Round(((upper - lower) / mean) * 100, 2) : 0;

            return (Math.Round(upper, 4), Math.Round(lower, 4), bandwidth);
        }

        public decimal CalculateAtr(List<Kline> klines, int period = 14)
        {
            if (klines.Count < period + 1) return 0m;

            var trList = new List<decimal>();
            for (int i = 1; i < klines.Count; i++)
            {
                decimal tr1 = klines[i].High - klines[i].Low;
                decimal tr2 = Math.Abs(klines[i].High - klines[i - 1].Close);
                decimal tr3 = Math.Abs(klines[i].Low - klines[i - 1].Close);
                trList.Add(Math.Max(tr1, Math.Max(tr2, tr3)));
            }

            decimal atr = trList.Take(period).Average();
            for (int i = period; i < trList.Count; i++)
            {
                atr = ((atr * (period - 1)) + trList[i]) / period;
            }
            return Math.Round(atr, 4);
        }

        public (decimal adx, decimal plusDi, decimal minusDi) CalculateAdx(List<Kline> klines, int period = 14)
        {
            if (klines.Count < period * 2) return (20, 20, 20);

            var tr = new List<decimal>();
            var plusDm = new List<decimal>();
            var minusDm = new List<decimal>();

            for (int i = 1; i < klines.Count; i++)
            {
                decimal hDiff = klines[i].High - klines[i - 1].High;
                decimal lDiff = klines[i - 1].Low - klines[i].Low;

                plusDm.Add((hDiff > lDiff && hDiff > 0) ? hDiff : 0);
                minusDm.Add((lDiff > hDiff && lDiff > 0) ? lDiff : 0);

                decimal tr1 = klines[i].High - klines[i].Low;
                decimal tr2 = Math.Abs(klines[i].High - klines[i - 1].Close);
                decimal tr3 = Math.Abs(klines[i].Low - klines[i - 1].Close);
                tr.Add(Math.Max(tr1, Math.Max(tr2, tr3)));
            }

            decimal smoothedTr = tr.Take(period).Sum();
            decimal smoothedPlusDm = plusDm.Take(period).Sum();
            decimal smoothedMinusDm = minusDm.Take(period).Sum();

            var dxList = new List<decimal>();
            for (int i = period; i < tr.Count; i++)
            {
                smoothedTr = smoothedTr - (smoothedTr / period) + tr[i];
                smoothedPlusDm = smoothedPlusDm - (smoothedPlusDm / period) + plusDm[i];
                smoothedMinusDm = smoothedMinusDm - (smoothedMinusDm / period) + minusDm[i];

                decimal pDi = smoothedTr > 0 ? (smoothedPlusDm / smoothedTr) * 100 : 0;
                decimal mDi = smoothedTr > 0 ? (smoothedMinusDm / smoothedTr) * 100 : 0;
                decimal diSum = pDi + mDi;
                decimal dx = diSum > 0 ? (Math.Abs(pDi - mDi) / diSum) * 100 : 0;
                dxList.Add(dx);
            }

            decimal finalAdx = dxList.Skip(Math.Max(0, dxList.Count - period)).Average();
            decimal finalPlusDi = smoothedTr > 0 ? (smoothedPlusDm / smoothedTr) * 100 : 0;
            decimal finalMinusDi = smoothedTr > 0 ? (smoothedMinusDm / smoothedTr) * 100 : 0;

            return (Math.Round(finalAdx, 2), Math.Round(finalPlusDi, 2), Math.Round(finalMinusDi, 2));
        }

        public decimal CalculateCci(List<Kline> klines, int period = 20)
        {
            if (klines.Count < period) return 0;
            var tpList = klines.Select(k => (k.High + k.Low + k.Close) / 3).ToList();
            var window = tpList.Skip(tpList.Count - period).Take(period).ToList();
            decimal smaTp = window.Average();
            decimal meanDev = window.Average(tp => Math.Abs(tp - smaTp));

            if (meanDev == 0) return 0;
            decimal cci = (tpList.Last() - smaTp) / (0.015m * meanDev);
            return Math.Round(cci, 2);
        }

        public decimal CalculateWilliamsR(List<Kline> klines, int period = 14)
        {
            if (klines.Count < period) return -50;
            var window = klines.Skip(klines.Count - period).Take(period).ToList();
            decimal highestHigh = window.Max(k => k.High);
            decimal lowestLow = window.Min(k => k.Low);
            decimal diff = highestHigh - lowestLow;

            if (diff == 0) return -50;
            decimal wr = ((highestHigh - window.Last().Close) / diff) * -100m;
            return Math.Round(wr, 2);
        }

        public (decimal superTrend, bool isBullish) CalculateSuperTrend(List<Kline> klines, int period = 10, decimal multiplier = 3.0m)
        {
            if (klines.Count < period + 1) return (klines.LastOrDefault()?.Close ?? 0, true);
            decimal atr = CalculateAtr(klines, period);

            decimal upperBand = ((klines.Last().High + klines.Last().Low) / 2) + (multiplier * atr);
            decimal lowerBand = ((klines.Last().High + klines.Last().Low) / 2) - (multiplier * atr);

            bool isBullish = klines.Last().Close >= lowerBand;
            decimal superTrend = isBullish ? lowerBand : upperBand;

            return (Math.Round(superTrend, 4), isBullish);
        }

        public decimal CalculateVwap(List<Kline> klines)
        {
            if (klines.Count == 0) return 0;
            decimal sumTpVol = 0;
            decimal sumVol = 0;

            foreach (var k in klines)
            {
                decimal tp = (k.High + k.Low + k.Close) / 3;
                sumTpVol += tp * k.Volume;
                sumVol += k.Volume;
            }

            return sumVol > 0 ? Math.Round(sumTpVol / sumVol, 4) : klines.Last().Close;
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