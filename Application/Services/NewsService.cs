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
using System.Globalization;
using System.IO;
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
            "pump", "breakout", "accumulate", "accumulation", "growth", "optimistic", "partnership", "upgrade", "buys", "buy", "buying", "bought", "green"
        };

        private static readonly string[] BearishWords = new[]
        {
            "crash", "crashes", "plunge", "plunges", "drop", "drops", "fall", "falls", "bear", "bearish", "ban",
            "bans", "lawsuit", "sue", "sues", "sec", "hack", "hacked", "stolen", "dump", "dumps", "dumping", "outflow",
            "outflows", "liquidation", "liquidated", "scam", "warning", "panic", "fear", "down", "collapse", "decline", "declines", "red",
            "halt", "halts", "halted", "pause", "pauses", "paused", "stop", "stops", "stopped", "suspend", "suspends", "suspended",
            "sell", "sells", "selling", "sold", "loss", "losses", "cut", "cuts", "freeze", "freezes", "frozen", "reject", "rejected", "rejection", "fail", "fails", "failed", "downturn"
        };

        private static readonly string[] BearishNegationPrefixes = new[]
        {
            "halt", "halts", "halted", "halting",
            "stop", "stops", "stopped", "stopping",
            "pause", "pauses", "paused", "pausing",
            "suspend", "suspends", "suspended", "suspending",
            "cancel", "cancels", "canceled", "cancelling",
            "delay", "delays", "delayed", "delaying",
            "freeze", "freezes", "frozen",
            "reject", "rejects", "rejected",
            "fail", "fails", "failed",
            "cut", "cuts", "cutting"
        };

        private static readonly string[] BullishActionTargets = new[]
        {
            "buy", "buys", "buying", "bought", "purchase", "purchases", "purchasing",
            "inflow", "inflows", "accumulation", "accumulating", "accumulate",
            "adoption", "etf", "approval", "rally", "growth"
        };

        private static readonly Dictionary<string, string> DictionaryAz = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Bitcoin", "Bitcoin" },
            { "Ethereum", "Ethereum" },
            { "Crypto", "Kripto" },
            { "Strategy", "MicroStrategy" },
            { "Halted", "Dayandırdı" },
            { "Halt", "Dayandırıldı" },
            { "Buys", "Alışları" },
            { "Buy", "Alış" },
            { "Buying", "Alışı" },
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
            if (_cachedSummary != null && DateTime.UtcNow - _lastFetchTime < TimeSpan.FromSeconds(30))
            {
                return _cachedSummary;
            }

            await _newsLock.WaitAsync();
            try
            {
                if (_cachedSummary != null && DateTime.UtcNow - _lastFetchTime < TimeSpan.FromSeconds(30))
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
                        var items = xdoc.Descendants("item").Take(10);

                        foreach (var item in items)
                        {
                            var title = item.Element("title")?.Value?.Trim() ?? "";
                            var link = item.Element("link")?.Value?.Trim() ?? "";
                            var pubDateStr = item.Element("pubDate")?.Value 
                                          ?? item.Element(XName.Get("date", "http://purl.org/dc/elements/1.1/"))?.Value 
                                          ?? "";
                            DateTime pubDate = DateTime.MinValue;
                            if (!string.IsNullOrWhiteSpace(pubDateStr))
                            {
                                if (DateTimeOffset.TryParse(pubDateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDto))
                                {
                                    pubDate = parsedDto.UtcDateTime;
                                }
                                else if (DateTime.TryParse(pubDateStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedDt))
                                {
                                    pubDate = parsedDt.ToUniversalTime();
                                }
                            }

                            if (pubDate == DateTime.MinValue)
                            {
                                // Qətiyyən indiki zaman götürmürük; etibarlı tarix yoxdursa köhnə xəbərin yanlışlıqla yeni kimi çıxmasının qarşısı alınır
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(title)) continue;

                            var (sentiment, score) = AnalyzeTextSentiment(title);
                            var titleAz = await TranslateToAzAsync(title);

                            allItems.Add(new CryptoNewsItem
                            {
                                Title = titleAz,
                                OriginalTitle = title,
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

        public class SentNewsRecord
        {
            public string Title { get; set; } = "";
            public string OriginalTitle { get; set; } = "";
            public string NormalizedTitle { get; set; } = "";
            public List<string> Keywords { get; set; } = new();
            public string Url { get; set; } = "";
            public DateTime PublishedAtUtc { get; set; }
            public DateTime SentAtUtc { get; set; }
        }

        private static readonly string DataDirectory = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAILWAY_VOLUME_MOUNT_PATH")) && Directory.Exists(Environment.GetEnvironmentVariable("RAILWAY_VOLUME_MOUNT_PATH"))
            ? Environment.GetEnvironmentVariable("RAILWAY_VOLUME_MOUNT_PATH")!
            : (Directory.Exists("/app/data") ? "/app/data" : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
        private static readonly string SentNewsFilePath = Path.Combine(DataDirectory, "sent_news_history.json");
        private static readonly object _sentNewsLock = new();
        private static List<SentNewsRecord> _sentNewsRecords = new();
        private static DateTime _lastPublishedCutoffUtc = DateTime.UtcNow.AddMinutes(-5);

        static NewsService()
        {
            LoadSentNewsHistory();
        }

        private static void LoadSentNewsHistory()
        {
            lock (_sentNewsLock)
            {
                try
                {
                    if (File.Exists(SentNewsFilePath))
                    {
                        var json = File.ReadAllText(SentNewsFilePath);
                        var list = JsonSerializer.Deserialize<List<SentNewsRecord>>(json);
                        if (list != null)
                        {
                            var cutoff24h = DateTime.UtcNow.AddHours(-24);
                            _sentNewsRecords = list.Where(r => r.SentAtUtc >= cutoff24h).ToList();
                            if (_sentNewsRecords.Count > 0)
                            {
                                var maxSentPub = _sentNewsRecords.Max(r => r.PublishedAtUtc);
                                _lastPublishedCutoffUtc = maxSentPub > DateTime.UtcNow.AddMinutes(-30)
                                    ? maxSentPub
                                    : DateTime.UtcNow.AddMinutes(-5);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NewsService] LoadSentNewsHistory error: {ex.Message}");
                }
            }
        }

        private static void SaveSentNewsHistory()
        {
            lock (_sentNewsLock)
            {
                try
                {
                    if (!Directory.Exists(DataDirectory))
                    {
                        Directory.CreateDirectory(DataDirectory);
                    }
                    var cutoff24h = DateTime.UtcNow.AddHours(-24);
                    _sentNewsRecords = _sentNewsRecords.Where(r => r.SentAtUtc >= cutoff24h).ToList();
                    var json = JsonSerializer.Serialize(_sentNewsRecords, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(SentNewsFilePath, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NewsService] SaveSentNewsHistory error: {ex.Message}");
                }
            }
        }

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "in", "on", "at", "to", "for", "of", "with", "by", "from", "up", "about",
            "into", "over", "after", "is", "are", "was", "were", "be", "been", "being", "have", "has",
            "had", "do", "does", "did", "but", "and", "or", "as", "if", "not", "new", "says", "market",
            "price", "crypto", "cryptocurrency", "today", "now", "just", "will", "this", "that", "these",
            "those", "its", "it's", "their", "more", "than", "down", "post", "read", "view",
            "bu", "bir", "ve", "və", "ile", "ilə", "üçün", "ucun", "haqqinda", "haqqında", "kimi", "yeni", "teze", "təzə",
            "qiyməti", "qiymet", "bazar", "kripto", "bugün", "indi", "artıq", "üzrə", "sonra"
        };

        public static List<string> ExtractSignificantKeywords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new();
            var lower = text.ToLowerInvariant();
            var words = Regex.Matches(lower, @"\b[a-z0-9]{3,}\b")
                             .Select(m => m.Value)
                             .Where(w => !StopWords.Contains(w))
                             .Distinct()
                             .ToList();
            return words;
        }

        public static string NormalizeNewsTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            var lower = title.ToLowerInvariant();
            var sb = new StringBuilder();
            foreach (var ch in lower)
            {
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                {
                    sb.Append(ch);
                }
            }
            return Regex.Replace(sb.ToString().Trim(), @"\s+", " ");
        }

        public static double CalculateTitleDifference(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2)) return 0.0;
            if (string.IsNullOrEmpty(s1)) return 1.0;
            if (string.IsNullOrEmpty(s2)) return 1.0;

            int n = s1.Length;
            int m = s2.Length;
            int[,] d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (s2[j - 1] == s1[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }

            return (double)d[n, m] / Math.Max(n, m);
        }

        public async Task<List<CryptoNewsItem>> GetUrgentBreakingNewsAndListingsAsync()
        {
            var summary = await GetNewsAndSentimentAsync();
            var urgentItems = new List<CryptoNewsItem>();
            if (summary.LatestNews == null || summary.LatestNews.Count == 0) return urgentItems;

            var listingWords = new[] { "list", "lists", "listing", "launch", "binance", "airdrop", "token", "təzə", "yeni" };
            var breakingWords = new[] { "sec", "fed", "interest rate", "faiz", "rate cut", "rate hike", "inflation", "cpi", "emergency", "təcili", "hack", "exploit", "etf", "approval", "təsdiq", "court", "lawsuit", "məhkəmə", "tariff", "war", "ban", "qadağa", "breaking", "ath", "all-time high" };

            var now = DateTime.UtcNow;
            DateTime highestPublishedInBatch = _lastPublishedCutoffUtc;

            lock (_sentNewsLock)
            {
                // Filter out records older than 24h
                var cutoff24h = now.AddHours(-24);
                _sentNewsRecords.RemoveAll(r => r.SentAtUtc < cutoff24h);

                foreach (var item in summary.LatestNews)
                {
                    // Track highest publication date seen in current batch (not exceeding now + 2m for clock skew)
                    if (item.PublishedAt > highestPublishedInBatch && item.PublishedAt <= now.AddMinutes(2))
                    {
                        highestPublishedInBatch = item.PublishedAt;
                    }

                    // QAYDA 1: ZAMAN FİLTRİ (Publication Time Filter)
                    // 14:56-da paylaşılan xəbərdən sonra 14:57, 14:58-də yalnız həmin dəqiqələrdə paylaşılanlar göndərilir!
                    // Köhnə vaxtda paylaşılan xəbərlər QƏTİYYƏN bir daha göndərilmir.
                    if (item.PublishedAt <= _lastPublishedCutoffUtc)
                    {
                        continue;
                    }
                    if (now - item.PublishedAt > TimeSpan.FromMinutes(15))
                    {
                        continue;
                    }

                    var searchBody = (item.OriginalTitle + " " + item.Title + " " + item.Url).ToLowerInvariant();
                    bool isListing = listingWords.Any(w => searchBody.Contains(w));
                    bool isBreaking = breakingWords.Any(w => searchBody.Contains(w));

                    if (isListing || isBreaking || Math.Abs(item.SentimentScore) >= 60)
                    {
                        var normAzTitle = NormalizeNewsTitle(item.Title);
                        var normEnTitle = NormalizeNewsTitle(item.OriginalTitle);
                        var azKeywords = ExtractSignificantKeywords(item.Title);
                        var enKeywords = ExtractSignificantKeywords(item.OriginalTitle);
                        var combinedItemKeywords = azKeywords.Union(enKeywords, StringComparer.OrdinalIgnoreCase).Distinct().ToList();

                        // QAYDA 2: GÜN ƏRZİNDƏ EYNİ TİP XƏBƏRİN TƏKRAR BLOKLANMASI (24 saatlıq unikal mövzu yoxlaması)
                        // "Bir xəbər gün ərzində bir dəfə gəldi, ikinci səfər eyni tip xəbər gəlməsin"
                        bool isDuplicate = false;

                        foreach (var sent in _sentNewsRecords)
                        {
                            // 1. Eyni URL
                            if (!string.IsNullOrWhiteSpace(item.Url) && !string.IsNullOrWhiteSpace(sent.Url) &&
                                string.Equals(item.Url.Trim(), sent.Url.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                isDuplicate = true;
                                break;
                            }

                            // 2. Yüksək başlıq bənzərliyi (Levenshtein fərqi < 30%) - həm AZ, həm EN
                            if (!string.IsNullOrWhiteSpace(sent.NormalizedTitle) && !string.IsNullOrWhiteSpace(normAzTitle))
                            {
                                if (CalculateTitleDifference(sent.NormalizedTitle, normAzTitle) < 0.30)
                                {
                                    isDuplicate = true;
                                    break;
                                }
                                if (sent.NormalizedTitle.Length >= 15 && (sent.NormalizedTitle.Contains(normAzTitle) || normAzTitle.Contains(sent.NormalizedTitle)))
                                {
                                    isDuplicate = true;
                                    break;
                                }
                            }
                            if (!string.IsNullOrWhiteSpace(sent.OriginalTitle) && !string.IsNullOrWhiteSpace(normEnTitle))
                            {
                                var sentNormEn = NormalizeNewsTitle(sent.OriginalTitle);
                                if (CalculateTitleDifference(sentNormEn, normEnTitle) < 0.30)
                                {
                                    isDuplicate = true;
                                    break;
                                }
                                if (sentNormEn.Length >= 15 && (sentNormEn.Contains(normEnTitle) || normEnTitle.Contains(sentNormEn)))
                                {
                                    isDuplicate = true;
                                    break;
                                }
                            }

                            // 3. Eyni tip / mövzu açar söz üst-üstə düşməsi (Jaccard similarity >= 30%)
                            if (combinedItemKeywords.Count >= 2 && sent.Keywords.Count >= 2)
                            {
                                int intersection = combinedItemKeywords.Intersect(sent.Keywords, StringComparer.OrdinalIgnoreCase).Count();
                                int union = combinedItemKeywords.Union(sent.Keywords, StringComparer.OrdinalIgnoreCase).Count();
                                if (union > 0 && ((double)intersection / union) >= 0.30)
                                {
                                    isDuplicate = true;
                                    break;
                                }
                            }

                            // 4. Eyni Kriptovalyuta / Birja + Hadisə kombinasiyası (24 saat ərzində təkrar qadağandır)
                            var keyEntities = new[] { "btc", "bitcoin", "eth", "ethereum", "sol", "solana", "xrp", "ripple", "bnb", "binance", "doge", "pepe", "shib", "cardano", "ada", "sec", "fed", "cpi", "etf", "ftx", "tether", "usdt" };
                            var actions = new[] { "list", "listing", "launch", "delist", "delisting", "sec", "fed", "hack", "exploit", "etf", "approval", "rate", "faiz", "court", "lawsuit", "təsdiq", "qadağa", "ban", "ath" };

                            var itemEntities = combinedItemKeywords.Where(w => keyEntities.Contains(w.ToLowerInvariant())).Distinct().ToList();
                            var sentEntities = sent.Keywords.Where(w => keyEntities.Contains(w.ToLowerInvariant())).Distinct().ToList();

                            if (itemEntities.Count > 0 && sentEntities.Count > 0)
                            {
                                var sharedEntities = itemEntities.Intersect(sentEntities, StringComparer.OrdinalIgnoreCase).ToList();
                                if (sharedEntities.Count >= 2)
                                {
                                    isDuplicate = true;
                                    break;
                                }
                                if (sharedEntities.Count == 1)
                                {
                                    bool itemHasAction = combinedItemKeywords.Any(w => actions.Contains(w.ToLowerInvariant()));
                                    bool sentHasAction = sent.Keywords.Any(w => actions.Contains(w.ToLowerInvariant()));
                                    if (itemHasAction && sentHasAction)
                                    {
                                        isDuplicate = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (isDuplicate)
                        {
                            continue;
                        }

                        // Yeni və unikal xəbər qeydə alınır
                        _sentNewsRecords.Add(new SentNewsRecord
                        {
                            Title = item.Title,
                            OriginalTitle = item.OriginalTitle,
                            NormalizedTitle = normAzTitle,
                            Keywords = combinedItemKeywords,
                            Url = item.Url ?? "",
                            PublishedAtUtc = item.PublishedAt,
                            SentAtUtc = now
                        });

                        urgentItems.Add(item);
                    }
                }

                // Qayda 1-in davamı: Watermark irəlilədilir ki, 14:56-dakı xəbərlər 14:57 və 14:58-də bir daha əsla yoxlanmasın
                if (highestPublishedInBatch > _lastPublishedCutoffUtc)
                {
                    _lastPublishedCutoffUtc = highestPublishedInBatch;
                }

                if (urgentItems.Count > 0)
                {
                    SaveSentNewsHistory();
                }
            }
            return urgentItems;
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

        public static (string Sentiment, int Score) AnalyzeTextSentiment(string text)
        {
            var lower = text.ToLowerInvariant();
            int score = 0;

            // 1. Kritik Qayda: İnkar və Alışın Dayandırılması (məs: "Strategy Halted Its Bitcoin Buys")
            // Əgər alış, kapital axını və ya böyümə dayandırılıb/ləğv edilibsə, bu qəti şəkildə GÜCLÜ BEARISH-dir!
            bool hasBearishNegation = false;
            foreach (var neg in BearishNegationPrefixes)
            {
                if (Regex.IsMatch(lower, $@"\b{Regex.Escape(neg)}\b", RegexOptions.IgnoreCase))
                {
                    foreach (var target in BullishActionTargets)
                    {
                        if (Regex.IsMatch(lower, $@"\b{Regex.Escape(target)}\b", RegexOptions.IgnoreCase))
                        {
                            hasBearishNegation = true;
                            break;
                        }
                    }
                }
                if (hasBearishNegation) break;
            }

            if (hasBearishNegation)
            {
                score = -75;
                return ("BEARISH (MƏNFİ) 🔴", score);
            }

            // 2. Bearish sözlərin dəqiq söz sərhədi (\b) ilə axtarışı
            foreach (var w in BearishWords)
            {
                if (Regex.IsMatch(lower, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase))
                {
                    score -= 35;
                }
            }

            // 3. Bullish sözlərin dəqiq söz sərhədi (\b) ilə axtarışı (yalnız inkar olmadıqda)
            foreach (var w in BullishWords)
            {
                if (Regex.IsMatch(lower, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase))
                {
                    score += 30;
                }
            }

            score = Math.Clamp(score, -100, 100);

            if (score >= 25) return ("BULLISH (MÜSBƏT) 🟢", score);
            if (score <= -25) return ("BEARISH (MƏNFİ) 🔴", score);
            return ("NEYTRAL ⚪", 0);
        }
    }
}
