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
            // Decisive outcome evaluation: Only TWO outcomes exist - UĞURLU ✅ or UĞURSUZ ❌
            bool isExplicitFail = outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL") || signal.Status == SignalStatus.Failed || profitPct < 0;
            bool isExplicitWin = signal.Status == SignalStatus.Success || outcomeType.Contains("Hədəf") || outcomeType.Contains("TP") || outcomeType.Contains("Uğurlu") || outcomeType.Contains("Ugurlu") || outcomeType.Contains("Breakeven") || outcomeType.Contains("Qorundu") || signal.Tp1Notified || profitPct >= 0;

            bool isWin = isExplicitWin && !isExplicitFail;
            if (outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL"))
            {
                isWin = false;
            }

            if (!isWin && profitPct > 0)
            {
                profitPct = -Math.Abs(profitPct);
            }

            var icon = isWin ? "🎯" : "⛔";
            var cleanSymbol = signal.Symbol.Replace("USDT", "");
            var directionStr = (signal.Direction == SignalDirection.Buy || signal.SignalType.Contains("LONG")) ? "LONG" : "SHORT";

            var sb = new StringBuilder();
            if (outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL") || !isWin)
            {
                sb.AppendLine($"⛔ <b>#{userSigNum} NƏTİCƏ HESABATI</b>");
                sb.AppendLine($"📌 <b>#{userSigNum} nömrəli əməliyyat üzrə: {(outcomeType.Contains("Stop Loss") ? "Stop-Loss vurdu" : outcomeType)} (UĞURSUZ OLDU) ❌</b>");
                sb.AppendLine();
                sb.AppendLine($"⚠️ <b>Təcili əməliyyatı dayandırın!</b>");
            }
            else if (outcomeType.Contains("Müddəti") || outcomeType.Contains("Bitdi") || outcomeType.Contains("Tamamlandı"))
            {
                var signStr = profitPct >= 0 ? "+" : "";
                sb.AppendLine($"🎯 <b>#{userSigNum} NƏTİCƏ HESABATI (MÜDDƏT BİTDİ)</b>");
                sb.AppendLine($"📌 <b>#{userSigNum} nömrəli əməliyyat üzrə: {outcomeType} ({signStr}{profitPct.ToString("F2", CultureInfo.InvariantCulture)}%) ⚪</b>");
            }
            else
            {
                var statusText = outcomeType.Contains("✅") ? outcomeType : $"{outcomeType} (UĞURLU OLDU) ✅";
                sb.AppendLine($"🎯 <b>#{userSigNum} NƏTİCƏ HESABATI</b>");
                sb.AppendLine($"📌 <b>#{userSigNum} nömrəli əməliyyat üzrə: {statusText}</b>");
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
                sb.AppendLine("🛡️ <b>PARTİAL CLOSE (50% BAĞLANDI) & RİSK MENECMENT:</b>");
                sb.AppendLine("• Mövqenin <b>50%-i TP1 SƏVİYYƏSİNDƏ QAZANCLA BAĞLANDI ✅</b>");
                sb.AppendLine($"• Stop Loss dərhal <b>GİRİŞƏ (${signal.EntryPrice.ToString(CultureInfo.InvariantCulture)})</b> çəkildi (Sıfır Risk)!");
                sb.AppendLine($"• Qalan <b>50%</b> mövqe ilə <b>Hədəf 2 (TP2: ${signal.TakeProfit2.ToString(CultureInfo.InvariantCulture)})</b> gözlənilir.");
            }
            else if (outcomeType.Contains("TP2") || outcomeType.Contains("Hədəf 2"))
            {
                sb.AppendLine();
                sb.AppendLine("🛡️ <b>PARTİAL CLOSE (25% ƏLAVƏ BAĞLANDI) & TRAILING STOP:</b>");
                sb.AppendLine("• Qalan mövqenin 50%-i (İlkin mövqenin <b>25%-i</b>) <b>QAZANCLA BAĞLANDI ✅</b>");
                sb.AppendLine($"• Stop Loss <b>TP1 (${signal.TakeProfit1.ToString(CultureInfo.InvariantCulture)})</b> səviyyəsinə qaldırıldı (Trailing)!");
                sb.AppendLine($"• Qalan son <b>25%</b> mövqe ilə <b>Hədəf 3 (TP3: ${signal.TakeProfit3.ToString(CultureInfo.InvariantCulture)})</b> gözlənilir.");
            }
            else if (outcomeType.Contains("TP3") || outcomeType.Contains("Hədəf 3"))
            {
                sb.AppendLine();
                sb.AppendLine("🏆 <b>TAM HƏDƏFƏ ÇATILDI:</b> Mövqe maksimum mənfəətlə 100% bağlandı.");
            }
            else if (outcomeType.Contains("Breakeven") || outcomeType.Contains("Qorundu") || signal.IsPartial1Closed)
            {
                sb.AppendLine();
                sb.AppendLine("🛡️ <b>PARTİAL CLOSE NƏTİCƏSİ & RİSK MENECMENT:</b>");
                if (signal.IsPartial2Closed)
                {
                    sb.AppendLine("• <b>TP1 Səviyyəsində:</b> Mövqenin <b>50%-i QAZANCLA BAĞLANIB ✅</b>");
                    sb.AppendLine("• <b>TP2 Səviyyəsində:</b> Mövqenin <b>25%-i ƏLAVƏ QAZANCLA BAĞLANIB ✅</b>");
                    sb.AppendLine("• <b>Qalan 25% Pay:</b> Trailing Stop (TP1) səviyyəsində qorunaraq bağlandı.");
                }
                else if (signal.IsPartial1Closed)
                {
                    sb.AppendLine("• <b>TP1 Səviyyəsində:</b> Mövqenin <b>50%-i QAZANCLA BAĞLANIB ✅</b>");
                    sb.AppendLine("• <b>Qalan 50% Pay:</b> Giriş qiymətində (Breakeven) sıfır risklə qorunaraq bağlandı.");
                }
                else
                {
                    sb.AppendLine("• Mövqe giriş qiymətində (Breakeven) risksiz bağlandı.");
                }
                sb.AppendLine($"• <b>Ümumi Realizə Olunan Xalis Qazanc:</b> <b>{(profitPct >= 0 ? "+" : "")}{profitPct.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
            }
            else if (outcomeType.Contains("Müddəti") || outcomeType.Contains("Bitdi"))
            {
                sb.AppendLine();
                sb.AppendLine("⏱️ <b>MÜDDƏT BİTMƏSİ MENECMENTİ:</b>");
                sb.AppendLine("• Müəyyən olunmuş maksimal gözləmə müddəti tamamlandı.");
                sb.AppendLine("• TP və ya SL səviyyələrinə çatmadan mövqe cari bazar qiyməti ilə bağlandı.");
                sb.AppendLine($"• <b>Realizə Olunan Nəticə:</b> <b>{(profitPct >= 0 ? "+" : "")}{profitPct.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
            }

            return sb.ToString();
        }

        public static string FormatVolatilityRiskAlert(string symbol, decimal currentPrice, decimal priceChange24h, decimal volatilityRatio, string reason)
        {
            var cleanSymbol = symbol.Replace("USDT", "");
            var sb = new StringBuilder();
            sb.AppendLine("⚠️ <b>YÜKSƏK VOLATİLLİK / RİSK BİLDİRİŞİ</b> ⚡");
            sb.AppendLine();
            sb.AppendLine($"🪙 <b>Coin:</b> <code>{cleanSymbol} Futures</code>");
            sb.AppendLine($"💵 <b>Cari Qiymət:</b> <code>${currentPrice.ToString(CultureInfo.InvariantCulture)}</code>");
            var sign = priceChange24h >= 0 ? "+" : "";
            sb.AppendLine($"📊 <b>24s Dəyişim:</b> <b>{sign}{priceChange24h.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"🔥 <b>Dalğalanma Nisbəti:</b> <b>{volatilityRatio.ToString("F1", CultureInfo.InvariantCulture)}x Normal Həcm</b>");
            sb.AppendLine($"📌 <b>Səbəb:</b> <i>{reason}</i>");
            sb.AppendLine();
            sb.AppendLine("🛡️ <b>RİSK MENECMENT QEYDİ:</b>");
            sb.AppendLine("• Coində kəskin qeyri-sabit dalğalanma aşkarlandığı üçün sistem riskli girişləri məhdudlaşdırır.");
            sb.AppendLine("• <b>Depozitin qorunması məqsədilə bu coində tələsik əməliyyat açılmır.</b>");
            return sb.ToString();
        }

        public static string FormatUrgentNewsAlert(CryptoNewsItem newsItem, bool isListing = false)
        {
            var sb = new StringBuilder();
            if (isListing || newsItem.Title.Contains("List", StringComparison.OrdinalIgnoreCase) || newsItem.Title.Contains("Token", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("🪙 <b>YENİ COİN LİSTİNQİ / BURAXILIŞ BİLDİRİŞİ!</b> 🚀");
            }
            else
            {
                sb.AppendLine("🚨 <b>TƏCİLİ BAZAR XƏBƏRİ / VACİB HADİSƏ!</b> 📢");
            }
            sb.AppendLine();
            var safeTitle = System.Net.WebUtility.HtmlEncode(newsItem.Title);
            sb.AppendLine($"📰 <b>Məlumat:</b> <b>{safeTitle}</b>");
            sb.AppendLine($"🌐 <b>Mənbə:</b> <code>{newsItem.Source}</code>");
            sb.AppendLine($"🎯 <b>Bazar Əhvalı / Təsiri:</b> <b>{newsItem.Sentiment}</b>");
            sb.AppendLine($"🕒 <b>Tarix:</b> <code>{newsItem.PublishedAt:dd.MM.yyyy | HH:mm:ss}</code>");
            if (!string.IsNullOrWhiteSpace(newsItem.Url))
            {
                sb.AppendLine();
                sb.AppendLine($"🔗 <a href=\"{newsItem.Url}\">Ətraflı oxumaq üçün mənbəyə keçin</a>");
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
                var emaRel = compass.Ema20 > compass.Ema50 ? "EMA20 &gt; EMA50 (Yüksəliş) 🟢" : "EMA20 &lt; EMA50 (Eniş) 🔴";
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

        public static string FormatPerformanceStats(PerformanceStats stats, string? activeTimeframe = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("📊 <b>Canlı Statistik Performans:</b>");
            if (!string.IsNullOrEmpty(activeTimeframe) && activeTimeframe != "Hamısı" && activeTimeframe != "Hamisi")
            {
                sb.AppendLine($"⏱ <b>Seçilmiş Rejim:</b> <code>{activeTimeframe}</code>");
            }
            else
            {
                sb.AppendLine("🌐 <b>Əhatə:</b> <code>Bütün Əsas Zamanlar (15m, 1h, 4h)</code>");
            }
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"📌 <b>Ümumi Analizlər:</b> {stats.TotalSignals} ədəd");
            sb.AppendLine($"🟡 <b>Açıq İzlənən:</b> {stats.OpenSignals} ədəd");
            sb.AppendLine($"✅ <b>Uğurlu (Qazanc / Hədəfə Çatan):</b> {stats.SuccessSignals} ədəd");
            sb.AppendLine($"❌ <b>Uğursuz:</b> {stats.FailedSignals} ədəd");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"🎯 <b>Real Qələbə Faizi (Win Rate):</b> <b>{stats.WinRatePercent.ToString("F1", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"📈 <b>Xalis Nəticə (PnL):</b> <b>{(stats.TotalNetProfitPercent >= 0 ? "+" : "")}{stats.TotalNetProfitPercent.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"📊 <b>Orta Əməliyyat Gəliri:</b> {(stats.AvgProfitPerTradePercent >= 0 ? "+" : "")}{stats.AvgProfitPerTradePercent.ToString("F2", CultureInfo.InvariantCulture)}%");
            sb.AppendLine($"💎 <b>Profit Factor:</b> <b>{stats.ProfitFactor.ToString("F2", CultureInfo.InvariantCulture)}</b> | <b>Expectancy:</b> <b>{stats.ExpectancyR.ToString("F2", CultureInfo.InvariantCulture)}R</b>");
            sb.AppendLine($"📉 <b>Maksimum Drawdown:</b> -{stats.MaxDrawdownPercent.ToString("F2", CultureInfo.InvariantCulture)}%");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"🎯 <b>Hədəf Bölgüsü:</b> TP3: {stats.Tp3HitsCount} ({stats.Tp3HitRatePercent}%) | Partial/Breakeven: {stats.PartialHitsCount + stats.BreakevenHitsCount} | Vaxt Bitdi: {stats.TimeExpiredCount}");
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
                var tgName = !string.IsNullOrWhiteSpace(u.TelegramUsername) 
                    ? $"@{u.TelegramUsername}" 
                    : (!string.IsNullOrWhiteSpace(u.TelegramChatId) ? $"ID: {u.TelegramChatId}" : "⏳ Hələ daxil olmayıb");
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
                sb.AppendLine($"<i>Hal-hazırda sistem seçilmiş {monitoredCoins.Count} coin üzrə canlı skan edir. Şamlar bağlandıqca və TP/SL hədəfləri vurduqca qələbə faizləri burada canlı toplanacaq.</i>");
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
                        "15m" => 1,
                        "1h" => 2,
                        "4h" => 3,
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

        public static string FormatBotStatus(UserSettings settings, int userOpenPositionsCount, string? lastSignalTime)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ℹ️ <b>CryptoSense Sistem Statusu:</b>");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine("🤖 <b>Skaner Vəziyyəti:</b> İşləyir 🟢 (24/7 Canlı Rejim)");
            var commitHash = "1d817af";
            try
            {
                var envSha = Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA")
                             ?? Environment.GetEnvironmentVariable("GIT_COMMIT_SHA");
                if (!string.IsNullOrEmpty(envSha) && envSha.Length >= 7)
                {
                    commitHash = envSha.Substring(0, 7);
                }
                else
                {
                    var headFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".git", "refs", "heads", "main");
                    if (System.IO.File.Exists(headFile))
                    {
                        var hash = System.IO.File.ReadAllText(headFile).Trim();
                        if (hash.Length >= 7) commitHash = hash.Substring(0, 7);
                    }
                }
            }
            catch { }
            sb.AppendLine($"📌 <b>Deploy Versiyası:</b> <code>v2.0 (Commit: {commitHash})</code>");
            var tfDisplay = (settings.Timeframe == "Hamısı" || settings.Timeframe == "Hamisi") ? "15m, 1h, 4h" : settings.Timeframe;
            sb.AppendLine($"⏱ <b>Aktiv Rejim:</b> <code>{tfDisplay}</code>");
            string coinText;
            if (settings.Coins.Count == 0)
            {
                coinText = "0/16 (Heç bir coin seçilməyib)";
            }
            else
            {
                var cleanTickers = string.Join(", ", settings.Coins.Select(c => c.Replace("USDT", "")));
                coinText = settings.Coins.Count <= 16 
                    ? $"{settings.Coins.Count}/16 ({cleanTickers})"
                    : $"{settings.Coins.Count} coin ({cleanTickers})";
            }
            sb.AppendLine($"🪙 <b>Seçilmiş Coinlər:</b> {coinText}");
            sb.AppendLine($"🟡 <b>Açıq Mövqeləriniz:</b> {userOpenPositionsCount} ədəd (Maksimum limit: 5)");
            sb.AppendLine($"🔔 <b>Bildiriş Statusu:</b> {(settings.IsActive ? "Aktiv 🟢" : "Dayandırılıb 🔴")}");
            sb.AppendLine($"🕒 <b>Son Siqnal Vaxtı:</b> {(string.IsNullOrEmpty(lastSignalTime) ? "Hələ yoxdur" : lastSignalTime)}");
            sb.AppendLine("-----------------------------------");
            return sb.ToString();
        }

        public static string FormatNoSignalReason(string reason, int nextCheckMinutes = 30)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ℹ️ <b>A+ siqnal yoxdur</b>");
            sb.AppendLine($"📌 <b>Səbəb:</b> {reason}");
            sb.AppendLine($"⏱ <b>Növbəti yoxlama:</b> {nextCheckMinutes} dəq");
            return sb.ToString();
        }

        public static string FormatDailyReport(PerformanceStats stats, List<string> coinsEntered, List<string> coinsFilteredReasons, bool isSuperAdmin)
        {
            var sb = new StringBuilder();
            sb.AppendLine(isSuperAdmin ? "📊 <b>GÜN SONU HESABATI (Qlobal Sistem)</b>" : "📊 <b>GÜN SONU ŞƏXSİ HESABATINIZ</b>");
            sb.AppendLine($"🕒 <b>Tarix:</b> <code>{DateTime.UtcNow:dd.MM.yyyy}</code>");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"📌 <b>Ümumi Siqnallar:</b> {stats.TotalSignals} ədəd");
            sb.AppendLine($"✅ <b>Uğurlu (TP):</b> {stats.SuccessSignals} ədəd");
            sb.AppendLine($"❌ <b>Uğursuz (SL):</b> {stats.FailedSignals} ədəd");
            sb.AppendLine($"🎯 <b>Günlük Win Rate:</b> <b>{stats.WinRatePercent.ToString("F1", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"📈 <b>Günlük Xalis PnL:</b> <b>{(stats.TotalNetProfitPercent >= 0 ? "+" : "")}{stats.TotalNetProfitPercent.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine("-----------------------------------");
            if (coinsEntered.Count > 0)
            {
                sb.AppendLine($"🪙 <b>Girilən Coinlər:</b> <code>{string.Join(", ", coinsEntered)}</code>");
            }
            else
            {
                sb.AppendLine("🪙 <b>Girilən Coinlər:</b> Bu gün yeni əməliyyat açılmayıb.");
            }
            if (coinsFilteredReasons.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("🛡️ <b>Filtrlənən Coinlərin Səbəbləri:</b>");
                foreach (var r in coinsFilteredReasons.Take(4))
                {
                    sb.AppendLine($"• {r}");
                }
            }
            sb.AppendLine("-----------------------------------");
            return sb.ToString();
        }
    }
}
