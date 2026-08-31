using System;
using System.Collections.Generic;

namespace CryptoSense.Models
{
    public class Kline
    {
        public long OpenTime { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public long CloseTime { get; set; }
        public DateTime Time => DateTimeOffset.FromUnixTimeMilliseconds(OpenTime).UtcDateTime;
    }

    public class IndicatorResult
    {
        // 1. RSI (14)
        public decimal Rsi { get; set; }
        public string RsiStatus { get; set; } = "Neytral";

        // 2. Stochastic RSI (14, 14, 3, 3)
        public decimal StochRsiK { get; set; }
        public decimal StochRsiD { get; set; }
        public string StochStatus { get; set; } = "Neytral";

        // 3. MACD (12, 26, 9)
        public decimal Macd { get; set; }
        public decimal MacdSignal { get; set; }
        public decimal MacdHist { get; set; }
        public string MacdStatus { get; set; } = "Neytral";

        // 4, 5, 6, 7. EMAs
        public decimal Ema9 { get; set; }
        public decimal Ema20 { get; set; }
        public decimal Ema50 { get; set; }
        public decimal Ema200 { get; set; }
        public string EmaTrend { get; set; } = "Neytral";

        // 8. SMA 20
        public decimal Sma20 { get; set; }

        // 9. Bollinger Bands (20, 2)
        public decimal BollingerUpper { get; set; }
        public decimal BollingerLower { get; set; }
        public decimal BollingerMiddle { get; set; }
        public decimal BollingerBandwidth { get; set; }
        public string BollingerStatus { get; set; } = "Neytral";

        // 10. ATR (14)
        public decimal Atr { get; set; }

        // 11. ADX (14) Trend Strength
        public decimal Adx { get; set; }
        public decimal PlusDi { get; set; }
        public decimal MinusDi { get; set; }
        public string AdxTrendStrength { get; set; } = "Z\u0259if Trend";

        // 12. CCI (20)
        public decimal Cci { get; set; }
        public string CciStatus { get; set; } = "Neytral";

        // 13. Williams %R (14)
        public decimal WilliamsR { get; set; }
        public string WilliamsRStatus { get; set; } = "Neytral";

        // 14. SuperTrend (10, 3)
        public decimal SuperTrend { get; set; }
        public string SuperTrendDirection { get; set; } = "Neytral";

        // 15. VWAP
        public decimal Vwap { get; set; }
        public string VwapStatus { get; set; } = "Neytral";

        // 16. OBV (On-Balance Volume)
        public decimal Obv { get; set; }
        public string ObvTrend { get; set; } = "Neytral";

        // 17. Volume Surge EMA (20)
        public decimal VolumeEma20 { get; set; }
        public decimal VolumeSurgeRatio { get; set; }
        public bool IsHighVolume { get; set; }

        // 18. Support & Resistance Pivots (Swing High/Low)
        public decimal SupportLevel { get; set; }
        public decimal ResistanceLevel { get; set; }
        public decimal PivotPoint { get; set; }

        // 19. Fair Value Gaps (FVG) / Smart Money Concept
        public bool HasBullishFvg { get; set; }
        public bool HasBearishFvg { get; set; }
        public decimal FvgTop { get; set; }
        public decimal FvgBottom { get; set; }

        // 20. BTC Correlation / Alignment
        public string BtcAlignment { get; set; } = "Neytral";

        // Confluence Scoring
        public int ConfluenceScore { get; set; } // -100 to +100
        public int BullishIndicatorsCount { get; set; }
        public int BearishIndicatorsCount { get; set; }
        public int NeutralIndicatorsCount { get; set; }
    }

    public class BtcMarketCompass
    {
        public decimal Price { get; set; }
        public decimal Change24h { get; set; }
        public string Trend { get; set; } = "Neytral";
        public int BullishScore { get; set; }
        public string Summary { get; set; } = "";
        public decimal Rsi15m { get; set; }
        public string EmaStructure { get; set; } = "";
        public string TimestampFormatted { get; set; } = DateTime.Now.ToString("dd.MM.yyyy | HH:mm:ss");
    }

    public class CryptoNewsItem
    {
        public string Title { get; set; } = "";
        public string Source { get; set; } = "";
        public string Url { get; set; } = "";
        public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
        public string Sentiment { get; set; } = "NEYTRAL \u26AA";
        public int SentimentScore { get; set; }
    }

    public class NewsSentimentSummary
    {
        public int OverallScore { get; set; }
        public string Status { get; set; } = "NEYTRAL \u26AA";
        public int BullishCount { get; set; }
        public int BearishCount { get; set; }
        public List<CryptoNewsItem> LatestNews { get; set; } = new();
    }

    public class FuturesSignal
    {
        public int SignalNumber { get; set; } = 148;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Symbol { get; set; } = "";
        public string CleanSymbol => Symbol.Replace("USDT", "");
        public decimal CurrentPrice { get; set; }
        public string Timeframe { get; set; } = "15m";
        public string SignalType { get; set; } = "NEYTRAL"; // G??CL?? LONG, G??CL?? SHORT, NEYTRAL
        public int Confidence { get; set; }
        public decimal EntryLow { get; set; }
        public decimal EntryHigh { get; set; }
        public decimal TakeProfit1 { get; set; }
        public decimal TakeProfit2 { get; set; }
        public decimal TakeProfit3 { get; set; }
        public decimal StopLoss { get; set; }
        public decimal RiskRewardRatio { get; set; }
        public List<string> AnalysisReasons { get; set; } = new();
        public BtcMarketCompass? BtcCompass { get; set; }
        public IndicatorResult? Indicators { get; set; }
        public string NewsSentimentImpact { get; set; } = "Neytral";
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string TimestampFormatted { get; set; } = DateTime.Now.ToString("dd.MM.yyyy | HH:mm:ss");
        
        public string OutcomeStatus { get; set; } = "AKT\u0130V \U0001F7E1";
        public decimal ProfitPercentAchieved { get; set; } = 0;
        public DateTime? ClosedAt { get; set; }
        public bool IsClosed { get; set; } = false;
        public bool Tp1Notified { get; set; } = false;
        public bool Tp2Notified { get; set; } = false;
        public bool Tp3Notified { get; set; } = false;
    }

    public class UserAccount
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string Role { get; set; } = "USER";
        public string TelegramUsername { get; set; } = "";
        public string TelegramChatId { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;
    }

    public class CoinTicker
    {
        public string Symbol { get; set; } = "";
        public decimal Price { get; set; }
        public decimal PriceChangePercent { get; set; }
        public decimal VolumeQuote { get; set; }
        public decimal High24h { get; set; }
        public decimal Low24h { get; set; }
    }

    public class AppConfig
    {
        public string SuperAdminTelegram { get; set; } = "@alimahammadov";
        public string SuperAdminChatId { get; set; } = "1219998176";
        public string TelegramBotToken { get; set; } = "8671151605:AAHHPojQbGiSKtJUkeQQCnITfTHInQ3tX5U";
        public bool AutoScanEnabled { get; set; } = true;
        public int MinConfidenceThreshold { get; set; } = 80;
        public List<string> SelectedCoins { get; set; } = new() { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT" };
        public bool AlertAllCoins { get; set; } = true;
        public string DefaultTimeframe { get; set; } = "15m";
    }

    public class LoginRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public class CreateUserRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
}