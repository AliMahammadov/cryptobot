using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CryptoSense.Application.DTOs;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;

namespace CryptoSense.Infrastructure.Telegram
{
    public static class TelegramMessageFormatter
    {
        public static string FormatSignalAlert(FuturesSignal signal, int userSigNum)
        {
            var isLong = signal.Direction == SignalDirection.Buy || signal.SignalType.Contains("LONG");
            var cleanSymbol = signal.Symbol.Replace("USDT", "");
            var statusIcon = isLong ? "🟢" : "🔴";
            var sentimentText = signal.NewsSentimentImpact.Contains("BULLISH") || signal.NewsSentimentImpact.Contains("MUSBET") || signal.NewsSentimentImpact.Contains("MÜSBƏT") ? "Müsbət 🟢" : (signal.NewsSentimentImpact.Contains("BEARISH") || signal.NewsSentimentImpact.Contains("MENFI") || signal.NewsSentimentImpact.Contains("MƏNFİ") ? "Mənfi 🔴" : "Neytral ⚪");

            var sb = new StringBuilder();
            sb.AppendLine($"#{userSigNum} {statusIcon} <b>SİQNAL</b>");
            sb.AppendLine();
            sb.AppendLine($"🪙 <b>Coin:</b> {cleanSymbol} Futures ({signal.Timeframe})");
            sb.AppendLine($"🕒 <b>Verilmə Tarixi:</b> {signal.TimestampFormatted}");
            sb.AppendLine($"🎯 <b>Confluence Razılaşma Balı:</b> <b>{signal.ConfluenceScore.ToString("F1", CultureInfo.InvariantCulture)}%</b> (İndiqatorların razılığı)");
            sb.AppendLine($"💵 <b>Cari Giriş Qiyməti:</b> ${signal.CurrentPrice.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"📰 <b>Xəbər Sentimenti:</b> {sentimentText}");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"📍 <b>Giriş Zonası:</b> ${signal.EntryLow.ToString(CultureInfo.InvariantCulture)} - ${signal.EntryHigh.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"🎯 <b>Hədəf 1 (TP1):</b> ${signal.TakeProfit1.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"🎯 <b>Hədəf 2 (TP2):</b> ${signal.TakeProfit2.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"🌟 <b>Hədəf 3 (TP3):</b> ${signal.TakeProfit3.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"⛔ <b>Stop Loss (SL):</b> ${signal.StopLoss.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine("-----------------------------------");
            return sb.ToString();
        }

        public static string FormatOutcomeAlert(FuturesSignal signal, int userSigNum, string outcomeType, decimal hitPrice, decimal profitPct)
        {
            bool isWin = outcomeType.Contains("Hədəf") || outcomeType.Contains("TP") || outcomeType.Contains("Uğurlu") || outcomeType.Contains("Ugurlu");
            
            if (outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL") || outcomeType.Contains("Bitdi"))
            {
                isWin = false;
            }

            if (isWin && profitPct < 0) profitPct = Math.Abs(profitPct);
            if (!isWin && profitPct > 0) profitPct = -Math.Abs(profitPct);

            var icon = isWin ? "🎯" : "⛔";
            var statusText = isWin ? $"{outcomeType} (UĞURLU) ✅" : $"{outcomeType} (UĞURSUZ) ❌";
            var cleanSymbol = signal.Symbol.Replace("USDT", "");
            var directionStr = (signal.Direction == SignalDirection.Buy || signal.SignalType.Contains("LONG")) ? "LONG" : "SHORT";

            var sb = new StringBuilder();
            if (outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL"))
            {
                sb.AppendLine($"⛔ <b>#{userSigNum} NÖMRƏLİ SİQNAL ÜZRƏ STOP-LOSS (SL) VURDU!</b>");
                sb.AppendLine($"<b>Stop Loss (SL) (UĞURSUZ) ❌</b>");
                sb.AppendLine();
                sb.AppendLine($"⚠️ <b>Təcili əməliyyatı dayandırın!</b>");
            }
            else
            {
                sb.AppendLine($"{icon} <b>#{userSigNum} NƏTİCƏ HESABATI:</b>");
                sb.AppendLine($"<b>{statusText}</b>");
            }
            sb.AppendLine();
            sb.AppendLine($"🪙 <b>Cütlük:</b> {cleanSymbol} Futures ({directionStr} - {signal.Timeframe})");
            sb.AppendLine($"📍 <b>İlkin Giriş Qiyməti:</b> ${signal.EntryPrice.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"💵 <b>Bağlanış Qiyməti:</b> ${hitPrice.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"📈 <b>Xalis Nəticə (PnL):</b> <b>{(profitPct >= 0 ? "+" : "")}{profitPct.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"🕒 <b>Siqnal Vaxtı:</b> {signal.TimestampFormatted}");
            sb.AppendLine($"🕒 <b>Bağlanma Vaxtı:</b> {CryptoSense.Domain.Common.TimeHelper.NowFormatted}");
            return sb.ToString();
        }

        public static string FormatBtcCompass(BtcMarketCompass compass)
        {
            return $"🧭 <b>Bitcoin Bazar Kompası</b>\n\n" +
                   $"🕒 Tarix: <b>{compass.TimestampFormatted}</b>\n" +
                   $"💵 Cari Qiymət: <b>${compass.Price.ToString(CultureInfo.InvariantCulture)}</b>\n" +
                   $"📈 Trend İstiqaməti: <b>{compass.Trend}</b>\n" +
                   $"🎯 Təhlil Gücü: <b>{compass.BullishScore}%</b>\n\n" +
                   $"<i>{compass.Summary}</i>";
        }

        public static string FormatNewsSentiment(NewsSentimentSummary newsSummary)
        {
            var sb = new StringBuilder();
            sb.AppendLine("📰 <b>Qlobal Kripto Xəbərləri & Sentimenti (Azərbaycan Dilində)</b>");
            sb.AppendLine($"📊 Ümumi Bazar Əhvalı: <b>{newsSummary.Status} ({newsSummary.OverallScore}%)</b>");
            sb.AppendLine();
            sb.AppendLine("Son xəbərlər (keçid üçün xəbərə klikləyin):");
            sb.AppendLine("-----------------------------------");
            foreach (var n in newsSummary.LatestNews.Take(5))
            {
                var safeTitle = System.Net.WebUtility.HtmlEncode(n.Title);
                sb.AppendLine($"• <b>[{n.Source}]</b> <a href=\"{n.Url}\">{safeTitle}</a> - <i>{n.Sentiment}</i>");
            }
            sb.AppendLine("-----------------------------------");
            return sb.ToString();
        }

        public static string FormatPerformanceStats(PerformanceStats stats)
        {
            var sb = new StringBuilder();
            sb.AppendLine("📊 <b>Canlı Statistik Performans:</b>");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"📌 <b>Ümumi Analizlər:</b> {stats.TotalSignals} ədəd");
            sb.AppendLine($"🟡 <b>Açıq İzlənən:</b> {stats.OpenSignals} ədəd");
            sb.AppendLine($"✅ <b>Uğurlu (Hədəfə Çatan):</b> {stats.SuccessSignals} ədəd");
            sb.AppendLine($"❌ <b>Uğursuz:</b> {stats.FailedSignals} ədəd");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"🎯 <b>Real Qələbə Faizi (Win Rate):</b> <b>{stats.WinRatePercent.ToString("F1", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"📈 <b>Xalis Nəticə (PnL):</b> <b>{(stats.TotalNetProfitPercent >= 0 ? "+" : "")}{stats.TotalNetProfitPercent.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"📊 <b>Orta Əməliyyat Gəliri:</b> {(stats.AvgProfitPerTradePercent >= 0 ? "+" : "")}{stats.AvgProfitPerTradePercent.ToString("F2", CultureInfo.InvariantCulture)}%");
            sb.AppendLine("-----------------------------------");
            return sb.ToString();
        }

        public static string FormatUserList(List<UserAccount> users)
        {
            var sb = new StringBuilder();
            sb.AppendLine("👥 <b>Sistemdəki Qeydiyyatlı İstifadəçilər:</b>");
            sb.AppendLine($"Ümumi say: <b>{users.Count} nəfər</b>");
            sb.AppendLine("-----------------------------------");
            int index = 1;
            foreach (var u in users)
            {
                var tgName = !string.IsNullOrEmpty(u.TelegramUsername) ? $"@{u.TelegramUsername}" : (u.TelegramChatId != null ? $"ID: {u.TelegramChatId}" : "Daxil olmayıb");
                sb.AppendLine($"{index}. <b>{u.Username}</b> | Rol: <code>{u.Role}</code> | Status: Aktiv 🟢");
                sb.AppendLine($"   Telegram: <code>{tgName}</code>");
                index++;
            }
            sb.AppendLine("-----------------------------------");
            return sb.ToString();
        }

        public static string FormatCoinPerformanceBreakdown(List<CryptoSense.Application.DTOs.CoinPerformanceBreakdownDto> breakdown, List<string> monitoredCoins)
        {
            var sb = new StringBuilder();
            sb.AppendLine("📈 <b>Coinlər Üzrə Dərin Win-Rate Statistikası</b>");
            sb.AppendLine("<i>(Bütün istifadəçilərə göndərilən qlobal sistem siqnalları üzrə)</i>");
            sb.AppendLine("-----------------------------------");

            if (breakdown.Count == 0)
            {
                sb.AppendLine("ℹ️ <i>Hələ qeydə alınmış əməliyyat nəticəsi yoxdur.</i>");
            }
            else
            {
                foreach (var coin in breakdown)
                {
                    var cleanSym = coin.CleanSymbol;
                    if (coin.TotalTrades == 0)
                    {
                        sb.AppendLine($"🪙 <b>{cleanSym}:</b> <i>Hələ tamamlanmış əməliyyat yoxdur</i>");
                        continue;
                    }

                    var icon = coin.OverallWinRate >= 70 ? "🟢" : (coin.OverallWinRate >= 50 ? "🟡" : "🔴");
                    sb.AppendLine($"🪙 <b>{cleanSym}: {coin.OverallWinRate.ToString("F1", CultureInfo.InvariantCulture)}%</b> {icon} ({coin.TotalTrades} əməliyyat: {coin.SuccessTrades} Uğurlu, {coin.FailedTrades} Uğursuz)");

                    var sortedTfs = coin.TimeframeStats.OrderBy(t => t.Key switch
                    {
                        "1m" => 1,
                        "3m" => 2,
                        "5m" => 3,
                        "15m" => 4,
                        "1h" => 5,
                        "4h" => 6,
                        _ => 10
                    });

                    foreach (var tf in sortedTfs)
                    {
                        var tfIcon = tf.Value.WinRate >= 70 ? "✅" : (tf.Value.WinRate >= 50 ? "🟡" : "❌");
                        sb.AppendLine($"   • <b>{tf.Key}:</b> {tf.Value.WinRate.ToString("F1", CultureInfo.InvariantCulture)}% ({tf.Value.SuccessTrades}/{tf.Value.TotalTrades}) {tfIcon}");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("-----------------------------------");
            var cleanMonitored = monitoredCoins.Select(c => c.Replace("USDT", "")).Distinct();
            sb.AppendLine($"🌐 <b>Sistemin Canlı İzlədiyi Coinlər ({cleanMonitored.Count()} ədəd):</b>");
            sb.AppendLine($"<code>{string.Join(", ", cleanMonitored)}</code>");
            sb.AppendLine();
            sb.AppendLine("💡 <i>Qeyd: Hər bir coin üzrə həm ümumi, həm də ayrı-ayrı şam çərçivələrindəki real nəticələr göstərilir.</i>");

            return sb.ToString();
        }
    }
}
