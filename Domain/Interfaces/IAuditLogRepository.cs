using System.Collections.Generic;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Domain.Interfaces
{
    public interface IAuditLogRepository
    {
        Task AddAsync(AuditLog log);
        Task<List<AuditLog>> GetRecentLogsAsync(int count = 50);
        Task<bool> HasDailyReportBeenSentAsync(string dateKey);
        Task RecordDailyReportSentAsync(string dateKey);
    }
}
