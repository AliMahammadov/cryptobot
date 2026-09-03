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
        private const string BaseUrl = "https://fapi.binance.com";

        public BinanceMarketDataProvider(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri(BaseUrl);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "CryptoSense-CleanArch/2.0");
        }

        public async Task<List<Kline>> GetKlinesAsync(string symbol, string interval = "15m", int limit = 100)
        {
            var klines = new List<Kline>();
            try
            {
                var cleanSym = symbol.ToUpper();
                if (cleanSym == "PEPEUSDT") cleanSym = "1000PEPEUSDT";
                else if (cleanSym == "SHIBUSDT") cleanSym = "1000SHIBUSDT";
                else if (cleanSym == "BONKUSDT") cleanSym = "1000BONKUSDT";
                else if (cleanSym == "FLOKIUSDT") cleanSym = "1000FLOKIUSDT";

                var url = $"/fapi/v1/klines?symbol={cleanSym}&interval={interval}&limit={limit}";
                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BinanceMarketDataProvider] Error fetching klines for {symbol}: {ex.Message}");
            }
            return klines;
        }

        public async Task<List<CoinTicker>> GetTopFuturesTickersAsync(int topCount = 35)
        {
            var result = new List<CoinTicker>();
            try
            {
                var response = await _httpClient.GetStringAsync("/fapi/v1/ticker/24hr");
                using var doc = JsonDocument.Parse(response);
                var list = new List<CoinTicker>();

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var symbol = item.GetProperty("symbol").GetString() ?? "";
                    if (!symbol.EndsWith("USDT")) continue;

                    var price = decimal.Parse(item.GetProperty("lastPrice").GetString()!, CultureInfo.InvariantCulture);
                    var priceChange = decimal.Parse(item.GetProperty("priceChangePercent").GetString()!, CultureInfo.InvariantCulture);
                    var quoteVol = decimal.Parse(item.GetProperty("quoteVolume").GetString()!, CultureInfo.InvariantCulture);
                    var high = decimal.Parse(item.GetProperty("highPrice").GetString()!, CultureInfo.InvariantCulture);
                    var low = decimal.Parse(item.GetProperty("lowPrice").GetString()!, CultureInfo.InvariantCulture);

                    list.Add(new CoinTicker
                    {
                        Symbol = symbol,
                        Price = price,
                        PriceChangePercent = priceChange,
                        VolumeQuote = quoteVol,
                        High24h = high,
                        Low24h = low
                    });

                    // Add normalized alias for 1000-prefix meme tokens (1000PEPEUSDT -> PEPEUSDT)
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
                result = list;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BinanceMarketDataProvider] Error fetching 24hr tickers: {ex.Message}");
            }
            return result;
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

                overview.BtcDominance = 59.1m;
                overview.UsdtDominance = 6.87m;
                overview.Summary = "BTC.D: ~59.1% | USDT.D: ~6.9% (Bazar Standart)";
                return overview;
            }
        }
    }
}
