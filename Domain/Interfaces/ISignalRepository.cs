using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;

namespace CryptoSense.Domain.Interfaces
{
    public interface ISignalRepository
    {
        Task<FuturesSignal?> GetByIdAsync(int id);
        Task<FuturesSignal?> GetExistingCandleSignalAsync(string symbol, string timeframe, DateTime sourceCandleOpenTimeUtc);
        Task<List<FuturesSignal>> GetOpenTrackedSignalsAsync();
        Task<List<FuturesSignal>> GetRecentSignalsAsync(int count = 25);
        Task<List<FuturesSignal>> GetSignalsSinceAsync(DateTime sinceUtc);
        Task<int> GetMaxSignalNumberAsync();
        Task AddAsync(FuturesSignal signal);
        Task UpdateAsync(FuturesSignal signal);
        Task<PerformanceStats> GetPerformanceStatsAsync(string? specificTimeframe = null, List<string>? userCoins = null, DateTime? sinceUtc = null, DateTime? untilUtc = null, bool isAllTime = false);
        Task<int> GetUserTodaySignalsCountAsync(string chatId);
        Task<int> GetUserOpenSignalsCountAsync(string chatId);
        Task<PerformanceStats> GetUserPerformanceStatsAsync(string chatId, string? specificTimeframe = null, List<string>? userCoins = null, DateTime? sinceUtc = null, DateTime? untilUtc = null, bool isAllTime = false);
        Task<List<FuturesSignal>> GetUserOpenSignalsAsync(string chatId);
        Task RecordDeliveryAsync(int signalId, string chatId, int userSignalNumber);
        Task<List<string>> GetDeliveredChatIdsAsync(int signalId);
        Task<int> GetUserSignalNumberAsync(int signalId, string chatId);
        Task ClearUserHistoryAsync(string chatId);
        Task<List<CryptoSense.Application.DTOs.CoinPerformanceBreakdownDto>> GetCoinPerformanceBreakdownAsync(List<string>? monitoredCoins = null);
        Task<bool> HasActiveSignalForSymbolAsync(string symbol);
        Task<FuturesSignal?> GetLastClosedSignalForSymbolAsync(string symbol);
        Task<int> GetNextSequentialSignalNumberAsync();
        Task<int> CommitSignalNumberOnSendSuccessAsync(int signalId);
        Task ResetClosedSignalsAsync();
        Task<int> GetActiveSignalsCountAsync();
        Task ClearAllSignalsAsync();
        Task<int> CleanupOrphanedSignalsAsync();
        Task<DateTime?> GetLastDeliveredSignalTimeUtcAsync(string chatId);
        Task<FuturesSignal?> GetLastTestSignalAsync(string chatId);
        Task DeleteAsync(FuturesSignal signal);
        Task<bool> DeleteUnsentForCandleAsync(string symbol, string timeframe, DateTime sourceCandleOpenTimeUtc, SignalDirection direction);
        Task<bool> DeleteUnsentForCandleAsync(string symbol, string timeframe, DateTime sourceCandleOpenTimeUtc, string direction);
        Task<FuturesSignal?> GetUnsentSignalForCandleAsync(string symbol, string timeframe, DateTime sourceCandleOpenTimeUtc, SignalDirection direction);
        Task<FuturesSignal?> GetUnsentSignalForCandleAsync(string symbol, string timeframe, DateTime sourceCandleOpenTimeUtc, string direction);
        Task<decimal> GetClosedPnlSinceAsync(DateTime sinceUtc);
        Task<List<FuturesSignal>> GetClosedSignalsSinceAsync(DateTime sinceUtc);
    }
}
