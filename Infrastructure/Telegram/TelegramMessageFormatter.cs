using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CryptoSense.Application.DTOs;
using CryptoSense.Domain.Common;
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
            var directionLabel = isLong ? "LONG" : "SHORT";

            decimal tp1Dist = Math.Abs(signal.TakeProfit1 - signal.EntryPrice);
            decimal slDist = Math.Abs(signal.StopLoss - signal.EntryPrice);
            decimal tp1Pct = signal.EntryPrice > 0 ? (tp1Dist / signal.EntryPrice) * 100m : 0m;
            decimal slPct = signal.EntryPrice > 0 ? (slDist / signal.EntryPrice) * 100m : 0m;

            decimal tp2Dist = signal.TakeProfit2 > 0 ? Math.Abs(signal.TakeProfit2 - signal.EntryPrice) : 0m;
            decimal tp2Pct = (signal.EntryPrice > 0 && tp2Dist > 0) ? (tp2Dist / signal.EntryPrice) * 100m : 0m;
            decimal rr = slDist > 0 ? (tp1Dist / slDist) : 0m;

            var candleDuration = signal.Timeframe switch
            {
                "4h" => TimeSpan.FromHours(4),
                "15m" => TimeSpan.FromMinutes(15),
                "5m" => TimeSpan.FromMinutes(5),
                _ => TimeSpan.FromHours(1)
            };

            var candleCloseUtc = signal.CandleCloseTimeUtc != default
                ? signal.CandleCloseTimeUtc
                : (signal.SourceCandleOpenTimeUtc != default ? signal.SourceCandleOpenTimeUtc + candleDuration : signal.GeneratedAt);

            var genTime = signal.GeneratedAt != default ? signal.GeneratedAt : DateTime.UtcNow;

            var sb = new StringBuilder();
            sb.AppendLine($"#{userSigNum}  {directionLabel}  {cleanSymbol}  {signal.Timeframe}");
            sb.AppendLine($"{TimeHelper.ClockAz(genTime)}  |  şam {TimeHelper.ClockAz(candleCloseUtc)}");
            sb.AppendLine();
            sb.AppendLine($"Giriş  {signal.EntryPrice.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"SL     {signal.StopLoss.ToString(CultureInfo.InvariantCulture)}  (-{slPct.ToString("F1", CultureInfo.InvariantCulture)}%)");
            sb.AppendLine($"TP1    {signal.TakeProfit1.ToString(CultureInfo.InvariantCulture)}  (+{tp1Pct.ToString("F1", CultureInfo.InvariantCulture)}%)  40%");
            if (signal.TakeProfit2 > 0 && signal.TakeProfit2 != signal.TakeProfit1)
            {
                sb.AppendLine($"TP2    {signal.TakeProfit2.ToString(CultureInfo.InvariantCulture)}  (+{tp2Pct.ToString("F1", CultureInfo.InvariantCulture)}%)  30%");
            }
            sb.AppendLine($"R:R    {rr.ToString("F2", CultureInfo.InvariantCulture)}     güc {signal.ConfluenceScore.ToString("F0", CultureInfo.InvariantCulture)}");
            return sb.ToString();
        }

        public static string FormatOutcomeAlert(FuturesSignal signal, int userSigNum, string outcomeType, decimal hitPrice, decimal profitPct)
        {
            decimal netPnl = signal.NetResultPercent != 0 ? signal.NetResultPercent : profitPct;
            var cleanSymbol = signal.Symbol.Replace("USDT", "");
            var directionStr = (signal.Direction == SignalDirection.Buy || signal.SignalType.Contains("LONG")) ? "LONG" : "SHORT";
            bool isSl = outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL") || signal.Status == SignalStatus.Failed || (!outcomeType.Contains("TP") && profitPct < 0);

            var candleDuration = signal.Timeframe switch
            {
                "4h" => TimeSpan.FromHours(4),
                "15m" => TimeSpan.FromMinutes(15),
                "5m" => TimeSpan.FromMinutes(5),
                _ => TimeSpan.FromHours(1)
            };

            var candleCloseUtc = signal.CandleCloseTimeUtc != default
                ? signal.CandleCloseTimeUtc
                : (signal.SourceCandleOpenTimeUtc != default ? signal.SourceCandleOpenTimeUtc + candleDuration : signal.GeneratedAt);

            var genTime = signal.GeneratedAt != default ? signal.GeneratedAt : DateTime.UtcNow;
            var candleCloseStr = TimeHelper.ClockAz(candleCloseUtc);

            var timeLine = !string.IsNullOrEmpty(candleCloseStr)
                ? $"{TimeHelper.ClockNow}  |  siqnal {TimeHelper.ClockAz(genTime)}  |  şam {candleCloseStr}"
                : $"{TimeHelper.ClockNow}  |  siqnal {TimeHelper.ClockAz(genTime)}";

            var sb = new StringBuilder();
            if (outcomeType.Contains("TP1") || outcomeType.Contains("TP_A") || outcomeType.Contains("Hədəf 1") || outcomeType.Contains("Hədəf A"))
            {
                sb.AppendLine($"#{userSigNum}  TP1  {cleanSymbol}  {directionStr}");
                sb.AppendLine(timeLine);
                sb.AppendLine($"Qiymət  {hitPrice.ToString(CultureInfo.InvariantCulture)}");
                sb.AppendLine($"PnL     +{Math.Abs(netPnl).ToString("F2", CultureInfo.InvariantCulture)}%   (40% bağlandı)");
                sb.AppendLine("SL → giriş");
            }
            else if (outcomeType.Contains("TP2") || outcomeType.Contains("TP_B") || outcomeType.Contains("Hədəf 2") || outcomeType.Contains("Hədəf B"))
            {
                sb.AppendLine($"#{userSigNum}  TP2  {cleanSymbol}  {directionStr}");
                sb.AppendLine(timeLine);
                sb.AppendLine($"Qiymət  {hitPrice.ToString(CultureInfo.InvariantCulture)}");
                sb.AppendLine($"PnL     +{Math.Abs(netPnl).ToString("F2", CultureInfo.InvariantCulture)}%   (30% bağlandı)");
                sb.AppendLine("qalan 30% trail");
            }
            else if (isSl)
            {
                sb.AppendLine($"#{userSigNum}  SL  {cleanSymbol}  {directionStr}");
                sb.AppendLine(timeLine);
                sb.AppendLine($"Qiymət  {hitPrice.ToString(CultureInfo.InvariantCulture)}");
                sb.AppendLine($"PnL     -{Math.Abs(netPnl).ToString("F2", CultureInfo.InvariantCulture)}%   mövqe bağlı");
            }
            else
            {
                string reasonTag;
                if (!string.IsNullOrEmpty(signal.CloseReason))
                {
                    reasonTag = signal.CloseReason switch
                    {
                        "TIME" => "TIME",
                        "TRAIL" => "TRAIL",
                        "BE" => "BE",
                        "INVALIDATION" => "INV",
                        _ => signal.CloseReason
                    };
                }
                else if (outcomeType.Contains("TIME") || outcomeType.Contains("Müddəti Bitdi")) reasonTag = "TIME";
                else if (outcomeType.Contains("TRAIL") || outcomeType.Contains("Trailing")) reasonTag = "TRAIL";
                else if (outcomeType.Contains("BE") || outcomeType.Contains("Breakeven")) reasonTag = "BE";
                else if (outcomeType.Contains("INVALIDATION") || outcomeType.Contains("Struktur")) reasonTag = "INV";
                else reasonTag = outcomeType;

                var pnlSign = netPnl >= 0 ? "+" : "-";
                sb.AppendLine($"#{userSigNum}  {reasonTag}  {cleanSymbol}  {directionStr}");
                sb.AppendLine(timeLine);
                sb.AppendLine($"Qiymət  {hitPrice.ToString(CultureInfo.InvariantCulture)}");
                sb.AppendLine($"PnL     {pnlSign}{Math.Abs(netPnl).ToString("F2", CultureInfo.InvariantCulture)}%   mövqe bağlı");
            }

            return sb.ToString();
        }

        public static string FormatVolatilityRiskAlert(string symbol, decimal currentPrice, decimal priceChange24h, decimal volatilityRatio, string reason)
        {
            var cleanSymbol = symbol.Replace("USDT", "");
            var sign = priceChange24h >= 0 ? "+" : "";
            var sb = new StringBuilder();
            sb.AppendLine($"⚠️ YÜKSƏK VOLATİLLİK / RİSK BİLDİRİŞİ  |  {TimeHelper.ClockNow}");
            sb.AppendLine($"{cleanSymbol}  ${currentPrice.ToString(CultureInfo.InvariantCulture)}  ({sign}{priceChange24h.ToString("F2", CultureInfo.InvariantCulture)}%)  {volatilityRatio.ToString("F1", CultureInfo.InvariantCulture)}x həcm");
            sb.AppendLine($"Səbəb: {reason}");
            sb.AppendLine("bu coində tələsik giriş yox");
            return sb.ToString();
        }

        public static string FormatUrgentNewsAlert(CryptoNewsItem newsItem, bool isListing = false)
        {
            bool isList = isListing || newsItem.Title.Contains("List", StringComparison.OrdinalIgnoreCase) || newsItem.Title.Contains("Token", StringComparison.OrdinalIgnoreCase) || newsItem.OriginalTitle.Contains("List", StringComparison.OrdinalIgnoreCase) || newsItem.OriginalTitle.Contains("Token", StringComparison.OrdinalIgnoreCase);
            var header = isList ? "LIST" : "NEWS";
            var safeTitle = System.Net.WebUtility.HtmlEncode(newsItem.Title);

            var sb = new StringBuilder();
            sb.AppendLine($"{header}  |  {TimeHelper.ClockAz(newsItem.PublishedAt)}");
            sb.AppendLine(safeTitle);
            sb.AppendLine($"Mənbə: {newsItem.Source}  |  {newsItem.Sentiment}");
            if (!string.IsNullOrWhiteSpace(newsItem.Url))
            {
                sb.AppendLine(newsItem.Url);
            }
            return sb.ToString();
        }

        public static string FormatBtcCompass(BtcMarketCompass compass)
        {
            var sb = new StringBuilder();
            var chgSign = compass.Change24h >= 0 ? "+" : "";
            var stStr = compass.IsSuperTrendBullish ? "bull" : "bear";
            var regimeStr = compass.Regime.ToString();
            var priceStr = compass.Price > 0 ? $"${compass.Price.ToString("N0", CultureInfo.InvariantCulture)}" : "$0";
            var chgStr = $"{chgSign}{compass.Change24h.ToString("F1", CultureInfo.InvariantCulture)}";
            var trendShort = string.IsNullOrWhiteSpace(compass.Trend) ? "Neytral" : compass.Trend;

            sb.AppendLine($"BTC  {TimeHelper.ClockNow}");
            sb.AppendLine($"{regimeStr}  |  {priceStr}  |  24s {chgStr}%");
            sb.AppendLine($"ST {stStr}  |  {trendShort}");
            return sb.ToString().TrimEnd();
        }

        public static string FormatOpenSignalsList(List<(int Num, FuturesSignal Signal)> signals)
        {
            if (signals.Count == 0)
                return "Açıq mövqe yoxdur";

            var sb = new StringBuilder();
            foreach (var item in signals)
            {
                var sig = item.Signal;
                var n = item.Num;
                var isLong = sig.Direction == SignalDirection.Buy || sig.SignalType.Contains("LONG");
                var dir = isLong ? "L" : "S";
                var coin = sig.CleanSymbol;
                var tf = sig.Timeframe;
                var entry = sig.EntryPrice.ToString(CultureInfo.InvariantCulture);
                var sl = sig.StopLoss.ToString(CultureInfo.InvariantCulture);
                sb.AppendLine($"#{n} {coin} {dir} {tf} giriş {entry} SL {sl}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string FormatTodayStatsStrip(PerformanceStats stats)
        {
            var pnlSign = stats.TotalNetProfitPercent >= 0 ? "+" : "";
            var sb = new StringBuilder();
            sb.AppendLine($"BUGÜN  {TimeHelper.ClockNow}");
            sb.AppendLine($"{stats.TotalSignals} kart  |  {stats.SuccessSignals} TP  |  {stats.FailedSignals} SL  |  PnL {pnlSign}{stats.TotalNetProfitPercent.ToString("F2", CultureInfo.InvariantCulture)}%");
            return sb.ToString().TrimEnd();
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
            sb.AppendLine($"🎯 <b>Hədəf Bölgüsü:</b> Hədəf B (TP_B): {stats.Tp3HitsCount} ({stats.Tp3HitRatePercent}%) | Hədəf A (TP_A 1.5R): {stats.PartialHitsCount} | BE (Neytral): {stats.BreakevenHitsCount} | Vaxt Bitdi: {stats.TimeExpiredCount}");
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

        public static string FormatCoinPerformanceBreakdown(
            List<CryptoSense.Application.DTOs.CoinPerformanceBreakdownDto> breakdown, 
            List<string> monitoredCoins, 
            bool isPersonal = false)
        {
            var sb = new StringBuilder();
            if (isPersonal)
            {
                sb.AppendLine("📈 <b>Coinlər Üzrə Şəxsi Win-Rate və Dərin Statistika</b>");
                sb.AppendLine("<i>(Hesabınıza çatdırılmış bütün tamamlanmış və açıq əməliyyatlar)</i>");
            }
            else
            {
                sb.AppendLine("📈 <b>Coinlər Üzrə Qlobal Win-Rate və Dərin Statistika</b>");
                sb.AppendLine("<i>(Canlı verilənlər bazasındakı bütün tamamlanmış və açıq əməliyyatlar)</i>");
            }
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
            var tfDisplay = BotConstants.Timeframe.IsAll(settings.Timeframe) ? "1h, 4h" : (settings.Timeframe == "15m" ? "1h" : settings.Timeframe);
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
            var onOff = settings.IsActive ? "ON" : "OFF";
            var tf = string.IsNullOrWhiteSpace(settings.Timeframe) || settings.Timeframe == "Təyin olunmayıb"
                ? "—"
                : (BotConstants.Timeframe.IsAll(settings.Timeframe) ? "1h+4h" : settings.Timeframe);

            var last = string.IsNullOrWhiteSpace(lastSignalTime) || lastSignalTime == "Hələ yoxdur" ? "—" : lastSignalTime.Trim();
            if (last.Contains("|"))
            {
                var parts = last.Split('|');
                if (parts.Length > 1) last = parts[1].Trim().Split(' ')[0];
            }

            var sb = new StringBuilder();
            sb.AppendLine($"TERMINAL  {TimeHelper.ClockNow}");
            sb.AppendLine($"{onOff}  |  {tf}  |  {settings.Coins.Count} koin");
            sb.AppendLine($"açıq {activePositionsCount}/20  |  son {last}");
            return sb.ToString().TrimEnd();
        }

        public static string FormatStopConfirmPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("⚠️ <b>Bildirişləri Dayandırmaq</b>\n");
            sb.AppendLine("Siqnal bildirişlərini dayandırmaq istəyirsiniz?");
            sb.AppendLine("• Yeni siqnallar göndərilməyəcək.");
            sb.AppendLine("• Açıq mövqelər nəzarətdə qalacaq.");
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
            int skipDailyLoss = 0,
            decimal maxConfluenceSeen = -1,
            int skipStaleTrend = 0,
            int skipBtcBounce = 0,
            int skipDirLock = 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"HEARTBEAT  {TimeHelper.ClockNow}");
            sb.AppendLine($"göndərildi {sent}  |  chase {chase}  range {skipBtcRange}  staleST {skipStaleTrend}  RR {lowRr}  vol/gate {skipBtcGate}");

            if (sent == 0)
            {
                if (skipCircuitBreaker > 0)
                {
                    sb.AppendLine("səbəb: CircuitBreaker aktiv");
                }
                else if (skipDirLock > 0)
                {
                    sb.AppendLine("səbəb: DirLock aktiv");
                }
                else if (skipDailyLoss > 0)
                {
                    sb.AppendLine("səbəb: Günlük itki limiti keçib (≤ -3.0%)");
                }
                else if (skipMaxOpen > 0)
                {
                    sb.AppendLine($"səbəb: Maksimum açıq mövqe ({activeLocks}/20)");
                }
                else
                {
                    var reasonsList = new List<(string Name, int Count, string Description)>
                    {
                        ("vol/gate", skipBtcGate, "BTC bear/gate"),
                        ("range", skipBtcRange, "BTC ranging"),
                        ("staleST", skipStaleTrend, "SuperTrend köhnəlib"),
                        ("btcBounce", skipBtcBounce, "BTC bounce"),
                        ("gözləmə", skipGozleme, "zəif konsolidasiya"),
                        ("conf", skipConfluence, "Confluence < 75%"),
                        ("SL", slWide, "SL çox geniş"),
                        ("RR", lowRr, "R:R < 1.50"),
                        ("chase", chase, "chase"),
                        ("lock", activeLocks, "mövqe limiti"),
                        ("lag", skipLag, "şam gecikməsi"),
                        ("stale", skipStale, "stale qiymət")
                    };
                    var dominant = reasonsList.OrderByDescending(r => r.Count).FirstOrDefault(r => r.Count > 0);
                    string reasonText = dominant.Count > 0 
                        ? $"səbəb: {dominant.Name} ({dominant.Count})"
                        : "səbəb: A+ meyar yoxdur";

                    sb.AppendLine(reasonText);
                }
            }

            if (maxConfluenceSeen >= 0)
            {
                sb.AppendLine($"maxConf {maxConfluenceSeen.ToString("F1", CultureInfo.InvariantCulture)}%");
            }

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
            string wsStatus = (dataAgeMsBtc >= 0 && dataAgeMsBtc <= BotConstants.Thresholds.MaxDataAgeMs) ? "Sağlam 🟢" : "Gecikir 🔴";
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
