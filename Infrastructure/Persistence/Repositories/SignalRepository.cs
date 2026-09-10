using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using CryptoSense.Infrastructure.Telegram;
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

        public async Task<bool> HasActiveSignalForSymbolAsync(string symbol)
        {
            return await _context.Signals
                .AnyAsync(s => s.Symbol == symbol && s.Status == SignalStatus.Open && !s.IsClosed && s.SignalAlertSent);
        }

        public async Task<int> GetActiveSignalsCountAsync()
        {
            return await _context.Signals
                .CountAsync(s => s.Status == SignalStatus.Open && !s.IsClosed && s.SignalAlertSent);
        }

        public async Task<List<FuturesSignal>> GetRecentSignalsAsync(int count = 25)
        {
            return await _context.Signals
                .Where(s => s.SignalAlertSent)
                .OrderByDescending(s => s.GeneratedAt)
                .Take(count)
                .ToListAsync();
        }

        public async Task<List<FuturesSignal>> GetSignalsSinceAsync(DateTime sinceUtc)
        {
            return await _context.Signals
                .Where(s => s.GeneratedAt >= sinceUtc && s.SignalAlertSent)
                .OrderByDescending(s => s.GeneratedAt)
                .ToListAsync();
        }

        public async Task<FuturesSignal?> GetLastClosedSignalForSymbolAsync(string symbol)
        {
            return await _context.Signals
                .Where(s => s.Symbol == symbol && (s.IsClosed || s.Status != SignalStatus.Open) && s.ClosedAt != null)
                .OrderByDescending(s => s.ClosedAt)
                .FirstOrDefaultAsync();
        }

        public async Task<int> GetNextSequentialSignalNumberAsync()
        {
            var maxSent = await _context.Signals
                .Where(s => s.SignalAlertSent && s.SignalNumber > 0 && !s.Symbol.StartsWith("TESTCOIN"))
                .MaxAsync(s => (int?)s.SignalNumber) ?? 0;
            return maxSent + 1;
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

        public async Task<int> CommitSignalNumberOnSendSuccessAsync(int signalId)
        {
            // BƏND 1: Bir INTEGER, bir DB lock (BEGIN IMMEDIATE).
            // Nömrə YALNIZ sendMessage=true-dan SONRA +1.
            var conn = _context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "BEGIN IMMEDIATE";
            try { await cmd.ExecuteNonQueryAsync(); } catch { }

            try
            {
                var sig = await _context.Signals.FirstOrDefaultAsync(s => s.Id == signalId);
                if (sig == null)
                {
                    cmd.CommandText = "ROLLBACK";
                    try { await cmd.ExecuteNonQueryAsync(); } catch { }
                    return 0;
                }

                if (sig.SignalAlertSent && sig.SignalNumber > 0)
                {
                    cmd.CommandText = "COMMIT";
                    try { await cmd.ExecuteNonQueryAsync(); } catch { }
                    return sig.SignalNumber;
                }

                cmd.CommandText = "SELECT COALESCE(MAX(SignalNumber), 0) FROM Signals WHERE SignalAlertSent = 1 AND SignalNumber > 0 AND NOT Symbol LIKE 'TESTCOIN%'";
                var res = await cmd.ExecuteScalarAsync();
                int maxSent = (res != null && res != DBNull.Value) ? Convert.ToInt32(res) : 0;
                int nextNum = maxSent + 1;

                sig.SignalNumber = nextNum;
                sig.SignalAlertSent = true;
                await _context.SaveChangesAsync();

                cmd.CommandText = "COMMIT";
                try { await cmd.ExecuteNonQueryAsync(); } catch { }
                return nextNum;
            }
            catch (Exception ex)
            {
                cmd.CommandText = "ROLLBACK";
                try { await cmd.ExecuteNonQueryAsync(); } catch { }
                Console.WriteLine($"[SignalRepository] CommitSignalNumber error: {ex.Message}");
                throw;
            }
        }

        public async Task ResetClosedSignalsAsync()
        {
            // BƏND 6: 🧹 Sıfırla (SuperAdmin): statistika + BAĞLI = 0. Açıq mövqeyə toxunma.
            var closedSignals = await _context.Signals
                .Where(s => s.IsClosed || s.Status != SignalStatus.Open)
                .ToListAsync();

            if (closedSignals.Count > 0)
            {
                var closedIds = closedSignals.Select(s => s.Id).ToList();
                var snaps = await _context.SignalIndicatorSnapshots
                    .Where(s => closedIds.Contains(s.SignalId))
                    .ToListAsync();
                _context.SignalIndicatorSnapshots.RemoveRange(snaps);

                var deliveries = await _context.UserSignalDeliveries
                    .Where(d => closedIds.Contains(d.SignalId))
                    .ToListAsync();
                _context.UserSignalDeliveries.RemoveRange(deliveries);

                _context.Signals.RemoveRange(closedSignals);
                await _context.SaveChangesAsync();
            }
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
                .Where(s => (s.SignalType.Contains("LONG") || s.SignalType.Contains("SHORT")) && (s.SignalAlertSent || s.IsClosed || s.Status != SignalStatus.Open));

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

            var beCount = closed.Count(s => s.CloseReason == "BE" || s.Status == SignalStatus.Neutral || (s.OutcomeStatus != null && s.OutcomeStatus.Contains("Breakeven")) || (s.ResultPercent.HasValue && Math.Abs(s.ResultPercent.Value) < 0.20m));
            var successCount = closed.Count(s => s.CloseReason != "BE" && s.Status == SignalStatus.Success && (s.ResultPercent == null || s.ResultPercent >= 0.20m));
            var failedCount = closed.Count(s => s.CloseReason != "BE" && (s.Status == SignalStatus.Failed || (s.Status == SignalStatus.Success && s.ResultPercent < -0.20m)));

            var stats = new PerformanceStats
            {
                TotalSignals = all.Count,
                OpenSignals = all.Count(s => s.Status == SignalStatus.Open && !s.IsClosed),
                SuccessSignals = successCount,
                FailedSignals = failedCount,
                NeutralSignals = beCount,
                BreakevenHitsCount = beCount
            };

            int decisiveTrades = stats.SuccessSignals + stats.FailedSignals;
            stats.WinRatePercent = decisiveTrades > 0 ? Math.Round(((decimal)stats.SuccessSignals / decisiveTrades) * 100, 1) : (stats.SuccessSignals > 0 ? 100m : 0m);

            var results = closed.Where(s => s.ResultPercent.HasValue).Select(s => s.ResultPercent!.Value).ToList();
            if (results.Count > 0)
            {
                stats.TotalNetProfitPercent = Math.Round(results.Sum(), 2);
                stats.AvgProfitPerTradePercent = Math.Round(results.Average(), 2);
                stats.BestTradePercent = Math.Round(results.Max(), 2);
                stats.WorstTradePercent = Math.Round(results.Min(), 2);

                // Prioritet 5: Profit Factor & Expectancy
                var grossProfit = results.Where(r => r > 0).Sum();
                var grossLoss = Math.Abs(results.Where(r => r < 0).Sum());
                stats.ProfitFactor = grossLoss > 0 ? Math.Round(grossProfit / grossLoss, 2) : (grossProfit > 0 ? 9.99m : 0m);

                var wins = results.Where(r => r > 0).ToList();
                var losses = results.Where(r => r < 0).ToList();
                decimal avgWin = wins.Count > 0 ? wins.Average() : 0m;
                decimal avgLoss = losses.Count > 0 ? Math.Abs(losses.Average()) : 0m;
                decimal winRate = decisiveTrades > 0 ? (decimal)stats.SuccessSignals / decisiveTrades : 0m;
                decimal lossRate = decisiveTrades > 0 ? (decimal)stats.FailedSignals / decisiveTrades : 0m;

                if (avgLoss > 0)
                {
                    stats.ExpectancyR = Math.Round(((winRate * avgWin) - (lossRate * avgLoss)) / avgLoss, 2);
                }
                else
                {
                    stats.ExpectancyR = Math.Round(winRate * avgWin, 2);
                }

                // Prioritet 5: Max Drawdown
                var orderedTrades = closed.Where(s => s.ResultPercent.HasValue).OrderBy(s => s.GeneratedAt).ToList();
                decimal peakEquity = 0;
                decimal currentEquity = 0;
                decimal maxDrawdown = 0;
                foreach (var trade in orderedTrades)
                {
                    currentEquity += trade.ResultPercent!.Value;
                    if (currentEquity > peakEquity) peakEquity = currentEquity;
                    decimal dd = peakEquity - currentEquity;
                    if (dd > maxDrawdown) maxDrawdown = dd;
                }
                stats.MaxDrawdownPercent = Math.Round(maxDrawdown, 2);
            }

            // Prioritet 5: Hit Rate Breakdown
            stats.Tp3HitsCount = closed.Count(s => s.OutcomeStatus != null && s.OutcomeStatus.Contains("TP3"));
            stats.PartialHitsCount = closed.Count(s => s.IsPartial1Closed || (s.OutcomeStatus != null && (s.OutcomeStatus.Contains("Partial") || s.OutcomeStatus.Contains("TP1") || s.OutcomeStatus.Contains("TP2"))));
            stats.BreakevenHitsCount = closed.Count(s => s.OutcomeStatus != null && s.OutcomeStatus.Contains("Breakeven"));
            stats.TimeExpiredCount = closed.Count(s => s.OutcomeStatus != null && s.OutcomeStatus.Contains("Müddəti"));
            stats.Tp3HitRatePercent = decisiveTrades > 0 ? Math.Round(((decimal)stats.Tp3HitsCount / decisiveTrades) * 100, 1) : 0m;
            stats.TimeExpiredRatePercent = decisiveTrades > 0 ? Math.Round(((decimal)stats.TimeExpiredCount / decisiveTrades) * 100, 1) : 0m;

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
                    dto.SuccessTrades = list.Count(s => s.Status == SignalStatus.Success && (s.ResultPercent == null || s.ResultPercent >= 0));
                    dto.FailedTrades = list.Count(s => s.Status == SignalStatus.Failed || (s.Status == SignalStatus.Success && s.ResultPercent < 0));
                    int decisive = dto.SuccessTrades + dto.FailedTrades;
                    dto.OverallWinRate = decisive > 0 ? Math.Round(((decimal)dto.SuccessTrades / decisive) * 100, 1) : (dto.SuccessTrades > 0 ? 100m : 0m);
                    dto.TotalNetProfitPercent = Math.Round(list.Where(s => s.ResultPercent.HasValue).Sum(s => s.ResultPercent!.Value), 2);

                    var tfGroups = list.GroupBy(s => s.Timeframe);
                    foreach (var tfGroup in tfGroups)
                    {
                        var tfTotal = tfGroup.Count();
                        var tfSuccess = tfGroup.Count(s => s.Status == SignalStatus.Success && (s.ResultPercent == null || s.ResultPercent >= 0));
                        var tfFailed = tfGroup.Count(s => s.Status == SignalStatus.Failed || (s.Status == SignalStatus.Success && s.ResultPercent < 0));
                        int tfDecisive = tfSuccess + tfFailed;
                        var tfWinRate = tfDecisive > 0 ? Math.Round(((decimal)tfSuccess / tfDecisive) * 100, 1) : (tfSuccess > 0 ? 100m : 0m);
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

        public async Task<int> GetUserTodaySignalsCountAsync(string chatId)
        {
            var todayUtc = DateTime.UtcNow.Date;
            return await _context.UserSignalDeliveries
                .CountAsync(d => d.TelegramChatId == chatId && d.DeliveredAtUtc >= todayUtc);
        }

        public async Task<int> GetUserOpenSignalsCountAsync(string chatId)
        {
            var deliveredSignalIds = await _context.UserSignalDeliveries
                .Where(d => d.TelegramChatId == chatId)
                .Select(d => d.SignalId)
                .ToListAsync();

            return await _context.Signals
                .CountAsync(s => deliveredSignalIds.Contains(s.Id) && s.Status == SignalStatus.Open && !s.IsClosed);
        }

        public async Task RecordDeliveryAsync(int signalId, string chatId, int userSignalNumber)
        {
            var exists = await _context.UserSignalDeliveries
                .AnyAsync(d => d.SignalId == signalId && d.TelegramChatId == chatId);
            if (!exists)
            {
                _context.UserSignalDeliveries.Add(new UserSignalDelivery
                {
                    SignalId = signalId,
                    TelegramChatId = chatId,
                    UserSignalNumber = userSignalNumber,
                    DeliveredAtUtc = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
            }
        }

        public async Task<List<string>> GetDeliveredChatIdsAsync(int signalId)
        {
            return await _context.UserSignalDeliveries
                .Where(d => d.SignalId == signalId)
                .Select(d => d.TelegramChatId)
                .ToListAsync();
        }

        public async Task<int> GetUserSignalNumberAsync(int signalId, string chatId)
        {
            var delivery = await _context.UserSignalDeliveries
                .FirstOrDefaultAsync(d => d.SignalId == signalId && d.TelegramChatId == chatId);
            return delivery?.UserSignalNumber ?? 0;
        }

        public Task ClearUserHistoryAsync(string chatId)
        {
            // Reset must NEVER wipe database deliveries or signals!
            // Preserving history ensures statistics read past trades accurately as required.
            return Task.CompletedTask;
        }

        public async Task<List<FuturesSignal>> GetUserOpenSignalsAsync(string chatId)
        {
            var deliveredSignalIds = await _context.UserSignalDeliveries
                .Where(d => d.TelegramChatId == chatId)
                .Select(d => d.SignalId)
                .ToListAsync();

            if (deliveredSignalIds.Count == 0)
            {
                return await _context.Signals
                    .Where(s => s.Status == SignalStatus.Open && !s.IsClosed)
                    .OrderByDescending(s => s.GeneratedAt)
                    .ToListAsync();
            }

            return await _context.Signals
                .Where(s => deliveredSignalIds.Contains(s.Id) && s.Status == SignalStatus.Open && !s.IsClosed)
                .OrderByDescending(s => s.GeneratedAt)
                .ToListAsync();
        }

        public async Task<PerformanceStats> GetUserPerformanceStatsAsync(string chatId, string? specificTimeframe = null, List<string>? userCoins = null)
        {
            if (chatId == "1219998176" || chatId == "SUPERADMIN" || (TelegramBotService.SuperAdminChatId != null && chatId == TelegramBotService.SuperAdminChatId))
            {
                return await GetPerformanceStatsAsync(specificTimeframe);
            }

            var deliveredSignalIds = await _context.UserSignalDeliveries
                .Where(d => d.TelegramChatId == chatId)
                .Select(d => d.SignalId)
                .ToListAsync();

            if (deliveredSignalIds.Count == 0)
            {
                return await GetPerformanceStatsAsync(specificTimeframe, userCoins);
            }

            var query = _context.Signals
                .Where(s => deliveredSignalIds.Contains(s.Id) && (s.SignalType.Contains("LONG") || s.SignalType.Contains("SHORT")));

            if (!string.IsNullOrEmpty(specificTimeframe) && specificTimeframe != "Hamısı" && specificTimeframe != "Hamisi")
            {
                query = query.Where(s => s.Timeframe == specificTimeframe);
            }

            var all = await query.ToListAsync();
            var closed = all.Where(s => s.Status != SignalStatus.Open || s.IsClosed).ToList();

            var beCount = closed.Count(s => s.CloseReason == "BE" || s.Status == SignalStatus.Neutral || (s.OutcomeStatus != null && s.OutcomeStatus.Contains("Breakeven")) || (s.ResultPercent.HasValue && Math.Abs(s.ResultPercent.Value) < 0.20m));
            var successCount = closed.Count(s => s.CloseReason != "BE" && s.Status == SignalStatus.Success && (s.ResultPercent == null || s.ResultPercent >= 0.20m));
            var failedCount = closed.Count(s => s.CloseReason != "BE" && (s.Status == SignalStatus.Failed || (s.Status == SignalStatus.Success && s.ResultPercent < -0.20m)));

            var stats = new PerformanceStats
            {
                TotalSignals = all.Count,
                OpenSignals = all.Count(s => s.Status == SignalStatus.Open && !s.IsClosed),
                SuccessSignals = successCount,
                FailedSignals = failedCount,
                NeutralSignals = beCount,
                BreakevenHitsCount = beCount
            };

            int decisiveTrades = stats.SuccessSignals + stats.FailedSignals;
            stats.WinRatePercent = decisiveTrades > 0 ? Math.Round(((decimal)stats.SuccessSignals / decisiveTrades) * 100, 1) : (stats.SuccessSignals > 0 ? 100m : 0m);

            var results = closed.Where(s => s.ResultPercent.HasValue).Select(s => s.ResultPercent!.Value).ToList();
            if (results.Count > 0)
            {
                stats.TotalNetProfitPercent = Math.Round(results.Sum(), 2);
                stats.AvgProfitPerTradePercent = Math.Round(results.Average(), 2);
                stats.BestTradePercent = Math.Round(results.Max(), 2);
                stats.WorstTradePercent = Math.Round(results.Min(), 2);

                var grossProfit = results.Where(r => r > 0).Sum();
                var grossLoss = Math.Abs(results.Where(r => r < 0).Sum());
                stats.ProfitFactor = grossLoss > 0 ? Math.Round(grossProfit / grossLoss, 2) : (grossProfit > 0 ? 9.99m : 0m);

                var wins = results.Where(r => r > 0).ToList();
                var losses = results.Where(r => r < 0).ToList();
                decimal avgWin = wins.Count > 0 ? wins.Average() : 0m;
                decimal avgLoss = losses.Count > 0 ? Math.Abs(losses.Average()) : 0m;
                decimal winRate = decisiveTrades > 0 ? (decimal)stats.SuccessSignals / decisiveTrades : 0m;
                decimal lossRate = decisiveTrades > 0 ? (decimal)stats.FailedSignals / decisiveTrades : 0m;

                if (avgLoss > 0)
                {
                    stats.ExpectancyR = Math.Round(((winRate * avgWin) - (lossRate * avgLoss)) / avgLoss, 2);
                }
                else
                {
                    stats.ExpectancyR = Math.Round(winRate * avgWin, 2);
                }

                var orderedTrades = closed.Where(s => s.ResultPercent.HasValue).OrderBy(s => s.GeneratedAt).ToList();
                decimal peakEquity = 0;
                decimal currentEquity = 0;
                decimal maxDrawdown = 0;
                foreach (var trade in orderedTrades)
                {
                    currentEquity += trade.ResultPercent!.Value;
                    if (currentEquity > peakEquity) peakEquity = currentEquity;
                    decimal dd = peakEquity - currentEquity;
                    if (dd > maxDrawdown) maxDrawdown = dd;
                }
                stats.MaxDrawdownPercent = Math.Round(maxDrawdown, 2);
            }

            stats.Tp3HitsCount = closed.Count(s => s.OutcomeStatus != null && s.OutcomeStatus.Contains("TP3"));
            stats.PartialHitsCount = closed.Count(s => s.IsPartial1Closed || (s.OutcomeStatus != null && (s.OutcomeStatus.Contains("Partial") || s.OutcomeStatus.Contains("TP1") || s.OutcomeStatus.Contains("TP2"))));
            stats.BreakevenHitsCount = beCount;
            stats.TimeExpiredCount = closed.Count(s => s.OutcomeStatus != null && s.OutcomeStatus.Contains("Müddəti"));
            stats.Tp3HitRatePercent = decisiveTrades > 0 ? Math.Round(((decimal)stats.Tp3HitsCount / decisiveTrades) * 100, 1) : 0m;
            stats.TimeExpiredRatePercent = decisiveTrades > 0 ? Math.Round(((decimal)stats.TimeExpiredCount / decisiveTrades) * 100, 1) : 0m;

            return stats;
        }
    }
}
