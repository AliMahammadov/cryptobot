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
        Task<int> GetMaxSignalNumberAsync();
        Task AddAsync(FuturesSignal signal);
        Task UpdateAsync(FuturesSignal signal);
        Task<PerformanceStats> GetPerformanceStatsAsync(string? specificTimeframe = null, List<string>? userCoins = null);
        Task<List<CryptoSense.Application.DTOs.CoinPerformanceBreakdownDto>> GetCoinPerformanceBreakdownAsync(List<string>? monitoredCoins = null);
        Task ClearAllSignalsAsync();
    }
}
