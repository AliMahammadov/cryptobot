using System.Collections.Generic;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Application.Interfaces
{
    public interface ISignalEngine
    {
        Task<BtcMarketCompass> GetBtcCompassAsync();
        Task<FuturesSignal> AnalyzeCoinAsync(string symbol, string timeframe = "15m", bool isLiveScan = false);
        Task<List<FuturesSignal>> GetTrackedActiveSignalsAsync();
        Task<List<FuturesSignal>> GetSignalHistoryAsync(int count = 25);
        Task<PerformanceStats> GetPerformanceStatsAsync(string? specificTimeframe = null, List<string>? userCoins = null);
        Task<List<CryptoSense.Application.DTOs.CoinPerformanceBreakdownDto>> GetCoinPerformanceBreakdownAsync(List<string>? monitoredCoins = null);
        Task ClearAllSignalsAsync();
    }
}
