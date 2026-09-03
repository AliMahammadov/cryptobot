using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Infrastructure.MarketData
{
    public class BinanceMarketDataProvider : IMarketDataProvider
    {
        private readonly HttpClient _httpClient;
        private static readonly HttpClient _visionClient = new() { Timeout = TimeSpan.FromSeconds(6) };
        private const string BaseUrl = "https://fapi.binance.com";
        private const string VisionUrl = "https://data-api.binance.vision";
        private static DateTime _futuresCoolDownUntil = DateTime.MinValue;
        private static readonly object _coolDownLock = new();

        public BinanceMarketDataProvider(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri(BaseUrl);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "CryptoSense-CleanArch/2.0");

            if (!_visionClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _visionClient.DefaultRequestHeaders.Add("User-Agent", "CryptoSense-Vision/2.0");
            }
        }

        public async Task<List<Kline>> GetKlinesAsync(string symbol, string interval = "15m", int limit = 100)
        {
            var cleanSym = symbol.ToUpper();
            var futuresSym = cleanSym;
            if (futuresSym == "PEPEUSDT") futuresSym = "1000PEPEUSDT";
            else if (futuresSym == "SHIBUSDT") futuresSym = "1000SHIBUSDT";
            else if (futuresSym == "BONKUSDT") futuresSym = "1000BONKUSDT";
            else if (futuresSym == "FLOKIUSDT") futuresSym = "1000FLOKIUSDT";

            var spotSym = cleanSym;
            if (spotSym.StartsWith("1000")) spotSym = spotSym.Substring(4);

            bool tryFutures = DateTime.UtcNow >= _futuresCoolDownUntil;

            if (tryFutures)
            {
                try
                {
                    var url = $"/fapi/v1/klines?symbol={futuresSym}&interval={interval}&limit={limit}";
                    var response = await _httpClient.GetStringAsync(url);
                    var list = ParseKlines(response);
                    if (list.Count > 0) return list;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int?)ex.StatusCode == 418 || (int?)ex.StatusCode == 451)
                {
                    Console.WriteLine($"[BinanceMarketDataProvider] Futures endpoint returned {(int?)ex.StatusCode}. Activating Vision fallback & 2-min cooldown.");
                    lock (_coolDownLock)
                    {
                        _futuresCoolDownUntil = DateTime.UtcNow.AddMinutes(2);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BinanceMarketDataProvider] Futures fetch error for {symbol}: {ex.Message}");
                }
            }

            // Fallback to Binance Vision Public Market Data API
            try
            {
                var visionUrl = $"{VisionUrl}/api/v3/klines?symbol={spotSym}&interval={interval}&limit={limit}";
                var response = await _visionClient.GetStringAsync(visionUrl);
                var list = ParseKlines(response);
                if (list.Count > 0) return list;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BinanceMarketDataProvider] Vision fallback error for {spotSym}: {ex.Message}");
            }

            return new List<Kline>();
        }

        private static List<Kline> ParseKlines(string json)
        {
            var klines = new List<Kline>();
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                klines.Add(new Kline
                {
                    OpenTime = item[0].GetInt64(),
                    Open = decimal.Parse(item[1].GetString()!, CultureInfo.InvariantCulture),
                    High = decimal.Parse(item[2].GetString()!, CultureInfo.InvariantCulture),
                    Low = decimal.Parse(item[3].GetString()!, CultureInfo.InvariantCulture),
                    Close = decimal.Parse(item[4].GetString()!, CultureInfo.InvariantCulture),
                    Volume = decimal.Parse(item[5].GetString()!, CultureInfo.InvariantCulture),
                    CloseTime = item[6].GetInt64()
                });
            }
            return klines;
        }

        public async Task<List<CoinTicker>> GetTopFuturesTickersAsync(int topCount = 35)
        {
            bool tryFutures = DateTime.UtcNow >= _futuresCoolDownUntil;

            if (tryFutures)
            {
                try
                {
                    var response = await _httpClient.GetStringAsync("/fapi/v1/ticker/24hr");
                    var list = ParseTickers(response);
                    if (list.Count > 0) return list.OrderByDescending(t => t.VolumeQuote).Take(topCount).ToList();
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int?)ex.StatusCode == 418 || (int?)ex.StatusCode == 451)
                {
                    lock (_coolDownLock)
                    {
                        _futuresCoolDownUntil = DateTime.UtcNow.AddMinutes(2);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BinanceMarketDataProvider] Error fetching 24hr tickers: {ex.Message}");
                }
            }

            // Fallback to Binance Vision 24hr ticker
            try
            {
                var response = await _visionClient.GetStringAsync($"{VisionUrl}/api/v3/ticker/24hr");
                var list = ParseTickers(response);
                if (list.Count > 0) return list.OrderByDescending(t => t.VolumeQuote).Take(topCount).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BinanceMarketDataProvider] Vision ticker fallback error: {ex.Message}");
            }

            return new List<CoinTicker>();
        }

        public async Task<CoinTicker?> Get24hTickerAsync(string symbol)
        {
            var cleanSym = symbol.ToUpper();
            if (!cleanSym.EndsWith("USDT")) cleanSym += "USDT";

            bool tryFutures = DateTime.UtcNow >= _futuresCoolDownUntil;
            if (tryFutures)
            {
                try
                {
                    var response = await _httpClient.GetStringAsync($"/fapi/v1/ticker/24hr?symbol={cleanSym}");
                    using var doc = JsonDocument.Parse(response);
                    var item = doc.RootElement;
                    if (decimal.TryParse(item.GetProperty("lastPrice").GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
                    {
                        decimal.TryParse(item.GetProperty("priceChangePercent").GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var change);
                        decimal.TryParse(item.GetProperty("quoteVolume").GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var vol);
                        decimal high = 0, low = 0;
                        if (item.TryGetProperty("highPrice", out var hp)) decimal.TryParse(hp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out high);
                        if (item.TryGetProperty("lowPrice", out var lp)) decimal.TryParse(lp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out low);

                        return new CoinTicker
                        {
                            Symbol = cleanSym,
                            Price = price,
                            PriceChangePercent = change,
                            VolumeQuote = vol,
                            High24h = high,
                            Low24h = low
                        };
                    }
                }
                catch { }
            }

            // Fallback to Vision / Spot
            try
            {
                var spotSym = cleanSym.StartsWith("1000") ? cleanSym.Substring(4) : cleanSym;
                var response = await _visionClient.GetStringAsync($"{VisionUrl}/api/v3/ticker/24hr?symbol={spotSym}");
                using var doc = JsonDocument.Parse(response);
                var item = doc.RootElement;
                if (decimal.TryParse(item.GetProperty("lastPrice").GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
                {
                    decimal.TryParse(item.GetProperty("priceChangePercent").GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var change);
                    decimal.TryParse(item.GetProperty("quoteVolume").GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var vol);
                    decimal high = 0, low = 0;
                    if (item.TryGetProperty("highPrice", out var hp)) decimal.TryParse(hp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out high);
                    if (item.TryGetProperty("lowPrice", out var lp)) decimal.TryParse(lp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out low);

                    return new CoinTicker
                    {
                        Symbol = cleanSym,
                        Price = price,
                        PriceChangePercent = change,
                        VolumeQuote = vol,
                        High24h = high,
                        Low24h = low
                    };
                }
            }
            catch { }

            return null;
        }

        private static List<CoinTicker> ParseTickers(string json)
        {
            var list = new List<CoinTicker>();
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var symbol = item.GetProperty("symbol").GetString() ?? "";
                if (!symbol.EndsWith("USDT")) continue;

                if (!decimal.TryParse(item.GetProperty("lastPrice").GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price)) continue;
                if (!decimal.TryParse(item.GetProperty("priceChangePercent").GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var priceChange)) continue;
                if (!decimal.TryParse(item.GetProperty("quoteVolume").GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var quoteVol)) continue;
                decimal high = 0, low = 0;
                if (item.TryGetProperty("highPrice", out var hp)) decimal.TryParse(hp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out high);
                if (item.TryGetProperty("lowPrice", out var lp)) decimal.TryParse(lp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out low);

                list.Add(new CoinTicker
                {
                    Symbol = symbol,
                    Price = price,
                    PriceChangePercent = priceChange,
                    VolumeQuote = quoteVol,
                    High24h = high,
                    Low24h = low
                });

                if (symbol.StartsWith("1000"))
                {
                    list.Add(new CoinTicker
                    {
                        Symbol = symbol.Substring(4),
                        Price = price,
                        PriceChangePercent = priceChange,
                        VolumeQuote = quoteVol,
                        High24h = high,
                        Low24h = low
                    });
                }
            }

            list.Sort((a, b) => b.VolumeQuote.CompareTo(a.VolumeQuote));
            return list;
        }

        private static MacroMarketOverview? _cachedMacro = null;
        private static DateTime _lastMacroFetch = DateTime.MinValue;
        private static readonly object _macroLock = new();

        private static readonly HttpClient _coinGeckoClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        public async Task<MacroMarketOverview> GetMacroMarketOverviewAsync()
        {
            if (_cachedMacro != null && (DateTime.UtcNow - _lastMacroFetch).TotalSeconds < 180)
            {
                return _cachedMacro;
            }

            var overview = new MacroMarketOverview();
            try
            {
                if (!_coinGeckoClient.DefaultRequestHeaders.Contains("User-Agent"))
                {
                    _coinGeckoClient.DefaultRequestHeaders.Add("User-Agent", "CryptoSense-MacroBot/2.0");
                }
                var json = await _coinGeckoClient.GetStringAsync("https://api.coingecko.com/api/v3/global");
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");

                if (data.TryGetProperty("market_cap_percentage", out var mcObj))
                {
                    if (mcObj.TryGetProperty("btc", out var btcVal))
                        overview.BtcDominance = Math.Round(btcVal.GetDecimal(), 2);
                    if (mcObj.TryGetProperty("usdt", out var usdtVal))
                        overview.UsdtDominance = Math.Round(usdtVal.GetDecimal(), 2);
                }

                if (data.TryGetProperty("total_market_cap", out var totalObj) && totalObj.TryGetProperty("usd", out var usdCap))
                {
                    overview.TotalMarketCapUsd = usdCap.GetDecimal();
                }

                if (data.TryGetProperty("market_cap_change_percentage_24h_usd", out var change24h))
                {
                    overview.MarketCapChange24h = Math.Round(change24h.GetDecimal(), 2);
                }

                overview.FetchedAtUtc = DateTime.UtcNow;
                overview.Summary = $"BTC.D: {overview.BtcDominance}% | USDT.D: {overview.UsdtDominance}% | 24h: {overview.MarketCapChange24h:+0.00;-0.00}%";

                lock (_macroLock)
                {
                    _cachedMacro = overview;
                    _lastMacroFetch = DateTime.UtcNow;
                }
                return overview;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BinanceMarketDataProvider] CoinGecko global API fallback: {ex.Message}");
                if (_cachedMacro != null) return _cachedMacro;

                // Dynamic Fallback via CoinCap API
                try
                {
                    var ccJson = await _coinGeckoClient.GetStringAsync("https://api.coincap.io/v2/assets?limit=15");
                    using var ccDoc = JsonDocument.Parse(ccJson);
                    var arr = ccDoc.RootElement.GetProperty("data").EnumerateArray().ToList();
                    decimal totalTopCap = 0;
                    decimal btcCap = 0;
                    decimal usdtCap = 0;
                    foreach (var asset in arr)
                    {
                        var sym = asset.GetProperty("symbol").GetString();
                        if (decimal.TryParse(asset.GetProperty("marketCapUsd").GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var mCap))
                        {
                            totalTopCap += mCap;
                            if (sym == "BTC") btcCap = mCap;
                            if (sym == "USDT") usdtCap = mCap;
                        }
                    }
                    if (totalTopCap > 0 && btcCap > 0)
                    {
                        // Normalize against broader crypto market (~65% of top 15 cap is total cap)
                        overview.BtcDominance = Math.Round((btcCap / totalTopCap) * 78.5m, 2);
                        overview.UsdtDominance = Math.Round((usdtCap / totalTopCap) * 12.0m, 2);
                        overview.Summary = $"BTC.D (Dinamik): {overview.BtcDominance}% | USDT.D: {overview.UsdtDominance}%";
                        overview.FetchedAtUtc = DateTime.UtcNow;
                        lock (_macroLock) { _cachedMacro = overview; _lastMacroFetch = DateTime.UtcNow; }
                        return overview;
                    }
                }
                catch (Exception ccEx)
                {
                    Console.WriteLine($"[BinanceMarketDataProvider] CoinCap fallback error: {ccEx.Message}");
                }

                // If completely offline, mark as dynamic pending without fake static numbers
                overview.BtcDominance = 0;
                overview.UsdtDominance = 0;
                overview.Summary = "Bazar dominasiya məlumatı canlı yenilənir...";
                return overview;
            }
        }
    }
}
