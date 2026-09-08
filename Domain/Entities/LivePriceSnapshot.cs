using System;

namespace CryptoSense.Domain.Entities
{
    public class LivePriceSnapshot
    {
        public string Symbol { get; set; } = "";
        public decimal Last { get; set; }
        public decimal SessionHigh { get; set; }
        public decimal SessionLow { get; set; }
        public long ExchangeTsMs { get; set; }
        public long LocalReceiveTsMs { get; set; }
        public string Source { get; set; } = "ws_last"; // "ws_last" | "rest_fallback"

        /// <summary>
        /// True age in milliseconds since trade executed on Binance matching engine.
        /// Synchronized against exchange server time, showing real physical transit & queuing latency (never 0ms).
        /// </summary>
        public long DataAgeMs
        {
            get
            {
                long nowExchange = CryptoSense.Application.Services.LivePriceCache.CurrentExchangeTimeMs;
                long age = nowExchange - ExchangeTsMs;
                if (age > 0) return age;
                long localElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LocalReceiveTsMs;
                return localElapsed > 0 ? localElapsed : 1;
            }
        }
    }
}
