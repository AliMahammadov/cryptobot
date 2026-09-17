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
            if (signal.AtrPercent > 0)
            {
                decimal atrAbs = entry * signal.AtrPercent / 100m;
                if (slDist < BotConstants.Thresholds.MinSlAtr * atrAbs || slDist > BotConstants.Thresholds.MaxSlAtr * atrAbs)
                {
                    return (false, "SL");
                }
            }

            if (signal.TakeProfit2 <= 0 || signal.TakeProfit2 == signal.TakeProfit1)
            {
                return (false, "RR");
            }

            decimal tp1Dist = Math.Abs(signal.TakeProfit1 - entry);
            decimal tpBDist = Math.Abs(signal.TakeProfit2 - entry);
            decimal weightedTpDist = (BotConstants.Thresholds.Tp1Weight * tp1Dist) + (BotConstants.Thresholds.Tp2Weight * tpBDist);
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
