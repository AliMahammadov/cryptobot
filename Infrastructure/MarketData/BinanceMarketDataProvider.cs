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

                    list.Add(new CoinTicker
                    {
                        Symbol = symbol,
                        Price = decimal.Parse(item.GetProperty("lastPrice").GetString()!, CultureInfo.InvariantCulture),
                        PriceChangePercent = decimal.Parse(item.GetProperty("priceChangePercent").GetString()!, CultureInfo.InvariantCulture),
                        VolumeQuote = decimal.Parse(item.GetProperty("quoteVolume").GetString()!, CultureInfo.InvariantCulture),
                        High24h = decimal.Parse(item.GetProperty("highPrice").GetString()!, CultureInfo.InvariantCulture),
                        Low24h = decimal.Parse(item.GetProperty("lowPrice").GetString()!, CultureInfo.InvariantCulture)
                    });
                }

                list.Sort((a, b) => b.VolumeQuote.CompareTo(a.VolumeQuote));
                result = list.GetRange(0, Math.Min(topCount, list.Count));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BinanceMarketDataProvider] Error fetching 24hr tickers: {ex.Message}");
            }
            return result;
        }
    }
}
