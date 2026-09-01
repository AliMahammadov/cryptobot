using System;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoSense.Domain.Interfaces
{
    public interface IUnitOfWork : IDisposable
    {
        IUserRepository Users { get; }
        ISignalRepository Signals { get; }
        IAuditLogRepository AuditLogs { get; }
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
        void EnsureDatabaseCreated();
        void PurgeAndResetDatabase();
    }
}
