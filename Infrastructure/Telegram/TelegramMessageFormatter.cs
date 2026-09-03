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
            bool isWin = outcomeType.Contains("Hədəf") || outcomeType.Contains("TP") || outcomeType.Contains("Uğurlu") || outcomeType.Contains("Ugurlu") || outcomeType.Contains("Breakeven") || outcomeType.Contains("Qorundu");
            
            if (outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL") || outcomeType.Contains("Bitdi"))
            {
                if (!outcomeType.Contains("Breakeven") && !outcomeType.Contains("Qorundu"))
                {
                    isWin = false;
                }
            }

            if (isWin && profitPct < 0) profitPct = Math.Abs(profitPct);
            if (!isWin && profitPct > 0) profitPct = -Math.Abs(profitPct);

            var icon = isWin ? "🎯" : "⛔";
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
                var statusText = isWin ? $"{outcomeType} (UĞURLU) ✅" : $"{outcomeType} (UĞURSUZ) ❌";
                sb.AppendLine($"{icon} <b>#{userSigNum} NƏTİCƏ HESABATI:</b>");
                sb.AppendLine($"<b>{statusText}</b>");
            }

            sb.AppendLine();
            sb.AppendLine($"🪙 <b>Cütlük:</b> {cleanSymbol} Futures ({directionStr} - {signal.Timeframe})");
            sb.AppendLine($"📍 <b>İlkin Giriş Qiyməti:</b> ${signal.EntryPrice.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"💵 <b>Bağlanış / Cari Qiymət:</b> ${hitPrice.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"📈 <b>Xalis Nəticə (PnL):</b> <b>{(profitPct >= 0 ? "+" : "")}{profitPct.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"🕒 <b>Siqnal Vaxtı:</b> {signal.TimestampFormatted}");
            sb.AppendLine($"🕒 <b>Yenilənmə Vaxtı:</b> {CryptoSense.Domain.Common.TimeHelper.NowFormatted}");

            if (outcomeType.Contains("TP1") || outcomeType.Contains("Hədəf 1"))
            {
                sb.AppendLine();
                sb.AppendLine("🛡️ <b>RISK MENECMENT VƏ DAVAM:</b>");
                sb.AppendLine($"• Stop Loss səviyyəsi <b>GİRİŞ QİYMƏTİNƏ (${signal.EntryPrice.ToString(CultureInfo.InvariantCulture)})</b> çəkildi!");
                sb.AppendLine("• Əməliyyat artıq <b>0 risklidir</b> (Kapital tam qorunur).");
                sb.AppendLine($"• Mövqe açıq saxlanılır, <b>Hədəf 2 (TP2: ${signal.TakeProfit2.ToString(CultureInfo.InvariantCulture)})</b> gözlənilir.");
            }
            else if (outcomeType.Contains("TP2") || outcomeType.Contains("Hədəf 2"))
            {
                sb.AppendLine();
                sb.AppendLine("🛡️ <b>RISK MENECMENT VƏ DAVAM:</b>");
                sb.AppendLine($"• Stop Loss səviyyəsi <b>TP1 (${signal.TakeProfit1.ToString(CultureInfo.InvariantCulture)})</b> səviyyəsinə çəkildi!");
                sb.AppendLine("• Əldə edilmiş qazanc zəmanət altına alındı.");
                sb.AppendLine($"• Mövqe açıq saxlanılır, <b>Hədəf 3 (TP3: ${signal.TakeProfit3.ToString(CultureInfo.InvariantCulture)})</b> gözlənilir.");
            }
            else if (outcomeType.Contains("Breakeven") || outcomeType.Contains("Qorundu"))
            {
                sb.AppendLine();
                sb.AppendLine("🛡️ <b>QEYD:</b> Qiymət giriş nöqtəsinə qayıtdı və mövqe <b>0 zərərlə / qorunmuş qazancla</b> bağlandı.");
            }

            return sb.ToString();
        }

        public static string FormatBtcCompass(BtcMarketCompass compass)
        {
            var sb = new StringBuilder();
            sb.AppendLine("🧭 <b>Bitcoin Makro Bazar Kompası</b>");
            sb.AppendLine($"🕒 <b>Canlı Vaxt:</b> <code>{compass.TimestampFormatted}</code>");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine("💵 <b>CANLI QİYMƏT VƏ 24H STATİSTİKA:</b>");
            sb.AppendLine($"• <b>Cari Qiymət:</b> <code>${compass.Price.ToString("N2", CultureInfo.InvariantCulture)}</code>");
            var changeSign = compass.Change24h >= 0 ? "+" : "";
            var changeIcon = compass.Change24h >= 0 ? "🟢" : "🔴";
            sb.AppendLine($"• <b>24h Dəyişim:</b> <b>{changeSign}{compass.Change24h.ToString("F2", CultureInfo.InvariantCulture)}% {changeIcon}</b>");
            if (compass.High24h > 0 && compass.Low24h > 0)
            {
                sb.AppendLine($"• <b>24h Maksimum:</b> <code>${compass.High24h.ToString("N2", CultureInfo.InvariantCulture)}</code>");
                sb.AppendLine($"• <b>24h Minimum:</b> <code>${compass.Low24h.ToString("N2", CultureInfo.InvariantCulture)}</code>");
            }
            if (compass.VolumeQuote > 0)
            {
                var volBillions = compass.VolumeQuote / 1_000_000_000m;
                sb.AppendLine($"• <b>24h Həcm:</b> <code>${volBillions.ToString("F2", CultureInfo.InvariantCulture)} Milyard USDT</code>");
            }
            sb.AppendLine("-----------------------------------");
            sb.AppendLine("📊 <b>QOBAL BAZAR DOMİNANTLIĞI:</b>");
            if (compass.BtcDominance > 0)
            {
                sb.AppendLine($"• <b>Bitcoin Dominantlığı (BTC.D):</b> <b>{compass.BtcDominance.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
                sb.AppendLine($"• <b>Tether Dominantlığı (USDT.D):</b> <b>{compass.UsdtDominance.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
                var altImpact = compass.BtcDominance >= 58.0m 
                    ? "⚠️ <i>BTC.D yüksəkdir — Altcoinlərdə ehtiyatlı olun.</i>"
                    : "✅ <i>BTC.D stabildir — Altcoinlərdə ticarət üçün əlverişlidir.</i>";
                sb.AppendLine($"• <b>Altcoinlərə Təsiri:</b> {altImpact}");
            }
            else
            {
                sb.AppendLine("• <b>Dominantlıq:</b> <i>Canlı API-dən yenilənir...</i>");
            }
            sb.AppendLine("-----------------------------------");
            sb.AppendLine("📈 <b>CANLI TEXNİKİ DƏRƏCƏLƏR:</b>");
            if (compass.Ema20 > 0 && compass.Ema50 > 0)
            {
                var emaRel = compass.Ema20 > compass.Ema50 ? "EMA20 > EMA50 (Yüksəliş) 🟢" : "EMA20 < EMA50 (Eniş) 🔴";
                sb.AppendLine($"• <b>EMA Strukturu:</b> {emaRel}");
                sb.AppendLine($"  <code>EMA20: ${compass.Ema20.ToString("N2", CultureInfo.InvariantCulture)} | EMA50: ${compass.Ema50.ToString("N2", CultureInfo.InvariantCulture)}</code>");
            }
            if (compass.Rsi15m > 0)
            {
                var rsiStatus = compass.Rsi15m > 70 ? "Aşırı Alış ⚠️" : (compass.Rsi15m < 30 ? "Aşırı Satış ⚠️" : "Sağlam Balans ✅");
                sb.AppendLine($"• <b>RSI (14):</b> <b>{compass.Rsi15m.ToString("F1", CultureInfo.InvariantCulture)}</b> ({rsiStatus})");
            }
            if (compass.MacdHist != 0)
            {
                var macdSign = compass.MacdHist > 0 ? "+" : "";
                var macdDesc = compass.MacdHist > 0 ? "Alıcı Təzyiqi 🟢" : "Satıcı Təzyiqi 🔴";
                sb.AppendLine($"• <b>MACD Histogram:</b> <code>{macdSign}{compass.MacdHist.ToString("F2", CultureInfo.InvariantCulture)}</code> ({macdDesc})");
            }
            if (compass.SupportLevel > 0 && compass.ResistanceLevel > 0)
            {
                sb.AppendLine($"• <b>Lokal Səviyyələr:</b> Dəstək <code>${compass.SupportLevel.ToString("N2", CultureInfo.InvariantCulture)}</code> | Müqavimət <code>${compass.ResistanceLevel.ToString("N2", CultureInfo.InvariantCulture)}</code>");
            }
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"📌 <b>Ümumi Trend İstiqaməti:</b> <b>{compass.Trend}</b>");

            return sb.ToString();
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
            sb.AppendLine("📈 <b>Coinlər Üzrə Qlobal Win-Rate və Dərin Statistika</b>");
            sb.AppendLine("<i>(Canlı verilənlər bazasındakı bütün tamamlanmış və açıq əməliyyatlar)</i>");
            sb.AppendLine("-----------------------------------");

            var tradedCoins = breakdown.Where(c => c.TotalTrades > 0).ToList();
            int totalActive = breakdown.Sum(c => c.ActiveTrades);

            if (tradedCoins.Count == 0)
            {
                sb.AppendLine("ℹ️ <b>Tamamlanmış Əməliyyatlar:</b> 0 ədəd");
                sb.AppendLine("<i>Hal-hazırda sistem 50 coin üzrə canlı skan edir. Şamlar bağlandıqca və TP/SL hədəfləri vurduqca qələbə faizləri burada canlı toplanacaq.</i>");
                sb.AppendLine();
                if (totalActive > 0)
                {
                    sb.AppendLine($"🟡 <b>Hal-hazırda bazarda izlənən açıq əməliyyatlar:</b> <b>{totalActive} ədəd</b>");
                    sb.AppendLine();
                }
            }
            else
            {
                int totalAllTrades = tradedCoins.Sum(c => c.TotalTrades);
                int totalAllSuccess = tradedCoins.Sum(c => c.SuccessTrades);
                int totalAllFailed = tradedCoins.Sum(c => c.FailedTrades);
                decimal globalWinRate = totalAllTrades > 0 ? Math.Round(((decimal)totalAllSuccess / totalAllTrades) * 100, 1) : 0;
                decimal globalPnL = Math.Round(tradedCoins.Sum(c => c.TotalNetProfitPercent), 2);

                sb.AppendLine($"📊 <b>Ümumi Əməliyyatlar:</b> {totalAllTrades} ədəd | Win-Rate: <b>{globalWinRate.ToString("F1", CultureInfo.InvariantCulture)}%</b>");
                sb.AppendLine($"✅ Uğurlu (TP): <b>{totalAllSuccess}</b> | ❌ Uğursuz (SL): <b>{totalAllFailed}</b>");
                if (totalActive > 0) sb.AppendLine($"🟡 Açıq İzlənən: <b>{totalActive} ədəd</b>");
                sb.AppendLine($"📈 <b>Xalis Nəticə (PnL):</b> <b>{(globalPnL >= 0 ? "+" : "")}{globalPnL.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
                sb.AppendLine("-----------------------------------");

                foreach (var coin in tradedCoins.OrderByDescending(c => c.TotalTrades).ThenByDescending(c => c.OverallWinRate))
                {
                    var cleanSym = coin.CleanSymbol;
                    var icon = coin.OverallWinRate >= 70 ? "🟢" : (coin.OverallWinRate >= 50 ? "🟡" : "🔴");
                    var activeNote = coin.ActiveTrades > 0 ? $" | 🟡 {coin.ActiveTrades} Açıq" : "";
                    sb.AppendLine($"🪙 <b>{cleanSym}: {coin.OverallWinRate.ToString("F1", CultureInfo.InvariantCulture)}%</b> {icon} ({coin.TotalTrades} əməliyyat: {coin.SuccessTrades} TP, {coin.FailedTrades} SL{activeNote})");

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

            var cleanMonitored = monitoredCoins.Select(c => c.Replace("USDT", "")).Distinct().ToList();
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"🌐 <b>Canlı İzlənilən Coinlər ({cleanMonitored.Count} ədəd):</b>");
            sb.AppendLine($"<code>{string.Join(", ", cleanMonitored)}</code>");
            sb.AppendLine();
            sb.AppendLine("💡 <i>Nəticələr hər bir şam tamamlandıqca avtomatik olaraq SQLite bazasında qeyd olunur.</i>");

            return sb.ToString();
        }
    }
}
