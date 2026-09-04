using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CryptoSense.Infrastructure.Persistence.Repositories
{
    public class SignalRepository : ISignalRepository
    {
        private readonly AppDbContext _context;

        public SignalRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<FuturesSignal?> GetByIdAsync(int id)
        {
            return await _context.Signals
                .Include(s => s.IndicatorSnapshots)
                .FirstOrDefaultAsync(s => s.Id == id);
        }

        public async Task<FuturesSignal?> GetExistingCandleSignalAsync(string symbol, string timeframe, DateTime sourceCandleOpenTimeUtc)
        {
            return await _context.Signals
                .Include(s => s.IndicatorSnapshots)
                .FirstOrDefaultAsync(s => s.Symbol == symbol && s.Timeframe == timeframe && s.SourceCandleOpenTimeUtc == sourceCandleOpenTimeUtc);
        }

        public async Task<List<FuturesSignal>> GetOpenTrackedSignalsAsync()
        {
            return await _context.Signals
                .Where(s => s.Status == SignalStatus.Open && !s.IsClosed && s.SignalAlertSent)
                .OrderByDescending(s => s.GeneratedAt)
                .ToListAsync();
        }

        public async Task<List<FuturesSignal>> GetRecentSignalsAsync(int count = 25)
        {
            return await _context.Signals
                .Where(s => s.SignalAlertSent)
                .OrderByDescending(s => s.GeneratedAt)
                .Take(count)
                .ToListAsync();
        }

        public async Task<int> GetMaxSignalNumberAsync()
        {
            return await _context.Signals.MaxAsync(s => (int?)s.SignalNumber) ?? 0;
        }

        public async Task AddAsync(FuturesSignal signal)
        {
            await _context.Signals.AddAsync(signal);
        }

        public Task UpdateAsync(FuturesSignal signal)
        {
            _context.Signals.Update(signal);
            return Task.CompletedTask;
        }

        public async Task ClearAllSignalsAsync()
        {
            _context.SignalIndicatorSnapshots.RemoveRange(_context.SignalIndicatorSnapshots);
            _context.Signals.RemoveRange(_context.Signals);
            await _context.SaveChangesAsync();
        }

        public async Task<PerformanceStats> GetPerformanceStatsAsync(string? specificTimeframe = null, List<string>? userCoins = null)
        {
            var query = _context.Signals
                .Where(s => (s.SignalType.Contains("LONG") || s.SignalType.Contains("SHORT")) && s.SignalAlertSent);

            if (!string.IsNullOrEmpty(specificTimeframe) && specificTimeframe != "Hamısı" && specificTimeframe != "Hamisi")
            {
                query = query.Where(s => s.Timeframe == specificTimeframe);
            }

            if (userCoins != null && userCoins.Count > 0)
            {
                query = query.Where(s => userCoins.Contains(s.Symbol));
            }

            var all = await query.ToListAsync();
            var closed = all.Where(s => s.Status != SignalStatus.Open || s.IsClosed).ToList();

            var stats = new PerformanceStats
            {
                TotalSignals = all.Count,
                OpenSignals = all.Count(s => s.Status == SignalStatus.Open && !s.IsClosed),
                SuccessSignals = closed.Count(s => s.Status == SignalStatus.Success),
                FailedSignals = closed.Count(s => s.Status == SignalStatus.Failed || s.Status == SignalStatus.Neutral),
                NeutralSignals = 0
            };

            int decisiveTrades = stats.SuccessSignals + stats.FailedSignals;
            stats.WinRatePercent = decisiveTrades > 0 ? Math.Round(((decimal)stats.SuccessSignals / decisiveTrades) * 100, 1) : 0;

            var results = closed.Where(s => s.ResultPercent.HasValue).Select(s => s.ResultPercent!.Value).ToList();
            if (results.Count > 0)
            {
                stats.TotalNetProfitPercent = Math.Round(results.Sum(), 2);
                stats.AvgProfitPerTradePercent = Math.Round(results.Average(), 2);
                stats.BestTradePercent = Math.Round(results.Max(), 2);
                stats.WorstTradePercent = Math.Round(results.Min(), 2);
            }

            return stats;
        }

        public async Task<List<CryptoSense.Application.DTOs.CoinPerformanceBreakdownDto>> GetCoinPerformanceBreakdownAsync(List<string>? monitoredCoins = null)
        {
            var signals = await _context.Signals
                .Where(s => (s.SignalType.Contains("LONG") || s.SignalType.Contains("SHORT")) && s.SignalAlertSent)
                .ToListAsync();

            var closedSignals = signals.Where(s => s.IsClosed || s.Status != SignalStatus.Open).ToList();
            var openSignals = signals.Where(s => !s.IsClosed && s.Status == SignalStatus.Open).ToList();
            
            static string Norm(string sym)
            {
                var c = sym.Replace("USDT", "");
                if (c.StartsWith("1000")) c = c.Substring(4);
                return c;
            }

            var groupedByCoin = closedSignals
                .GroupBy(s => Norm(s.Symbol))
                .ToDictionary(g => g.Key, g => g.ToList());

            var groupedOpenByCoin = openSignals
                .GroupBy(s => Norm(s.Symbol))
                .ToDictionary(g => g.Key, g => g.ToList());

            var allNormCoins = new HashSet<string>(groupedByCoin.Keys);
            foreach (var k in groupedOpenByCoin.Keys) allNormCoins.Add(k);
            if (monitoredCoins != null)
            {
                foreach (var c in monitoredCoins) allNormCoins.Add(Norm(c));
            }

            var result = new List<CryptoSense.Application.DTOs.CoinPerformanceBreakdownDto>();

            foreach (var normCoin in allNormCoins.OrderBy(c => c))
            {
                var dto = new CryptoSense.Application.DTOs.CoinPerformanceBreakdownDto
                {
                    Symbol = normCoin + "USDT",
                    ActiveTrades = groupedOpenByCoin.TryGetValue(normCoin, out var openList) ? openList.Count : 0
                };

                if (groupedByCoin.TryGetValue(normCoin, out var list) && list.Count > 0)
                {
                    dto.TotalTrades = list.Count;
                    dto.SuccessTrades = list.Count(s => s.Status == SignalStatus.Success);
                    dto.FailedTrades = list.Count(s => s.Status == SignalStatus.Failed || s.Status == SignalStatus.Neutral);
                    int decisive = dto.SuccessTrades + dto.FailedTrades;
                    dto.OverallWinRate = decisive > 0 ? Math.Round(((decimal)dto.SuccessTrades / decisive) * 100, 1) : 0;
                    dto.TotalNetProfitPercent = Math.Round(list.Where(s => s.ResultPercent.HasValue).Sum(s => s.ResultPercent!.Value), 2);

                    var tfGroups = list.GroupBy(s => s.Timeframe);
                    foreach (var tfGroup in tfGroups)
                    {
                        var tfTotal = tfGroup.Count();
                        var tfSuccess = tfGroup.Count(s => s.Status == SignalStatus.Success);
                        var tfFailed = tfGroup.Count(s => s.Status == SignalStatus.Failed || s.Status == SignalStatus.Neutral);
                        int tfDecisive = tfSuccess + tfFailed;
                        var tfWinRate = tfDecisive > 0 ? Math.Round(((decimal)tfSuccess / tfDecisive) * 100, 1) : 0;
                        var tfPnL = Math.Round(tfGroup.Where(s => s.ResultPercent.HasValue).Sum(s => s.ResultPercent!.Value), 2);

                        dto.TimeframeStats[tfGroup.Key] = new CryptoSense.Application.DTOs.TimeframeStatsDto
                        {
                            Timeframe = tfGroup.Key,
                            TotalTrades = tfTotal,
                            SuccessTrades = tfSuccess,
                            FailedTrades = tfFailed,
                            WinRate = tfWinRate,
                            NetProfitPercent = tfPnL
                        };
                    }
                }

                result.Add(dto);
            }

            return result;
        }
    }
}
