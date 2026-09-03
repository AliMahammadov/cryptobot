using System.Collections.Generic;

namespace CryptoSense.Application.DTOs
{
    public class CoinPerformanceBreakdownDto
    {
        public string Symbol { get; set; } = "";
        public string CleanSymbol => Symbol.Replace("USDT", "");
        public int TotalTrades { get; set; }
        public int SuccessTrades { get; set; }
        public int FailedTrades { get; set; }
        public decimal OverallWinRate { get; set; }
        public decimal TotalNetProfitPercent { get; set; }
        public Dictionary<string, TimeframeStatsDto> TimeframeStats { get; set; } = new();
    }

    public class TimeframeStatsDto
    {
        public string Timeframe { get; set; } = "";
        public int TotalTrades { get; set; }
        public int SuccessTrades { get; set; }
        public int FailedTrades { get; set; }
        public decimal WinRate { get; set; }
        public decimal NetProfitPercent { get; set; }
    }
}
