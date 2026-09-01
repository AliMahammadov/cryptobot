using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CryptoSense.Models
{
    public enum UserRole
    {
        Admin,
        User
    }

    public enum SignalDirection
    {
        Buy,   // LONG
        Sell   // SHORT
    }

    public enum SignalStatus
    {
        Open,
        Success,
        Failed,
        Neutral
    }

    public enum IndicatorVote
    {
        Bullish,
        Bearish,
        Neutral
    }

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

    [Table("Users")]
    public class UserAccount
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(50)]
        public string Username { get; set; } = "";
        
        [Required]
        [MaxLength(255)]
        public string PasswordHash { get; set; } = "";
        
        // Backward-compatibility helper for plain password validation / migration
        [NotMapped]
        public string Password { get; set; } = "";
        
        public UserRole Role { get; set; } = UserRole.User;
        
        [MaxLength(100)]
        public string TelegramUsername { get; set; } = "";
        
        [MaxLength(50)]
        public string TelegramChatId { get; set; } = "";
        
        public long? TelegramUserId { get; set; }
        
        public bool IsActive { get; set; } = true;
        
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;
    }

    [Table("Signals")]
    public class FuturesSignal
    {
        [Key]
        public int Id { get; set; }
        
        public int SignalNumber { get; set; }
        
        [Required]
        [MaxLength(20)]
        public string Symbol { get; set; } = "";
        
        [NotMapped]
        public string CleanSymbol => Symbol.Replace("USDT", "");
        
        public SignalDirection Direction { get; set; } = SignalDirection.Buy;
        
        [MaxLength(100)]
        public string SignalType { get; set; } = "GÜCLÜ LONG 🟢"; // Display string
        
        [Required]
        [MaxLength(10)]
        public string Timeframe { get; set; } = "15m";
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal EntryPrice { get; set; }
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal CurrentPrice { get; set; }
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal EntryLow { get; set; }
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal EntryHigh { get; set; }
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal TakeProfit1 { get; set; }
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal TakeProfit2 { get; set; }
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal TakeProfit3 { get; set; }
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal StopLoss { get; set; }
        
        [Column(TypeName = "decimal(5, 2)")]
        public decimal ConfluenceScore { get; set; } // 0..100
        
        public int Confidence { get; set; } // 0..100
        
        [NotMapped]
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> UserSignalNumbers { get; } = new();

        public SignalStatus Status { get; set; } = SignalStatus.Open;
        
        [MaxLength(100)]
        public string OutcomeStatus { get; set; } = "AKTİV 🟡";
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal? ClosePrice { get; set; }
        
        [Column(TypeName = "decimal(8, 4)")]
        public decimal? ResultPercent { get; set; }
        
        public decimal ProfitPercentAchieved { get; set; } = 0;
        
        public DateTime SourceCandleOpenTimeUtc { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public DateTime ExpiryTimeUtc { get; set; }
        public DateTime? ClosedAt { get; set; }
        public bool IsClosed { get; set; } = false;
        
        public bool Tp1Notified { get; set; } = false;
        public bool Tp2Notified { get; set; } = false;
        public bool Tp3Notified { get; set; } = false;
        public bool SignalAlertSent { get; set; } = false;
        public bool OutcomeAlertSent { get; set; } = false;
        
        public string TimestampFormatted { get; set; } = DateTime.Now.ToString("dd.MM.yyyy | HH:mm:ss");
        
        [NotMapped]
        public List<string> AnalysisReasons { get; set; } = new();
        
        [NotMapped]
        public BtcMarketCompass? BtcCompass { get; set; }
        
        [NotMapped]
        public IndicatorResult? Indicators { get; set; }
        
        [MaxLength(100)]
        public string NewsSentimentImpact { get; set; } = "Neytral";
        
        public List<SignalIndicatorSnapshot> IndicatorSnapshots { get; set; } = new();
    }

    [Table("SignalIndicatorSnapshots")]
    public class SignalIndicatorSnapshot
    {
        [Key]
        public int Id { get; set; }
        
        public int SignalId { get; set; }
        
        [Required]
        [MaxLength(50)]
        public string IndicatorName { get; set; } = "";
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal Value { get; set; }
        
        public IndicatorVote Vote { get; set; } = IndicatorVote.Neutral;
        
        [Column(TypeName = "decimal(5, 2)")]
        public decimal Weight { get; set; }
    }

    [Table("AuditLogs")]
    public class AuditLog
    {
        [Key]
        public int Id { get; set; }
        
        public int? AdminUserId { get; set; }
        
        [Required]
        [MaxLength(50)]
        public string Action { get; set; } = "";
        
        [MaxLength(100)]
        public string TargetUsername { get; set; } = "";
        
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

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

        // 5. SMA 20
        public decimal Sma20 { get; set; }

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

        // 17. BTC Correlation
        public string BtcAlignment { get; set; } = "Neytral";

        // Weighted Confluence Scoring
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
        public string Sentiment { get; set; } = "NEYTRAL ⚪";
        public int SentimentScore { get; set; }
    }

    public class NewsSentimentSummary
    {
        public int OverallScore { get; set; }
        public string Status { get; set; } = "NEYTRAL ⚪";
        public int BullishCount { get; set; }
        public int BearishCount { get; set; }
        public List<CryptoNewsItem> LatestNews { get; set; } = new();
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
        public long SuperAdminUserId { get; set; } = 1219998176;
        public string TelegramBotToken { get; set; } = "8671151605:AAHHPojQbGiSKtJUkeQQCnITfTHInQ3tX5U";
        public bool AutoScanEnabled { get; set; } = true;
        public int MinConfidenceThreshold { get; set; } = 78;
        public List<string> SelectedCoins { get; set; } = new() { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT" };
        public bool AlertAllCoins { get; set; } = true;
        public string DefaultTimeframe { get; set; } = "3m";
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

    public class PerformanceStats
    {
        public int TotalSignals { get; set; }
        public int OpenSignals { get; set; }
        public int SuccessSignals { get; set; }
        public int FailedSignals { get; set; }
        public int NeutralSignals { get; set; }
        public decimal WinRatePercent { get; set; }
        public decimal TotalNetProfitPercent { get; set; }
        public decimal AvgProfitPerTradePercent { get; set; }
        public decimal BestTradePercent { get; set; }
        public decimal WorstTradePercent { get; set; }
    }
}