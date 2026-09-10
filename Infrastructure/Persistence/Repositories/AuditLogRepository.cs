using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CryptoSense.Infrastructure.Persistence.Repositories
{
    public class AuditLogRepository : IAuditLogRepository
    {
        private readonly AppDbContext _context;

        public AuditLogRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task AddAsync(AuditLog log)
        {
            await _context.AuditLogs.AddAsync(log);
        }

        public async Task<List<AuditLog>> GetRecentLogsAsync(int count = 50)
        {
            return await _context.AuditLogs
                .OrderByDescending(a => a.CreatedAtUtc)
                .Take(count)
                .ToListAsync();
        }

        public async Task<bool> HasDailyReportBeenSentAsync(string dateKey)
        {
            return await _context.AuditLogs
                .AnyAsync(a => a.Action == "DAILY_REPORT_SENT" && a.TargetUsername == dateKey);
        }

        public async Task RecordDailyReportSentAsync(string dateKey)
        {
            var exists = await _context.AuditLogs
                .AnyAsync(a => a.Action == "DAILY_REPORT_SENT" && a.TargetUsername == dateKey);
            if (!exists)
            {
                await _context.AuditLogs.AddAsync(new AuditLog
                {
                    Action = "DAILY_REPORT_SENT",
                    TargetUsername = dateKey,
                    CreatedAtUtc = System.DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
            }
        }
    }
}
