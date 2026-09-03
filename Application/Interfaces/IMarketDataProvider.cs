using System.Collections.Generic;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Application.Interfaces
{
    public interface IMarketDataProvider
    {
        Task<List<Kline>> GetKlinesAsync(string symbol, string interval = "15m", int limit = 100);
        Task<List<CoinTicker>> GetTopFuturesTickersAsync(int topCount = 35);
        Task<MacroMarketOverview> GetMacroMarketOverviewAsync();
    }
}
