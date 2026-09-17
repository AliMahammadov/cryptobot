using System;

namespace CryptoSense.Worker
{
    public partial class BackgroundMarketScanner
    {
        public class ScanTelemetry
        {
            public int CoinsScanned;
            public int Sent;
            public int SkipChase;
            public int SkipCorr;
            public int SkipSL;
            public int SkipRR;
            public int SkipLock;
            public int SkipLag;
            public int SkipStale;
            public int SkipConfluence;  // Confluence < 75 (BLOCK, not PASS)
            public int SkipGozleme;     // GÖZLƏMƏ ⚪ (market neutral, ADX < 16, no setup)
            public int SkipVolume;        // SKIP_VOLUME
            public int SkipBtcResidual;   // SKIP_BTC_RESIDUAL
            public int SkipBtcBearLong; // SKIP_BTC_BEAR_LONG
            public int SkipBtcRange;    // SKIP_BTC_RANGE
            public int SkipBtc4hOppose; // SKIP_BTC_4H_OPPOSE
            public int SkipHourCap;     // morningCap >=1 OR hourCap >=3
            public int TelegramFail;    // SendSignalAlertAsync returned false
            public int SkipCircuitBreaker;
            public int SkipMaxOpen;
            public int SkipDailyLoss;
            public int SkipStaleTrend;  // SKIP_STALE_TREND
            public int SkipBtcBounce;   // SKIP_BTC_BOUNCE
            public int SkipDirLock;     // SKIP_DIR_LOCK
            public decimal MaxConfluenceSeen = -1m;

            public int SkipBtcGate => SkipBtcBearLong + SkipBtc4hOppose + SkipBtcResidual;

            public ScanTelemetry Clone() => new ScanTelemetry
            {
                CoinsScanned = this.CoinsScanned,
                Sent = this.Sent,
                SkipChase = this.SkipChase,
                SkipCorr = this.SkipCorr,
                SkipSL = this.SkipSL,
                SkipRR = this.SkipRR,
                SkipLock = this.SkipLock,
                SkipLag = this.SkipLag,
                SkipStale = this.SkipStale,
                SkipConfluence = this.SkipConfluence,
                SkipGozleme = this.SkipGozleme,
                SkipVolume = this.SkipVolume,
                SkipBtcResidual = this.SkipBtcResidual,
                SkipBtcBearLong = this.SkipBtcBearLong,
                SkipBtcRange = this.SkipBtcRange,
                SkipBtc4hOppose = this.SkipBtc4hOppose,
                SkipHourCap = this.SkipHourCap,
                TelegramFail = this.TelegramFail,
                SkipCircuitBreaker = this.SkipCircuitBreaker,
                SkipMaxOpen = this.SkipMaxOpen,
                SkipDailyLoss = this.SkipDailyLoss,
                SkipStaleTrend = this.SkipStaleTrend,
                SkipBtcBounce = this.SkipBtcBounce,
                SkipDirLock = this.SkipDirLock,
                MaxConfluenceSeen = this.MaxConfluenceSeen
            };

            public void Reset()
            {
                CoinsScanned = 0;
                Sent = 0;
                SkipChase = 0;
                SkipCorr = 0;
                SkipSL = 0;
                SkipRR = 0;
                SkipLock = 0;
                SkipLag = 0;
                SkipStale = 0;
                SkipConfluence = 0;
                SkipGozleme = 0;
                SkipVolume = 0;
                SkipBtcResidual = 0;
                SkipBtcBearLong = 0;
                SkipBtcRange = 0;
                SkipBtc4hOppose = 0;
                SkipHourCap = 0;
                TelegramFail = 0;
                SkipCircuitBreaker = 0;
                SkipMaxOpen = 0;
                SkipDailyLoss = 0;
                SkipStaleTrend = 0;
                SkipBtcBounce = 0;
                SkipDirLock = 0;
                MaxConfluenceSeen = -1m;
            }
        }
    }
}
