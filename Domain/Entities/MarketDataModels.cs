using System;
using System.Collections.Generic;

namespace CryptoSense.Domain.Entities
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

    public class CoinTicker
    {
        public string Symbol { get; set; } = "";
        public decimal Price { get; set; }
        public decimal PriceChangePercent { get; set; }
        public decimal VolumeQuote { get; set; }
        public decimal High24h { get; set; }
        public decimal Low24h { get; set; }
    }

    public class BtcMarketCompass
    {
        public decimal Price { get; set; }
        public decimal Change24h { get; set; }
        public decimal High24h { get; set; }
        public decimal Low24h { get; set; }
        public decimal VolumeQuote { get; set; }
        public decimal BtcDominance { get; set; }
        public decimal UsdtDominance { get; set; }
        public decimal MarketCapChange24h { get; set; }
        public decimal Ema20 { get; set; }
        public decimal Ema50 { get; set; }
        public decimal Rsi15m { get; set; }
        public decimal MacdHist { get; set; }
        public decimal SuperTrend { get; set; }
        public decimal SupportLevel { get; set; }
        public decimal ResistanceLevel { get; set; }
        public string Trend { get; set; } = "Neytral";
        public int BullishScore { get; set; }
        public string Summary { get; set; } = "";
        public string EmaStructure { get; set; } = "";
        public string TimestampFormatted { get; set; } = CryptoSense.Domain.Common.TimeHelper.NowFormatted;
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

    public class MacroMarketOverview
    {
        public decimal BtcDominance { get; set; } = 59.0m;
        public decimal UsdtDominance { get; set; } = 6.8m;
        public decimal TotalMarketCapUsd { get; set; } = 2.65e12m;
        public decimal MarketCapChange24h { get; set; } = 0m;
        public string Summary { get; set; } = "Bazar Sabitdir";
        public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
