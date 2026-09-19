using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Entities;
using CryptoSense.Infrastructure.Telegram;
using Microsoft.Extensions.DependencyInjection;

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

                // Bu saatda ən azı 1 yeni siqnal gedibsə bu raport GÖNDƏRİLMƏSİN (siqnal kartı kifayətdir)
                if (snapTelemetry.Sent > 0)
                {
                    lock (_heartbeatLock)
                    {
                        s.LastHeartbeatSentUtc = DateTime.UtcNow;
                        TelegramBotService.SaveSettings();
                    }
                    continue;
                }

                // Koin dəsti = o istifadəçinin/kanalın aktiv siyahısı (40 baza + əlavə seçilənlər). Hamısı. Heç birini atma.
                var monitoredCoins = new List<string>();
                foreach (var c in TelegramBotService.Default40Coins)
                {
                    monitoredCoins.Add(c);
                }

                int extraUserCoinsCount = 0;
                if (s.CustomCoins != null)
                {
                    foreach (var c in s.CustomCoins)
                    {
                        var norm = c.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? c.ToUpperInvariant() : c.ToUpperInvariant() + "USDT";
                        if (!monitoredCoins.Contains(norm))
                        {
                            monitoredCoins.Add(norm);
                            extraUserCoinsCount++;
                        }
                    }
                }
                if (s.Coins != null)
                {
                    foreach (var c in s.Coins)
                    {
                        var norm = c.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? c.ToUpperInvariant() : c.ToUpperInvariant() + "USDT";
                        if (!monitoredCoins.Contains(norm))
                        {
                            monitoredCoins.Add(norm);
                            extraUserCoinsCount++;
                        }
                    }
                }

                string[] targetTfs;
                if (s.Timeframe == "4h")
                {
                    targetTfs = new[] { "4h" };
                }
                else if (s.Timeframe == "1h")
                {
                    targetTfs = new[] { "1h" };
                }
                else
                {
                    targetTfs = new[] { "1h", "4h" };
                }

                var coinSkipList = new List<CoinSkipDetail>();
                foreach (var sym in monitoredCoins)
                {
                    foreach (var tf in targetTfs)
                    {
                        var evalKey = $"{sym}|{tf}";
                        if (_latestCoinEvaluations.TryGetValue(evalKey, out var eval))
                        {
                            coinSkipList.Add(eval);
                        }
                        else
                        {
                            coinSkipList.Add(new CoinSkipDetail
                            {
                                Symbol = sym,
                                Timeframe = tf,
                                Bias = "neytral",
                                ConfluenceScore = 0,
                                GroupReason = "scan yox",
                                ReasonDescription = "bu saat baxılmayıb"
                            });
                        }
                    }
                }

                var tfDisplay = (s.Timeframe == "4h") ? "4h" : (s.Timeframe == "1h" ? "1h" : "1h, 4h");

                BtcMarketCompass? compass = null;
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var sigEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
                    compass = await sigEngine.GetBtcCompassAsync();
                }
                catch { }

                var reportMsgs = TelegramMessageFormatter.FormatHourSkipReport(
                    candleCloseUtc: nowUtc,
                    timeframe: tfDisplay,
                    baseCoinsCount: TelegramBotService.Default40Coins.Count * targetTfs.Length,
                    userExtraCoinsCount: extraUserCoinsCount * targetTfs.Length,
                    compass: compass,
                    coins: coinSkipList,
                    telemetry: snapTelemetry,
                    newSignalsCount: snapTelemetry.Sent);

                if (reportMsgs.Count == 0) continue;

                if (s.LastHeartbeatMessageId.HasValue)
                {
                    try
                    {
                        await _telegramService.DeleteMessageAsync(chatId, s.LastHeartbeatMessageId.Value);
                    }
                    catch { /* Köhnə mesaj silinə bilməsə belə yeni mesaj mütləq getməlidir */ }
                }

                long? lastMsgId = null;
                foreach (var msgPart in reportMsgs)
                {
                    var newMsgId = await _telegramService.SendMessageReturnIdAsync(msgPart, chatId);
                    if (newMsgId.HasValue)
                    {
                        lastMsgId = newMsgId;
                    }

                    var effectiveUsername = _telegramService.GetEffectiveUsername(chatId, s.Username);
                    _ = _telegramService.MirrorToChannelIfUserbotAsync(effectiveUsername, chatId, msgPart);
                }

                if (lastMsgId.HasValue)
                {
                    lock (_heartbeatLock)
                    {
                        s.LastHeartbeatSentUtc = DateTime.UtcNow;
                        s.LastHeartbeatMessageId = lastMsgId;
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
