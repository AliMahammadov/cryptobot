using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Application.Services
{
    public class NewsService : INewsService
    {
        private readonly HttpClient _httpClient;
        private static NewsSentimentSummary? _cachedSummary;
        private static DateTime _lastFetchTime = DateTime.MinValue;
        private static readonly System.Threading.SemaphoreSlim _newsLock = new(1, 1);
        private static bool _rateLimited = false;
        private static DateTime _rateLimitResetTime = DateTime.MinValue;
        private static readonly ConcurrentDictionary<string, string> _translationCache = new();

        private static readonly string[] BullishWords = new[]
        {
            "surge", "surges", "soar", "soars", "jump", "jumps", "rally", "rallies", "bull", "bullish", "record",
            "all-time high", "ath", "adoption", "approved", "approval", "inflow", "inflows", "gain", "gains",
            "pump", "breakout", "accumulate", "accumulation", "growth", "optimistic", "partnership", "upgrade", "buys", "buy", "green"
        };

        private static readonly string[] BearishWords = new[]
        {
            "crash", "crashes", "plunge", "plunges", "drop", "drops", "fall", "falls", "bear", "bearish", "ban",
            "bans", "lawsuit", "sue", "sues", "sec", "hack", "hacked", "stolen", "dump", "dumps", "outflow",
            "outflows", "liquidation", "liquidated", "scam", "warning", "panic", "fear", "down", "collapse", "decline", "red"
        };

        private static readonly Dictionary<string, string> DictionaryAz = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Bitcoin", "Bitcoin" },
            { "Ethereum", "Ethereum" },
            { "Crypto", "Kripto" },
            { "Surges", "Sürətlə yüksəlir" },
            { "Plunges", "Kəskin düşür" },
            { "Rally", "Yüksəliş dalğası" },
            { "Adoption", "Kütləvi qəbul" },
            { "Inflows", "Kapital axınları" },
            { "Outflows", "Kapital çıxışları" },
            { "Liquidation", "Likvidasiya" },
            { "Record", "Yeni rekord" }
        };

        public NewsService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CryptoSense-News/2.0");
        }

        public async Task<NewsSentimentSummary> GetNewsAndSentimentAsync()
        {
            if (_cachedSummary != null && DateTime.UtcNow - _lastFetchTime < TimeSpan.FromMinutes(2))
            {
                return _cachedSummary;
            }

            await _newsLock.WaitAsync();
            try
            {
                if (_cachedSummary != null && DateTime.UtcNow - _lastFetchTime < TimeSpan.FromMinutes(2))
                {
                    return _cachedSummary;
                }

            var summary = new NewsSentimentSummary();
            var allItems = new List<CryptoNewsItem>();

            var sources = new (string Name, string Url)[]
            {
                ("CoinTelegraph", "https://cointelegraph.com/rss"),
                ("CoinDesk", "https://www.coindesk.com/arc/outboundfeeds/rss/"),
                ("Decrypt", "https://decrypt.co/feed"),
                ("Bitcoin Magazine", "https://bitcoinmagazine.com/feed")
            };

            foreach (var (sourceName, feedUrl) in sources)
            {
                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var content = await _httpClient.GetStringAsync(feedUrl, cts.Token);
                    var xdoc = XDocument.Parse(content);
                    var items = xdoc.Descendants("item").Take(3);

                    foreach (var item in items)
                    {
                        var title = item.Element("title")?.Value?.Trim() ?? "";
                        var link = item.Element("link")?.Value?.Trim() ?? "";
                        var pubDateStr = item.Element("pubDate")?.Value ?? "";
                        DateTime.TryParse(pubDateStr, out var pubDate);
                        if (pubDate == DateTime.MinValue) pubDate = DateTime.UtcNow;

                        if (string.IsNullOrWhiteSpace(title)) continue;

                        var (sentiment, score) = AnalyzeTextSentiment(title);
                        var titleAz = await TranslateToAzAsync(title);

                        allItems.Add(new CryptoNewsItem
                        {
                            Title = titleAz,
                            Source = sourceName,
                            Url = link,
                            PublishedAt = pubDate,
                            Sentiment = sentiment,
                            SentimentScore = score
                        });
                    }
                }
                catch
                {
                }
            }

            if (allItems.Count == 0)
            {
                summary.LatestNews = new List<CryptoNewsItem>();
                summary.BullishCount = 0;
                summary.BearishCount = 0;
                summary.OverallScore = 0;
                summary.Status = "XƏBƏR ƏLÇATMAZDIR (NEYTRAL) ⚪";
                _cachedSummary = summary;
                _lastFetchTime = DateTime.UtcNow;
                return summary;
            }

            allItems.Sort((a, b) => b.PublishedAt.CompareTo(a.PublishedAt));
            summary.LatestNews = allItems.Take(15).ToList();

            summary.BullishCount = summary.LatestNews.Count(n => n.SentimentScore > 0);
            summary.BearishCount = summary.LatestNews.Count(n => n.SentimentScore < 0);

            var avgScore = summary.LatestNews.Count > 0 ? (int)summary.LatestNews.Average(n => n.SentimentScore) : 0;
            summary.OverallScore = Math.Clamp(avgScore, -100, 100);

            if (summary.OverallScore >= 25) summary.Status = "BULLISH (MÜSBƏT) 🟢";
            else if (summary.OverallScore <= -25) summary.Status = "BEARISH (MƏNFİ) 🔴";
            else summary.Status = "NEYTRAL (BALANS) ⚪";

            _cachedSummary = summary;
            _lastFetchTime = DateTime.UtcNow;

            return summary;
            }
            finally
            {
                _newsLock.Release();
            }
        }

        private async Task<string> TranslateToAzAsync(string englishText)
        {
            if (_translationCache.TryGetValue(englishText, out var cached))
            {
                return cached;
            }

            if (_rateLimited && DateTime.UtcNow < _rateLimitResetTime)
            {
                return LocalDictionaryTranslate(englishText);
            }

            try
            {
                var cleanQuery = Regex.Replace(englishText, @"[^\w\s\$\-\.\%]", " ");
                var encoded = Uri.EscapeDataString(cleanQuery);
                var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=az&dt=t&q=" + encoded;
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                var response = await _httpClient.GetStringAsync(url, cts.Token);
                using var doc = JsonDocument.Parse(response);
                var firstArr = doc.RootElement[0];
                var sb = new StringBuilder();
                foreach (var item in firstArr.EnumerateArray())
                {
                    if (item.GetArrayLength() > 0)
                    {
                        sb.Append(item[0].GetString());
                    }
                }
                var translated = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    var clean = CleanAzText(translated);
                    _translationCache[englishText] = clean;
                    _rateLimited = false;
                    return clean;
                }
            }
            catch
            {
                _rateLimited = true;
                _rateLimitResetTime = DateTime.UtcNow.AddMinutes(5);
            }

            var fallback = LocalDictionaryTranslate(englishText);
            _translationCache[englishText] = fallback;
            return fallback;
        }

        private static string LocalDictionaryTranslate(string text)
        {
            var result = text;
            foreach (var kvp in DictionaryAz)
            {
                result = Regex.Replace(result, $@"\b{Regex.Escape(kvp.Key)}\b", kvp.Value, RegexOptions.IgnoreCase);
            }
            return CleanAzText(result);
        }

        private static string CleanAzText(string text)
        {
            text = text.Replace("&amp;", "&").Replace("&quot;", "\"").Replace("&#39;", "'");
            return text;
        }

        private static (string Sentiment, int Score) AnalyzeTextSentiment(string text)
        {
            var lower = text.ToLowerInvariant();
            int score = 0;

            foreach (var w in BullishWords)
            {
                if (lower.Contains(w)) score += 30;
            }

            foreach (var w in BearishWords)
            {
                if (lower.Contains(w)) score -= 35;
            }

            score = Math.Clamp(score, -100, 100);

            if (score >= 25) return ("BULLISH (MÜSBƏT) 🟢", score);
            if (score <= -25) return ("BEARISH (MƏNFİ) 🔴", score);
            return ("NEYTRAL ⚪", 0);
        }
    }
}
