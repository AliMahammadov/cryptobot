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
        Task<PerformanceStats> GetPerformanceStatsAsync(string? specificTimeframe = null, List<string>? userCoins = null);
        Task<int> GetUserTodaySignalsCountAsync(string chatId);
        Task<int> GetUserOpenSignalsCountAsync(string chatId);
        Task<PerformanceStats> GetUserPerformanceStatsAsync(string chatId, string? specificTimeframe = null, List<string>? userCoins = null);
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
    }
}
