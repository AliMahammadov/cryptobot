using CryptoSense.Domain.Enums;

namespace CryptoSense.Application.DTOs
{
    public class IndicatorResult
    {
        // 1. RSI (14)
        public decimal Rsi { get; set; }
        public string RsiStatus { get; set; } = "Neytral";
        public IndicatorVote RsiVote { get; set; } = IndicatorVote.Neutral;

        // 2. Stochastic RSI (14, 14, 3, 3)
        public decimal StochRsiK { get; set; }
        public decimal StochRsiD { get; set; }
        public string StochStatus { get; set; } = "Neytral";
        public IndicatorVote StochVote { get; set; } = IndicatorVote.Neutral;

        // 3. MACD (12, 26, 9)
        public decimal Macd { get; set; }
        public decimal MacdSignal { get; set; }
        public decimal MacdHist { get; set; }
        public string MacdStatus { get; set; } = "Neytral";
        public IndicatorVote MacdVote { get; set; } = IndicatorVote.Neutral;

        // 4. EMAs (9, 20, 50, 200)
        public decimal Ema9 { get; set; }
        public decimal Ema20 { get; set; }
        public decimal Ema50 { get; set; }
        public decimal Ema200 { get; set; }
        public string EmaTrend { get; set; } = "Neytral";
        public IndicatorVote EmaVote { get; set; } = IndicatorVote.Neutral;

        // 5. MA / SMA (20, 50)
        public decimal Sma20 { get; set; }
        public decimal Sma50 { get; set; }
        public string SmaTrend { get; set; } = "Neytral";
        public IndicatorVote SmaVote { get; set; } = IndicatorVote.Neutral;

        // 6. Bollinger Bands (20, 2)
        public decimal BollingerUpper { get; set; }
        public decimal BollingerLower { get; set; }
        public decimal BollingerMiddle { get; set; }
        public decimal BollingerBandwidth { get; set; }
        public string BollingerStatus { get; set; } = "Neytral";
        public IndicatorVote BollingerVote { get; set; } = IndicatorVote.Neutral;

        // 7. ATR (14)
        public decimal Atr { get; set; }

        // 8. ADX (14)
        public decimal Adx { get; set; }
        public decimal PlusDi { get; set; }
        public decimal MinusDi { get; set; }
        public string AdxTrendStrength { get; set; } = "Zəif Trend";
        public IndicatorVote AdxVote { get; set; } = IndicatorVote.Neutral;

        // 9. CCI (20)
        public decimal Cci { get; set; }
        public string CciStatus { get; set; } = "Neytral";
        public IndicatorVote CciVote { get; set; } = IndicatorVote.Neutral;

        // 10. Williams %R (14)
        public decimal WilliamsR { get; set; }
        public string WilliamsRStatus { get; set; } = "Neytral";
        public IndicatorVote WilliamsRVote { get; set; } = IndicatorVote.Neutral;

        // 11. SuperTrend (10, 3)
        public decimal SuperTrend { get; set; }
        public string SuperTrendDirection { get; set; } = "Neytral";
        public IndicatorVote SuperTrendVote { get; set; } = IndicatorVote.Neutral;

        // 12. VWAP
        public decimal Vwap { get; set; }
        public string VwapStatus { get; set; } = "Neytral";
        public IndicatorVote VwapVote { get; set; } = IndicatorVote.Neutral;

        // 13. OBV
        public decimal Obv { get; set; }
        public string ObvTrend { get; set; } = "Neytral";
        public IndicatorVote ObvVote { get; set; } = IndicatorVote.Neutral;

        // 14. Volume Surge
        public decimal VolumeEma20 { get; set; }
        public decimal VolumeSurgeRatio { get; set; }
        public bool IsHighVolume { get; set; }
        public IndicatorVote VolumeVote { get; set; } = IndicatorVote.Neutral;

        // 15. Support & Resistance Pivots
        public decimal SupportLevel { get; set; }
        public decimal ResistanceLevel { get; set; }
        public decimal PivotPoint { get; set; }

        // 16. Fair Value Gaps (FVG)
        public bool HasBullishFvg { get; set; }
        public bool HasBearishFvg { get; set; }

        // 17. BTC Alignment
        public string BtcAlignment { get; set; } = "Neytral";

        // Multi-Category Confluence Scores
        public decimal TrendScore { get; set; }
        public decimal MomentumScore { get; set; }
        public decimal VolatilityScore { get; set; }
        public decimal VolumeScore { get; set; }
        public decimal MtfFactor { get; set; } = 1.0m;
        public decimal ConfluenceScore { get; set; } // 0..100
        
        public int BullishIndicatorsCount { get; set; }
        public int BearishIndicatorsCount { get; set; }
        public int NeutralIndicatorsCount { get; set; }
    }
}
