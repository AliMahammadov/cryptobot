using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CryptoSense.Infrastructure.Persistence.Repositories
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly AppDbContext _context;
        private IUserRepository? _users;
        private ISignalRepository? _signals;
        private IAuditLogRepository? _auditLogs;

        public UnitOfWork(AppDbContext context)
        {
            _context = context;
        }

        public IUserRepository Users => _users ??= new UserRepository(_context);
        public ISignalRepository Signals => _signals ??= new SignalRepository(_context);
        public IAuditLogRepository AuditLogs => _auditLogs ??= new AuditLogRepository(_context);

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return await _context.SaveChangesAsync(cancellationToken);
        }

        public void EnsureDatabaseCreated()
        {
            _context.Database.EnsureCreated();

            // Safe SuperAdmin initialization: Only add SuperAdmin if missing. NEVER delete or touch existing users!
            bool adminExists = _context.Users.Any(u => u.Username == "Ali" || u.Role == UserRole.Admin);
            if (!adminExists)
            {
                var hash = BCrypt.Net.BCrypt.HashPassword("23031999Am");
                _context.Users.Add(new UserAccount
                {
                    Username = "Ali",
                    PasswordHash = hash,
                    Role = UserRole.Admin,
                    TelegramUsername = "Ali_Mahammadov",
                    TelegramUserId = 1219998176,
                    IsActive = true,
                    CreatedAtUtc = System.DateTime.UtcNow,
                    LastLoginAt = System.DateTime.UtcNow
                });
                _context.SaveChanges();
            }
        }

        public void PurgeAndResetDatabase()
        {
            _context.SignalIndicatorSnapshots.RemoveRange(_context.SignalIndicatorSnapshots);
            _context.Signals.RemoveRange(_context.Signals);
            _context.AuditLogs.RemoveRange(_context.AuditLogs);
            _context.Users.RemoveRange(_context.Users);
            _context.SaveChanges();

            // Re-create default clean SuperAdmin
            var hash = BCrypt.Net.BCrypt.HashPassword("23031999Am");
            _context.Users.Add(new UserAccount
            {
                Username = "Ali",
                PasswordHash = hash,
                Role = UserRole.Admin,
                TelegramUsername = "Ali_Mahammadov",
                TelegramUserId = 1219998176,
                IsActive = true,
                CreatedAtUtc = System.DateTime.UtcNow,
                LastLoginAt = System.DateTime.UtcNow
            });
            _context.SaveChanges();
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}
