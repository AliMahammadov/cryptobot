using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CryptoSense.Domain.Enums;

namespace CryptoSense.Domain.Entities
{
    [Table("Signals")]
    public class FuturesSignal
    {
        [Key]
        public int Id { get; set; }
        
        public int SignalNumber { get; set; }
        
        [NotMapped]
        public int Number { get => SignalNumber; set => SignalNumber = value; }
        
        [MaxLength(50)]
        public string NewsSentimentImpact { get; set; } = "Neytral ⚪";
        
        [Required]
        [MaxLength(20)]
        public string Symbol { get; set; } = "";
        
        [NotMapped]
        public string CleanSymbol => Symbol.Replace("USDT", "");
        
        public SignalDirection Direction { get; set; } = SignalDirection.Buy;
        
        [MaxLength(100)]
        public string SignalType { get; set; } = "GÜCLÜ LONG 🟢";
        
        [Required]
        [MaxLength(10)]
        public string Timeframe { get; set; } = "1h";
        
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
        public decimal ConfluenceScore { get; set; }
        
        public int Confidence { get; set; }
        
        [NotMapped]
        public ConcurrentDictionary<string, int> UserSignalNumbers { get; } = new();

        public SignalStatus Status { get; set; } = SignalStatus.Open;
        
        [MaxLength(100)]
        public string OutcomeStatus { get; set; } = "AKTİV 🟡";
        
        [MaxLength(50)]
        public string? CloseReason { get; set; }
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal? ClosePrice { get; set; }
        
        [Column(TypeName = "decimal(8, 4)")]
        public decimal? ResultPercent { get; set; }
        
        [Column(TypeName = "decimal(8, 4)")]
        public decimal RealizedProfitPercent { get; set; } = 0;

        [Column(TypeName = "decimal(5, 4)")]
        public decimal RemainingPositionRatio { get; set; } = 1.0m;

        public bool IsPartial1Closed { get; set; } = false;
        public bool IsPartial2Closed { get; set; } = false;
        
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
        public bool IsTest { get; set; } = false;
        
        public string TimestampFormatted { get; set; } = CryptoSense.Domain.Common.TimeHelper.NowFormatted;
        
        [NotMapped]
        public List<string> AnalysisReasons { get; set; } = new();
        
        [NotMapped]
        public BtcMarketCompass? BtcCompass { get; set; }
        
        [NotMapped]
        public object? Indicators { get; set; }
        
        [NotMapped]
        public string PriceSource { get; set; } = "ws_last";
        
        [NotMapped]
        public long ExchangeTsMs { get; set; }
        
        [NotMapped]
        public long DataAgeMs { get; set; }
        
        [NotMapped]
        public DateTime CandleCloseTimeUtc { get; set; }
        
        [NotMapped]
        public decimal SessionHigh { get; set; }
        
        [NotMapped]
        public decimal SessionLow { get; set; }
        
        [NotMapped]
        public decimal GrossResultPercent { get; set; }
        
        [NotMapped]
        public decimal NetResultPercent { get; set; }
        
        [NotMapped]
        public decimal MfePercent { get; set; }
        
        [NotMapped]
        public decimal MaePercent { get; set; }

        [NotMapped]
        public decimal InitialRiskR { get; set; }

        [NotMapped]
        public decimal AtrPercent { get; set; }

        [NotMapped]
        public decimal SignalSwingLow { get; set; }

        [NotMapped]
        public decimal SignalSwingHigh { get; set; }

        [NotMapped]
        public bool BreakevenTriggered { get; set; }

        [NotMapped]
        public decimal TrailPrice { get; set; }

        [NotMapped]
        public int CandlesObserved { get; set; }

        [NotMapped]
        public DateTime LastObservedCandleTime { get; set; }

        public List<SignalIndicatorSnapshot> IndicatorSnapshots { get; set; } = new();
    }
}
