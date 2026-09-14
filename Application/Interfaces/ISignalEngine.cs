using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Application.Interfaces
{
    public interface ISignalEngine
    {
        Task<BtcMarketCompass> GetBtcCompassAsync();
        Task<FuturesSignal> AnalyzeCoinAsync(string symbol, string timeframe = "1h", bool isLiveScan = false, CancellationToken ct = default);
        Task<List<FuturesSignal>> GetTrackedActiveSignalsAsync();
        Task<List<FuturesSignal>> GetSignalHistoryAsync(int count = 25);
        Task<PerformanceStats> GetPerformanceStatsAsync(string? specificTimeframe = null, List<string>? userCoins = null, DateTime? sinceUtc = null, DateTime? untilUtc = null, bool isAllTime = false);
        Task<PerformanceStats> GetUserPerformanceStatsAsync(string chatId, string? specificTimeframe = null, List<string>? userCoins = null, DateTime? sinceUtc = null, DateTime? untilUtc = null, bool isAllTime = false);
        Task<List<FuturesSignal>> GetUserOpenSignalsAsync(string chatId);
        Task ClearUserHistoryAsync(string chatId);
        Task<List<CryptoSense.Application.DTOs.CoinPerformanceBreakdownDto>> GetCoinPerformanceBreakdownAsync(List<string>? monitoredCoins = null);
        Task ClearAllSignalsAsync();
    }
}
