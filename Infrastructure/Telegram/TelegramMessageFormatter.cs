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
            var directionText = isLong ? "AL (LONG)" : "SAT (SHORT)";
            var sentimentText = signal.NewsSentimentImpact.Contains("BULLISH") || signal.NewsSentimentImpact.Contains("MUSBET") || signal.NewsSentimentImpact.Contains("MÜSBƏT") ? "Müsbət 🟢" : (signal.NewsSentimentImpact.Contains("BEARISH") || signal.NewsSentimentImpact.Contains("MENFI") || signal.NewsSentimentImpact.Contains("MƏNFİ") ? "Mənfi 🔴" : "Neytral ⚪");

            var sb = new StringBuilder();
            sb.AppendLine($"#{userSigNum} {statusIcon} <b>SİQNAL</b>");
            sb.AppendLine();
            sb.AppendLine($"🪙 <b>Cütlük:</b> {cleanSymbol} Futures ({signal.Timeframe})");
            sb.AppendLine($"🧭 <b>İstiqamət:</b> <b>{directionText}</b>");
            sb.AppendLine($"⏱ <b>Zaman Çərçivəsi:</b> {signal.Timeframe}");
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
            sb.AppendLine("📊 <b>Texniki Əsaslandırma:</b>");
            foreach (var r in signal.AnalysisReasons)
            {
                sb.AppendLine($"- {r}");
            }
            sb.AppendLine("-----------------------------------");
            sb.AppendLine("⚠️ <i>Bu maliyyə məsləhəti deyil. Confluence balı indiqatorların razılığıdır, uğur zəmanəti deyil. Risk idarəetməsinə riayət edin.</i>");
            return sb.ToString();
        }

        public static string FormatOutcomeAlert(FuturesSignal signal, int userSigNum, string outcomeType, decimal hitPrice, decimal profitPct)
        {
            bool isWin;
            if (outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL") || outcomeType.Contains("Mənfi") || outcomeType.Contains("Menfi"))
            {
                isWin = false;
                if (profitPct > 0) profitPct = -Math.Abs(profitPct);
            }
            else if (outcomeType.Contains("Hədəf") || outcomeType.Contains("TP") || outcomeType.Contains("Müsbət") || outcomeType.Contains("Musbet"))
            {
                isWin = true;
                if (profitPct < 0) profitPct = Math.Abs(profitPct);
            }
            else
            {
                isWin = profitPct > 0.05m;
            }

            var icon = isWin ? "🎯" : (Math.Abs(profitPct) <= 0.05m ? "⚪" : "⛔");
            var statusText = isWin ? $"{outcomeType} (UĞURLU) ✅" : (Math.Abs(profitPct) <= 0.05m ? $"{outcomeType} (NEYTRAL) ⚪" : $"{outcomeType} (UĞURSUZ) ❌");
            var cleanSymbol = signal.Symbol.Replace("USDT", "");
            var directionStr = (signal.Direction == SignalDirection.Buy || signal.SignalType.Contains("LONG")) ? "LONG" : "SHORT";

            var sb = new StringBuilder();
            sb.AppendLine($"{icon} <b>#{userSigNum} NƏTİCƏ HESABATI:</b>");
            sb.AppendLine($"<b>{statusText}</b>");
            sb.AppendLine();
            sb.AppendLine($"🪙 <b>Cütlük:</b> {cleanSymbol} Futures ({directionStr} - {signal.Timeframe})");
            sb.AppendLine($"📍 <b>İlkin Giriş Qiyməti:</b> ${signal.EntryPrice.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"💵 <b>Bağlanış Qiyməti:</b> ${hitPrice.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"📈 <b>Xalis Nəticə (PnL):</b> <b>{(profitPct >= 0 ? "+" : "")}{profitPct.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"🕒 <b>Siqnal Vaxtı:</b> {signal.TimestampFormatted}");
            sb.AppendLine($"🕒 <b>Bağlanma Vaxtı:</b> {DateTime.Now:dd.MM.yyyy | HH:mm:ss}");
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
            sb.AppendLine("📊 <b>Sistemin Real Statistik Performansı (Şəffaf İzləmə):</b>");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"📌 <b>Ümumi Siqnallar:</b> {stats.TotalSignals} ədəd");
            sb.AppendLine($"🟡 <b>Açıq İzlənən:</b> {stats.OpenSignals} ədəd");
            sb.AppendLine($"✅ <b>Uğurlu (TP/Müsbət):</b> {stats.SuccessSignals} ədəd");
            sb.AppendLine($"❌ <b>Uğursuz (SL/Mənfi):</b> {stats.FailedSignals} ədəd");
            sb.AppendLine($"⚪ <b>Neytral:</b> {stats.NeutralSignals} ədəd");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"🎯 <b>Real Qələbə Faizi (Win Rate):</b> <b>{stats.WinRatePercent.ToString("F1", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"📈 <b>Ümumi Xalis PnL:</b> <b>{(stats.TotalNetProfitPercent >= 0 ? "+" : "")}{stats.TotalNetProfitPercent.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"📊 <b>Orta Əməliyyat Gəliri:</b> {(stats.AvgProfitPerTradePercent >= 0 ? "+" : "")}{stats.AvgProfitPerTradePercent.ToString("F2", CultureInfo.InvariantCulture)}%");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine("<i>Qeyd: Bütün nəticələr (uğurlu və uğursuz) verilənlər bazasında dəqiq və şəffaf şəkildə qeyd olunur.</i>");
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
    }
}
