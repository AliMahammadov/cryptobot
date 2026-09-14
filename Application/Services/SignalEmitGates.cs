using System;
using CryptoSense.Domain.Common;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Application.Services
{
    public static class SignalEmitGates
    {
        public static (bool Ok, string SkipReason) Evaluate(FuturesSignal signal, decimal livePrice, long dataAgeMs, string timeframe)
        {
            if (signal == null) return (false, "NULL_SIGNAL");

            string tf = string.IsNullOrEmpty(timeframe) ? signal.Timeframe : timeframe;
            decimal entry = signal.EntryPrice > 0 ? signal.EntryPrice : livePrice;

            decimal slDist = Math.Abs(signal.StopLoss - entry);
            decimal slPct = entry > 0 ? (slDist / entry) * 100m : 0m;
            decimal maxSlPct = tf == "4h" ? BotConstants.Thresholds.MaxSlPct4h : BotConstants.Thresholds.MaxSlPct1h;
            if (slPct > maxSlPct)
            {
                return (false, "SL");
            }

            decimal tp1Dist = Math.Abs(signal.TakeProfit1 - entry);
            decimal tpBDist = signal.TakeProfit2 > 0 ? Math.Abs(signal.TakeProfit2 - entry) : tp1Dist;
            decimal weightedTpDist = (0.50m * tp1Dist) + (0.50m * tpBDist);
            decimal effectiveRr = slDist > 0 ? (weightedTpDist / slDist) : 0m;
            if (effectiveRr < BotConstants.Thresholds.MinRiskReward)
            {
                return (false, "RR");
            }

            long effectiveDataAge = dataAgeMs > 0 ? dataAgeMs : signal.DataAgeMs;
            if (effectiveDataAge > BotConstants.Thresholds.MaxDataAgeMs)
            {
                return (false, "DataAge");
            }

            return (true, string.Empty);
        }
    }
}
