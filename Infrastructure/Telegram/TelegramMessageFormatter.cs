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
            sb.AppendLine($"💵 <b>Giriş (Entry):</b> ${signal.EntryPrice.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"📡 <b>Mənbə:</b> <code>{signal.PriceSource}</code>");
            sb.AppendLine($"⏱️ <b>DataAge:</b> {signal.DataAgeMs}ms");
            if (signal.CandleCloseTimeUtc != default)
            {
                sb.AppendLine($"🕯️ <b>Şam bağlandı:</b> {CryptoSense.Domain.Common.TimeHelper.FormatAz(signal.CandleCloseTimeUtc)}");
            }
            sb.AppendLine($"📰 <b>Xəbər Sentimenti:</b> {sentimentText}");
            sb.AppendLine("-----------------------------------");
            decimal tp1Dist = Math.Abs(signal.TakeProfit1 - signal.EntryPrice);
            decimal slDist = Math.Abs(signal.StopLoss - signal.EntryPrice);
            decimal tp1Pct = signal.EntryPrice > 0 ? (tp1Dist / signal.EntryPrice) * 100m : 0m;
            decimal slPct = signal.EntryPrice > 0 ? (slDist / signal.EntryPrice) * 100m : 0m;

            decimal tpBDist = signal.TakeProfit2 > 0 ? Math.Abs(signal.TakeProfit2 - signal.EntryPrice) : 0m;
            decimal tpBPct = (signal.EntryPrice > 0 && tpBDist > 0) ? (tpBDist / signal.EntryPrice) * 100m : 0m;
            decimal effectiveRr = slDist > 0 ? ((0.50m * tp1Dist + 0.50m * (tpBDist > 0 ? tpBDist : tp1Dist)) / slDist) : 0m;

            sb.AppendLine($"📍 <b>Giriş Zonası:</b> ${signal.EntryLow.ToString(CultureInfo.InvariantCulture)} - ${signal.EntryHigh.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"🎯 <b>Hədəf A (TP_A 1.0R - 50%):</b> ${signal.TakeProfit1.ToString(CultureInfo.InvariantCulture)} (+{tp1Pct.ToString("F2", CultureInfo.InvariantCulture)}%) <i>[50% Bağla + BE Stop]</i>");
            if (signal.TakeProfit2 > 0 && signal.TakeProfit2 != signal.TakeProfit1)
            {
                sb.AppendLine($"🎯 <b>Hədəf B (TP_B - 50%):</b> ${signal.TakeProfit2.ToString(CultureInfo.InvariantCulture)} (+{tpBPct.ToString("F2", CultureInfo.InvariantCulture)}%) <i>[Qalan 50% Struktur]</i>");
            }
            sb.AppendLine($"⛔ <b>Stop Loss (SL):</b> ${signal.StopLoss.ToString(CultureInfo.InvariantCulture)} (-{slPct.ToString("F2", CultureInfo.InvariantCulture)}%)");
            sb.AppendLine($"⚖️ <b>Risk:Mükafat (R:R):</b> <b>{effectiveRr.ToString("F2", CultureInfo.InvariantCulture)}</b>");
            sb.AppendLine("-----------------------------------");
            return sb.ToString();
        }

        public static string FormatOutcomeAlert(FuturesSignal signal, int userSigNum, string outcomeType, decimal hitPrice, decimal profitPct)
        {
            decimal netPnl = signal.NetResultPercent != 0 ? signal.NetResultPercent : profitPct;
            decimal grossPnl = signal.GrossResultPercent != 0 ? signal.GrossResultPercent : (netPnl + 0.10m);

            bool isNeutral = signal.Status == SignalStatus.Neutral || (outcomeType.Contains("Breakeven") && netPnl < 0.20m);
            bool isExplicitFail = outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL") || signal.Status == SignalStatus.Failed || (!isNeutral && profitPct < 0);
            bool isExplicitWin = !isNeutral && (signal.Status == SignalStatus.Success || outcomeType.Contains("Hədəf") || outcomeType.Contains("TP") || outcomeType.Contains("Uğurlu") || outcomeType.Contains("Ugurlu") || (outcomeType.Contains("Breakeven") && netPnl >= 0.20m) || signal.Tp1Notified || profitPct >= 0);

            bool isWin = isExplicitWin && !isExplicitFail && !isNeutral;
            if (outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL"))
            {
                isWin = false;
            }

            if (!isWin && !isNeutral && profitPct > 0)
            {
                profitPct = -Math.Abs(profitPct);
            }

            var cleanSymbol = signal.Symbol.Replace("USDT", "");
            var directionStr = (signal.Direction == SignalDirection.Buy || signal.SignalType.Contains("LONG")) ? "LONG" : "SHORT";

            var sb = new StringBuilder();
            if (isNeutral)
            {
                sb.AppendLine($"🎯 <b>#{userSigNum} NƏTİCƏ HESABATI (NEYTRAL ⚪)</b>");
                sb.AppendLine($"📌 <b>#{userSigNum} nömrəli əməliyyat üzrə: Breakeven (Xalis {(netPnl >= 0 ? "+" : "")}{netPnl.ToString("F2", CultureInfo.InvariantCulture)}% - NEYTRAL) ⚪</b>");
            }
            else if (outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL") || !isWin)
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
            sb.AppendLine($"💵 <b>Bağlanış / Last:</b> ${hitPrice.ToString(CultureInfo.InvariantCulture)} (DataAge: {signal.DataAgeMs}ms)");
            sb.AppendLine($"📊 <b>Gross PnL:</b> {(grossPnl >= 0 ? "+" : "")}{grossPnl.ToString("F2", CultureInfo.InvariantCulture)}%");
            sb.AppendLine($"💰 <b>Net PnL (fee -0.10%):</b> {(netPnl >= 0 ? "+" : "")}{netPnl.ToString("F2", CultureInfo.InvariantCulture)}%");
            sb.AppendLine($"📈 <b>MFE:</b> +{signal.MfePercent.ToString("F2", CultureInfo.InvariantCulture)}% | <b>MAE:</b> -{Math.Abs(signal.MaePercent).ToString("F2", CultureInfo.InvariantCulture)}%");
            sb.AppendLine($"🏷️ <b>Səbəb:</b> {signal.CloseReason ?? outcomeType}");
            sb.AppendLine($"🕒 <b>Siqnal Vaxtı:</b> {signal.TimestampFormatted}");
            sb.AppendLine($"🕒 <b>Yenilənmə Vaxtı:</b> {CryptoSense.Domain.Common.TimeHelper.NowFormatted}");

            if (outcomeType.Contains("TP1") || outcomeType.Contains("TP_A") || outcomeType.Contains("Hədəf 1") || outcomeType.Contains("Hədəf A"))
            {
                sb.AppendLine();
                sb.AppendLine("🛡️ <b>PARTİAL CLOSE (50% BAĞLANDI) & BE QORUMA:</b>");
                sb.AppendLine("• Mövqenin <b>50%-i Hədəf A (TP_A 1.00R) səviyyəsində mənfəətlə bağlandı ✅</b>");
                sb.AppendLine($"• Stop Loss dərhal <b>GİRİŞƏ (Breakeven: ${signal.EntryPrice.ToString(CultureInfo.InvariantCulture)})</b> çəkildi (Sıfır Risk)!");
                if (signal.TakeProfit2 > 0 && signal.TakeProfit2 != signal.TakeProfit1)
                {
                    sb.AppendLine($"• Qalan <b>50%</b> mövqe ilə <b>Hədəf B (TP_B: ${signal.TakeProfit2.ToString(CultureInfo.InvariantCulture)})</b> strukturu gözlənilir.");
                }
            }
            else if (outcomeType.Contains("TP2") || outcomeType.Contains("TP_B") || outcomeType.Contains("Hədəf 2") || outcomeType.Contains("Hədəf B") || outcomeType.Contains("TP3") || outcomeType.Contains("Hədəf 3"))
            {
                sb.AppendLine();
                sb.AppendLine("🏆 <b>TAM HƏDƏFƏ ÇATILDI:</b> Qalan 50% mövqe Hədəf B (TP_B) ilə tam bağlandı.");
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
            if (isListing || newsItem.Title.Contains("List", StringComparison.OrdinalIgnoreCase) || newsItem.Title.Contains("Token", StringComparison.OrdinalIgnoreCase) || newsItem.OriginalTitle.Contains("List", StringComparison.OrdinalIgnoreCase) || newsItem.OriginalTitle.Contains("Token", StringComparison.OrdinalIgnoreCase))
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
            sb.AppendLine($"🕒 <b>Paylaşılma Vaxtı (Bakı):</b> <code>{CryptoSense.Domain.Common.TimeHelper.FormatAz(newsItem.PublishedAt)}</code>");
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
                var threshold = compass.BtcDominanceThreshold > 0 ? compass.BtcDominanceThreshold : 56.5m;
                sb.AppendLine($"• <b>Bitcoin Dominantlığı (BTC.D):</b> <b>{compass.BtcDominance.ToString("F2", CultureInfo.InvariantCulture)}%</b> (Dinamik Hədd: <b>{threshold.ToString("F2", CultureInfo.InvariantCulture)}%</b>)");
                sb.AppendLine($"• <b>Tether Dominantlığı (USDT.D):</b> <b>{compass.UsdtDominance.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
                var altImpact = compass.BtcDominance >= threshold 
                    ? $"⚠️ <i>BTC.D dinamik həddən ({threshold.ToString("F2", CultureInfo.InvariantCulture)}%) yüksəkdir — Altcoinlərdə ehtiyatlı olun.</i>"
                    : $"✅ <i>BTC.D dinamik həddən ({threshold.ToString("F2", CultureInfo.InvariantCulture)}%) stabildir — Altcoinlərdə ticarət üçün əlverişlidir.</i>";
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

        public static string FormatPerformanceStats(PerformanceStats stats, string? activeTimeframe = null, bool isAllTime = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("📊 <b>Canlı Statistik Performans:</b>");
            var bakuNow = DateTime.UtcNow.AddHours(4);
            var bakuDateStr = bakuNow.ToString("dd.MM.yyyy");
            sb.AppendLine($"🕒 <b>Tarix:</b> <code>{bakuDateStr} (Bakı)</code>");
            if (isAllTime)
            {
                sb.AppendLine("📅 <b>Dövr:</b> <code>Bütün Tarix (All-Time)</code>");
            }
            else
            {
                sb.AppendLine("📅 <b>Dövr:</b> <code>Bugünkü (00:00-dan etibarən)</code>");
            }

            if (!string.IsNullOrEmpty(activeTimeframe) && activeTimeframe != "Hamısı" && activeTimeframe != "Hamisi")
            {
                sb.AppendLine($"⏱ <b>Seçilmiş Rejim:</b> <code>{activeTimeframe}</code>");
            }
            else
            {
                sb.AppendLine("🌐 <b>Əhatə:</b> <code>Bütün Əsas Zamanlar (1h, 4h)</code>");
            }

            if (stats.TotalSignals == 0)
            {
                sb.AppendLine("-----------------------------------");
                sb.AppendLine("📌 <b>Ümumi Analizlər:</b> 0 ədəd");
                sb.AppendLine("🟡 <b>Açıq İzlənən:</b> 0 ədəd");
                sb.AppendLine("✅ <b>Uğurlu (Qazanc / Hədəfə Çatan):</b> 0 ədəd");
                sb.AppendLine("❌ <b>Uğursuz:</b> 0 ədəd");
                sb.AppendLine("-----------------------------------");
                sb.AppendLine("🎯 <b>Real Qələbə Faizi (Win Rate):</b> <b>0.0%</b>");
                sb.AppendLine("📈 <b>Xalis Nəticə (PnL):</b> <b>+0.00%</b>");
                sb.AppendLine("📊 <b>Orta Əməliyyat Gəliri:</b> +0.00%");
                sb.AppendLine("💎 <b>Profit Factor:</b> <b>0.00</b> | <b>Expectancy:</b> <b>0.00R</b>");
                sb.AppendLine("📉 <b>Maksimum Drawdown:</b> -0.00%");
                sb.AppendLine("-----------------------------------");
                sb.AppendLine("ℹ️ <i>Hələ heç bir əməliyyat və ya zaman seçilməyib. Bütün statistik göstəricilər sıfırdır. Başlamaq üçün Əsas Terminaldan portfel və zaman aralığı seçin.</i>");
                return sb.ToString();
            }

            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"📌 <b>Ümumi Əməliyyatlar:</b> {stats.TotalSignals} ədəd");
            sb.AppendLine($"🟡 <b>Açıq İzlənən:</b> {stats.OpenSignals} ədəd");
            sb.AppendLine($"🎯 <b>TP (Tam / Qismən Qazanc):</b> {stats.SuccessSignals} ədəd");
            sb.AppendLine($"⛔ <b>SL (Uğursuz):</b> {stats.FailedSignals} ədəd");
            sb.AppendLine($"⚪ <b>NEYTRAL / Breakeven:</b> {stats.BreakevenHitsCount} ədəd");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"🎯 <b>Real Qələbə Faizi (Win Rate):</b> <b>{stats.WinRatePercent.ToString("F1", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"💰 <b>Xalis Nəticə (Net PnL):</b> <b>{(stats.TotalNetProfitPercent >= 0 ? "+" : "")}{stats.TotalNetProfitPercent.ToString("F2", CultureInfo.InvariantCulture)}%</b>");
            sb.AppendLine($"📊 <b>Orta Əməliyyat Gəliri:</b> {(stats.AvgProfitPerTradePercent >= 0 ? "+" : "")}{stats.AvgProfitPerTradePercent.ToString("F2", CultureInfo.InvariantCulture)}%");
            sb.AppendLine($"💎 <b>Profit Factor:</b> <b>{stats.ProfitFactor.ToString("F2", CultureInfo.InvariantCulture)}</b> | <b>Expectancy:</b> <b>{stats.ExpectancyR.ToString("F2", CultureInfo.InvariantCulture)}R</b>");
            sb.AppendLine($"📉 <b>Maksimum Drawdown:</b> -{stats.MaxDrawdownPercent.ToString("F2", CultureInfo.InvariantCulture)}%");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"🎯 <b>Hədəf Bölgüsü:</b> TP3: {stats.Tp3HitsCount} ({stats.Tp3HitRatePercent}%) | TP1/TP2: {stats.PartialHitsCount} | BE (Neytral): {stats.BreakevenHitsCount} | Vaxt Bitdi: {stats.TimeExpiredCount}");
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
                var statusText = u.IsActive ? "Aktiv 🟢" : "Deaktiv 🔴";
                var sessionText = u.IsLoggedIn ? "Daxil olub 🟢" : "Çıxış edib ⚪";
                sb.AppendLine($"{index}. <b>{u.Username}</b> | Rol: <code>{u.Role}</code> | {statusText} | {sessionText}");
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

        public static string GetShortGitCommitHash()
        {
            try
            {
                var envSha = Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA")
                             ?? Environment.GetEnvironmentVariable("GIT_COMMIT_SHA");
                if (!string.IsNullOrEmpty(envSha) && envSha.Length >= 7)
                {
                    return envSha.Substring(0, 7);
                }

                var searchDirs = new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() };
                foreach (var startDir in searchDirs)
                {
                    if (Directory.Exists(startDir))
                    {
                        var dir = new DirectoryInfo(startDir);
                        while (dir != null)
                        {
                            var gitDir = Path.Combine(dir.FullName, ".git");
                            if (Directory.Exists(gitDir))
                            {
                                var headPath = Path.Combine(gitDir, "HEAD");
                                if (File.Exists(headPath))
                                {
                                    var headContent = File.ReadAllText(headPath).Trim();
                                    if (headContent.StartsWith("ref: "))
                                    {
                                        var refSubPath = headContent.Substring(5).Trim().Replace('/', Path.DirectorySeparatorChar);
                                        var refPath = Path.Combine(gitDir, refSubPath);
                                        if (File.Exists(refPath))
                                        {
                                            var h = File.ReadAllText(refPath).Trim();
                                            if (h.Length >= 7) return h.Substring(0, 7);
                                        }
                                        else
                                        {
                                            var packedRefs = Path.Combine(gitDir, "packed-refs");
                                            if (File.Exists(packedRefs))
                                            {
                                                foreach (var line in File.ReadAllLines(packedRefs))
                                                {
                                                    if (line.EndsWith(refSubPath.Replace(Path.DirectorySeparatorChar, '/')))
                                                    {
                                                        var parts = line.Split(' ');
                                                        if (parts[0].Length >= 7) return parts[0].Substring(0, 7);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else if (headContent.Length >= 7)
                                    {
                                        return headContent.Substring(0, 7);
                                    }
                                }
                            }
                            dir = dir.Parent;
                        }
                    }
                }
            }
            catch { }

            try
            {
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse --short HEAD",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (proc != null)
                {
                    var outStr = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(1000);
                    if (outStr.Length >= 7) return outStr.Substring(0, 7);
                }
            }
            catch { }

            return "2737c31";
        }

        public static string FormatBotStatus(UserSettings settings, int userOpenPositionsCount, string? lastSignalTime, bool? canReceivePush = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ℹ️ <b>CryptoSense Sistem Statusu:</b>");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine("🤖 <b>Skaner Vəziyyəti:</b> İşləyir 🟢 (24/7 Canlı Rejim)");
            var commitHash = GetShortGitCommitHash();
            sb.AppendLine($"📌 <b>Deploy Versiyası:</b> <code>v2.0 (Commit: {commitHash})</code>");
            var tfDisplay = (settings.Timeframe == "Hamısı" || settings.Timeframe == "Hamisi") ? "1h, 4h" : (settings.Timeframe == "15m" ? "1h" : settings.Timeframe);
            sb.AppendLine($"⏱ <b>Aktiv Rejim:</b> <code>{tfDisplay}</code>");
            string coinText;
            if (settings.Coins.Count == 0)
            {
                coinText = "0 coin (Heç bir coin seçilməyib)";
            }
            else
            {
                var cleanTickers = string.Join(", ", settings.Coins.Select(c => c.Replace("USDT", "")));
                coinText = $"{settings.Coins.Count} coin ({cleanTickers})";
            }
            sb.AppendLine($"🪙 <b>Seçilmiş Coinlər:</b> {coinText}");
            sb.AppendLine($"🟡 <b>Açıq Mövqeləriniz:</b> {userOpenPositionsCount} ədəd (Maksimum limit: 20)");
            bool isPushActive = canReceivePush ?? settings.IsActive;
            sb.AppendLine($"🔔 <b>Bildiriş Statusu:</b> {(isPushActive ? "Aktiv 🟢" : "Dayandırılıb 🔴")}");
            sb.AppendLine($"🕒 <b>Son Siqnal Vaxtı:</b> {(string.IsNullOrEmpty(lastSignalTime) ? "Hələ yoxdur" : lastSignalTime)}");
            sb.AppendLine("-----------------------------------");
            return sb.ToString();
        }

        public static string FormatTerminalDashboard(UserSettings settings, int activePositionsCount, string lastSignalTime)
        {
            var statusIcon = settings.IsActive ? "İşləyir 🟢" : "Dayandırılıb 🔴";
            var tfDisplay = string.IsNullOrWhiteSpace(settings.Timeframe) || settings.Timeframe == "Təyin olunmayıb"
                ? "Təyin olunmayıb ⚠️"
                : ((settings.Timeframe == "Hamısı" || settings.Timeframe == "Hamisi") ? "1h, 4h" : settings.Timeframe);

            string portModeName;
            string coinCount;
            if (settings.PortfolioMode == "Combined")
            {
                portModeName = "🔥 40 Coin + Fərdi Coinlər (Kombinə)";
                coinCount = $"{settings.Coins.Count} ədəd (40 Standart + {settings.CustomCoins.Count} Fərdi)";
            }
            else if (settings.PortfolioMode == "Custom")
            {
                portModeName = "⭐ Mənim Coinlərim (Fərdi)";
                coinCount = settings.CustomCoins.Count > 0 ? $"{settings.CustomCoins.Count} ədəd (Fərdi)" : "0 ədəd (Boşdur)";
            }
            else if (settings.PortfolioMode == "Standard40")
            {
                portModeName = "🪙 Standart 40 Coin";
                coinCount = $"{settings.Coins.Count} ədəd (İnstitusional 40)";
            }
            else
            {
                portModeName = "Təyin olunmayıb ⚠️";
                coinCount = "0 ədəd (Seçilməyib)";
            }

            var sb = new StringBuilder();
            sb.AppendLine("🤖 <b>CryptoSense v2.0 | Canlı Terminal</b>");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"💼 <b>Aktiv Portfel:</b> <b>{portModeName}</b>");
            sb.AppendLine($"⏱ <b>Zaman Rejimi:</b> <code>{tfDisplay}</code>");
            sb.AppendLine($"🪙 <b>İzlənən Coinlər:</b> <code>{coinCount}</code>");
            sb.AppendLine($"⚡ <b>Açıq Mövqelər:</b> <code>{activePositionsCount} ədəd (Maksimum: 20)</code>");
            sb.AppendLine($"🔔 <b>Skaner Vəziyyəti:</b> <b>{statusIcon}</b>");
            sb.AppendLine($"🕒 <b>Son Siqnal:</b> <code>{(string.IsNullOrEmpty(lastSignalTime) ? "Hələ yoxdur" : lastSignalTime)}</code>");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            if (string.IsNullOrWhiteSpace(settings.Timeframe) || settings.Timeframe == "Təyin olunmayıb" || settings.Coins.Count == 0 || !settings.IsActive)
            {
                sb.AppendLine("⚠️ <i>Hələ heç bir əməliyyat və zaman aralığı seçilməyib. Bazar sistemini başlatmaq üçün aşağıdakı düymələrlə portfel və zaman aralığını seçin.</i>");
            }
            else
            {
                sb.AppendLine("<i>Aşağıdakı düymələrlə portfeli və parametrləri tənzimləyin:</i>");
            }
            return sb.ToString();
        }

        public static string FormatStopConfirmPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("⚠️ <b>Bildirişləri Dayandırmaq</b>");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("Siqnal və bazar bildirişlərini dayandırmaq istədiyinizə əminsiniz?");
            sb.AppendLine("• Bot sizin üçün yeni siqnal bildirişləri göndərməyəcək.");
            sb.AppendLine("• Mövcud açıq mövqeləriniz izlənmədə qalacaq.");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("Davam etmək istədiyinizə əminsiniz?");
            return sb.ToString();
        }

        public static string FormatResetConfirmationPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("⚠️ <b>DİQQƏT: Bütün Bazar Sistemlərini Sıfırlamaq</b>");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("Bu əməliyyatı təsdiq etdikdə:");
            sb.AppendLine("• Bütün aktiv bazar skaneri və bildirişlər <b>dayandırılacaq</b> (Dayandırılıb 🔴).");
            sb.AppendLine("• Bütün təyin edilmiş zaman aralıqları (1h / 4h) <b>sıfırlanacaq</b> və yenidən təyin olunma tələb edəcək.");
            sb.AppendLine("• Şəxsi siqnal sayğacınız sıfırlanacaq.");
            sb.AppendLine();
            sb.AppendLine("ℹ️ <b>QEYD:</b> <i>Sizin seçilmiş standart 40 coin və ya əlavə etdiyiniz fərdi coinləriniz SİLİNMİR, toxunulmaz saxlanılır!</i>");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("Davam etmək istədiyinizə əminsiniz?");
            return sb.ToString();
        }

        public static string FormatAdminDashboard(int totalUsers, int activeUsers)
        {
            var sb = new StringBuilder();
            sb.AppendLine("👑 <b>CryptoSense v2.0 | Super Admin Paneli</b>");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"👥 <b>Qeydiyyatlı İstifadəçilər:</b> <code>{totalUsers} nəfər</code>");
            sb.AppendLine($"🟢 <b>Aktiv İcazəli Sessiyalar:</b> <code>{activeUsers} nəfər</code>");
            sb.AppendLine($"🕒 <b>Sistem Vaxtı (AZT):</b> <code>{CryptoSense.Domain.Common.TimeHelper.NowFormatted}</code>");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("<i>Aşağıdakı düymələrlə inzibati əməliyyatları icra edin:</i>");
            return sb.ToString();
        }

        public static string FormatLiveHeartbeat(
            int chase, 
            int corr, 
            int slWide, 
            int lowRr, 
            int activeLocks, 
            int sent, 
            int nextCheckMinutes = 60, 
            long dataAgeMsBtc = -1, 
            int skipStale = 0, 
            int skipLag = 0, 
            int skipConfluence = 0, 
            int telegramFail = 0,
            int skipGozleme = 0,
            int skipBtcGate = 0,
            int skipBtcRange = 0,
            int skipCircuitBreaker = 0,
            int skipMaxOpen = 0,
            int skipDailyLoss = 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("&#8505;&#65039; <b>Bazar N&#601;zar&#601;ti (Heartbeat)</b>");
            sb.AppendLine($"A+ yoxdur | Chase:{chase} Corr:{corr} SL:{slWide} RR:{lowRr} Lag:{skipLag} Stale:{skipStale} Conf:{skipConfluence} G&#246;nd&#601;rildi:{sent} | G&#246;zl&#601;m&#601;:{skipGozleme} BtcGate:{skipBtcGate} Range:{skipBtcRange}");

            if (sent == 0)
            {
                if (skipCircuitBreaker > 0)
                {
                    sb.AppendLine("📌 <b>Səbəb:</b> CircuitBreaker aktivdir (Risk qorunması)");
                }
                else if (skipDailyLoss > 0)
                {
                    sb.AppendLine("📌 <b>Səbəb:</b> Günlük itki limiti keçib (≤ -3.0%)");
                }
                else if (skipMaxOpen > 0)
                {
                    sb.AppendLine($"📌 <b>Səbəb:</b> Maksimum açıq mövqe limitinə çatılıb ({activeLocks}/20)");
                }
                else
                {
                    var reasonsList = new List<(string Name, int Count, string Description)>
                    {
                        ("BtcGate", skipBtcGate, "BTC Ayı (Bearish) rejimindədir — Alt LONG-lar bloklandı"),
                        ("Range", skipBtcRange, "BTC 1h Kompası Ranging (qeyri-müəyyən) rejimindədir"),
                        ("Gözləmə", skipGozleme, "Bazar zəif konsolidasiyadadır (Gözləmə rejimi / ADX zəif)"),
                        ("Confluence", skipConfluence, "Confluence balı tələb olunan 75%-dən aşağıdır"),
                        ("SL", slWide, "Stop-Loss məsafəsi çox genişdir (> 2.8%)"),
                        ("RR", lowRr, "Risk/Reward nisbəti qeyri-qənaətbəxşdir (< 1.30)"),
                        ("Chase", chase, "Qiymət giriş zonasından uzaqlaşıb (Chase filtri)"),
                        ("Lock", activeLocks, "Aktiv mövqe limitinə çatılıb"),
                        ("Lag", skipLag, "Şam bağlanış gecikməsi (Lag filtri)"),
                        ("Stale", skipStale, "Qiymət məlumatı köhnədir (Stale WebSocket)")
                    };
                    var dominant = reasonsList.OrderByDescending(r => r.Count).FirstOrDefault(r => r.Count > 0);
                    string reasonText = dominant.Count > 0 
                        ? $"{dominant.Description} ({dominant.Name}: {dominant.Count})"
                        : "Bazar konyukturası A+ siqnal meyarlarına uyğun gəlmir";

                    sb.AppendLine($"📌 <b>Səbəb:</b> {reasonText}");
                }
            }

            if (telegramFail > 0)
                sb.AppendLine($"&#9888;&#65039; <b>TELEGRAM_FAIL={telegramFail}</b> &#8212; signal haz&#305;rland&#305;, lakin g&#246;nd&#601;rilm&#601;di!");
            if (dataAgeMsBtc < 0)
                sb.AppendLine("&#128993; BTC DataAge: <code>&#246;l&#231;&#252;lm&#601;yib (snap yoxdur)</code>");
            else if (dataAgeMsBtc > 3500)
                sb.AppendLine($"&#128308; <b>WS GECİKİR DataAge:{dataAgeMsBtc}ms &gt; 3500ms</b>");
            else
                sb.AppendLine($"&#128994; BTC DataAge: <code>{dataAgeMsBtc}ms</code> (WS sa&#287;lam)");
            sb.AppendLine($"&#8987; <b>N&#246;vb&#601;ti yoxlama:</b> {nextCheckMinutes} d&#601;q");
            return sb.ToString();
        }

        public static string FormatBootBriefing(
            string commitHash,
            long dataAgeMsBtc,
            string btcTrend,
            string btcRegime,
            decimal btcAdx,
            bool btcSuperTrendBullish,
            bool btcCandleGreen,
            bool ethCandleGreen,
            bool ethFilterPassing,
            int last1hAgeMinutes,
            int last4hAgeMinutes,
            int scannedCount,
            List<FuturesSignal> emittedSignals,
            string dominantSkipName,
            int dominantSkipCount,
            int nextCheckMinutes,
            List<Kline>? recentBtc1hCandles = null,
            List<(string Symbol, string Timeframe, string Direction, decimal EntryPrice, string Reason)>? skippedPassSignals = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("🚀 <b>CryptoSense v2.0 | Sistem Başlatma Brifinqi</b>");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"📌 <b>Deploy Versiyası:</b> <code>v2.0 ({commitHash})</code>");
            sb.AppendLine($"🕒 <b>Boot Vaxtı (AZT):</b> <code>{CryptoSense.Domain.Common.TimeHelper.NowFormatted}</code>");
            string wsStatus = (dataAgeMsBtc >= 0 && dataAgeMsBtc <= 3500) ? "Sağlam 🟢" : "Gecikir 🔴";
            string wsText = dataAgeMsBtc >= 0 ? $"{dataAgeMsBtc}ms ({wsStatus})" : "Ölçülməyib 🟡";
            sb.AppendLine($"⚡ <b>WebSocket Statusu:</b> <code>{wsText}</code>");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("🪙 <b>BTC 1h Vəziyyəti:</b>");
            string btcColor = btcCandleGreen ? "Yaşıl 🟢" : "Qırmızı 🔴";
            string btcSt = btcSuperTrendBullish ? "Bullish 🟢" : "Bearish 🔴";
            sb.AppendLine($"• Kompas: <b>{btcTrend}</b> ({btcRegime})");
            sb.AppendLine($"• ADX: <code>{btcAdx:F1}</code> | SuperTrend: <b>{btcSt}</b> | Şam: <b>{btcColor}</b>");
            sb.AppendLine();
            sb.AppendLine("🔷 <b>ETH 1h Vəziyyəti:</b>");
            string ethColor = ethCandleGreen ? "Yaşıl 🟢" : "Qırmızı 🔴";
            string ethStatus = ethFilterPassing ? "Keçir 🟢" : "Bloklayır 🔴";
            sb.AppendLine($"• Şam: <b>{ethColor}</b> | 1h Filter: <b>{ethStatus}</b>");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("⏱ <b>Son Bağlanmış Şamlar:</b>");
            sb.AppendLine($"• 1h şamın bağlanışından: <code>{last1hAgeMinutes} dəqiqə</code>");
            sb.AppendLine($"• 4h şamın bağlanışından: <code>{last4hAgeMinutes} dəqiqə</code>");

            if (recentBtc1hCandles != null && recentBtc1hCandles.Count > 0)
            {
                sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                sb.AppendLine("📊 <b>Son 12 Bağlanmış BTC 1h Şamı:</b>");
                sb.AppendLine("<pre>");
                sb.AppendLine("Saat (AZT) |   Açılış   |   Bağlanış  |   Dəyişmə");
                sb.AppendLine("-----------|------------|-------------|-----------");
                foreach (var c in recentBtc1hCandles)
                {
                    var aztTime = DateTimeOffset.FromUnixTimeMilliseconds(c.OpenTime).UtcDateTime.AddHours(4).ToString("dd.MM HH:mm");
                    decimal chgPct = c.Open > 0 ? ((c.Close - c.Open) / c.Open) * 100m : 0m;
                    string sign = chgPct >= 0 ? "+" : "";
                    sb.AppendLine($"{aztTime,-10} | ${c.Open,9:F1} | ${c.Close,10:F1} | {sign}{chgPct:F2}%");
                }
                sb.AppendLine("</pre>");
            }

            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("🎯 <b>Catch-Up Skanı Nəticəsi:</b>");
            sb.AppendLine($"• Skan edilən cütlük sayı: <code>{scannedCount}</code>");
            if (emittedSignals != null && emittedSignals.Count > 0)
            {
                sb.AppendLine($"• Tapılan A+ siqnal: <b>{emittedSignals.Count} ədəd</b> (Göndərildi ✅)");
                foreach (var sig in emittedSignals)
                {
                    sb.AppendLine($"  - <b>{sig.Symbol}</b> ({sig.Timeframe} {sig.Direction}) @ ${sig.EntryPrice}");
                }
            }
            else
            {
                sb.AppendLine("• Tapılan A+ siqnal: <code>0 ədəd</code>");
            }
            string skipInfo = dominantSkipCount > 0 ? $"{dominantSkipName}: {dominantSkipCount}" : "Yoxdur";
            sb.AppendLine($"• Dominant skip səbəbi: <code>{skipInfo}</code>");

            if (skippedPassSignals != null && skippedPassSignals.Count > 0)
            {
                sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                sb.AppendLine("🛡️ <b>Filterlənən A+ Siqnallar (Maks 8):</b>");
                foreach (var sk in skippedPassSignals.Take(8))
                {
                    sb.AppendLine($"• <b>{sk.Symbol}</b> ({sk.Timeframe} {sk.Direction} @ ${sk.EntryPrice}) &#8212; Səbəb: <code>{sk.Reason}</code>");
                }
            }

            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"⏳ <b>Növbəti qrafik yoxlama:</b> <code>{nextCheckMinutes} dəqiqə sonra</code>");
            return sb.ToString();
        }

        public static string FormatNoSignalReason(string reason, int nextCheckMinutes = 60)
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
            sb.AppendLine($"🕒 <b>Tarix:</b> <code>{CryptoSense.Domain.Common.TimeHelper.NowFormatted}</code>");
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
