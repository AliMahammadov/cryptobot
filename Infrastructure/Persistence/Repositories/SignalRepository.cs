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
                .Where(s => s.Status == SignalStatus.Open && !s.IsClosed)
                .OrderByDescending(s => s.GeneratedAt)
                .ToListAsync();
        }

        public async Task<List<FuturesSignal>> GetRecentSignalsAsync(int count = 25)
        {
            return await _context.Signals
                .OrderByDescending(s => s.GeneratedAt)
                .Take(count)
                .ToListAsync();
        }

        public async Task<int> GetMaxSignalNumberAsync()
        {
            return await _context.Signals.MaxAsync(s => (int?)s.SignalNumber) ?? 160;
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

        public async Task<PerformanceStats> GetPerformanceStatsAsync()
        {
            var all = await _context.Signals
                .Where(s => s.SignalType.Contains("LONG") || s.SignalType.Contains("SHORT"))
                .ToListAsync();
            var closed = all.Where(s => s.Status != SignalStatus.Open || s.IsClosed).ToList();

            var stats = new PerformanceStats
            {
                TotalSignals = all.Count,
                OpenSignals = all.Count(s => s.Status == SignalStatus.Open && !s.IsClosed),
                SuccessSignals = closed.Count(s => s.Status == SignalStatus.Success),
                FailedSignals = closed.Count(s => s.Status == SignalStatus.Failed),
                NeutralSignals = closed.Count(s => s.Status == SignalStatus.Neutral)
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
    }
}
