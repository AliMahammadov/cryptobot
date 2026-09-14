using System;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Infrastructure.Telegram;

namespace CryptoSense.Worker
{
    public partial class BackgroundMarketScanner
    {
        private async Task MaybeSendHourlyHeartbeatAsync(CancellationToken stoppingToken)
        {
            var nowUtc = DateTime.UtcNow;
            var bakuNowHb = nowUtc.AddHours(4);
            // Pəncərə: Bakı dəqiqə 0-10 (0-2 yox) VƏ “bu Bakı saatında hələ göndərilməyib”
            bool isHourlyWindow = bakuNowHb.Minute >= 0 && bakuNowHb.Minute <= 10;

            if (!isHourlyWindow) return;

            foreach (var kvp in TelegramBotService.UserPreferences)
            {
                var chatId = kvp.Key;
                var s = kvp.Value;
                if (!s.IsActive) continue;

                // Başqa user-in heartbeat-i SuperAdmin çatına GETMƏSİN — Tək qapı ilə yoxlanılır və yalnız bu chatId-yə göndərilir
                if (!await _telegramService.CanReceivePushAsync(chatId)) continue;

                // Eyni chatId-yə saatda 1 heartbeat. Cari Bakı saatında artıq göndərilibsə ötür
                if (s.LastHeartbeatSentUtc != default)
                {
                    var lastSentBaku = s.LastHeartbeatSentUtc.AddHours(4);
                    if (lastSentBaku.Date == bakuNowHb.Date && lastSentBaku.Hour == bakuNowHb.Hour)
                    {
                        continue;
                    }
                }

                lock (_heartbeatLock)
                {
                    if (s.LastHeartbeatSentUtc != default)
                    {
                        var lastSentBaku = s.LastHeartbeatSentUtc.AddHours(4);
                        if (lastSentBaku.Date == bakuNowHb.Date && lastSentBaku.Hour == bakuNowHb.Hour)
                        {
                            continue;
                        }
                    }
                }

                var snapTelemetry = LatestTelemetrySnapshot ?? new ScanTelemetry();
                int activeLocksCount = _coinActiveLocks.Count;
                var btcSnapHb = _livePriceCache.GetSnapshot("BTCUSDT");
                var heartbeatMsg = TelegramMessageFormatter.FormatLiveHeartbeat(
                    chase: snapTelemetry.SkipChase,
                    corr: snapTelemetry.SkipCorr,
                    slWide: snapTelemetry.SkipSL,
                    lowRr: snapTelemetry.SkipRR,
                    activeLocks: activeLocksCount,
                    sent: snapTelemetry.Sent,
                    nextCheckMinutes: 60,
                    dataAgeMsBtc: btcSnapHb?.DataAgeMs ?? -1,
                    skipStale: snapTelemetry.SkipStale,
                    skipLag: snapTelemetry.SkipLag,
                    skipConfluence: snapTelemetry.SkipConfluence,
                    telegramFail: snapTelemetry.TelegramFail,
                    skipGozleme: snapTelemetry.SkipGozleme,
                    skipBtcGate: snapTelemetry.SkipBtcGate,
                    skipBtcRange: snapTelemetry.SkipBtcRange,
                    skipCircuitBreaker: snapTelemetry.SkipCircuitBreaker,
                    skipMaxOpen: snapTelemetry.SkipMaxOpen,
                    skipDailyLoss: snapTelemetry.SkipDailyLoss,
                    maxConfluenceSeen: snapTelemetry.MaxConfluenceSeen);

                if (s.LastHeartbeatMessageId.HasValue)
                {
                    try
                    {
                        await _telegramService.DeleteMessageAsync(chatId, s.LastHeartbeatMessageId.Value);
                    }
                    catch { /* Köhnə mesaj silinə bilməsə belə yeni mesaj mütləq getməlidir */ }
                }

                var newMsgId = await _telegramService.SendMessageReturnIdAsync(heartbeatMsg, chatId);
                if (newMsgId.HasValue)
                {
                    lock (_heartbeatLock)
                    {
                        s.LastHeartbeatSentUtc = DateTime.UtcNow;
                        s.LastHeartbeatMessageId = newMsgId;
                        TelegramBotService.SaveSettings();
                    }
                }
            }
        }

        private static DateTime _lastNewsAlertSentUtc = DateTime.MinValue;
        private static readonly TimeSpan MinNewsAlertInterval = TimeSpan.FromMinutes(3);

        private Task MonitorBreakingNewsAndListingsAsync(CancellationToken stoppingToken)
        {
            // Avtomatik xəbər axını istifadəçi əmri ilə qəti şəkildə dayandırıldı.
            return Task.CompletedTask;
        }
    }
}
