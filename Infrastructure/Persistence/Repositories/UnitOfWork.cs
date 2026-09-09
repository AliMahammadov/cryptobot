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

            // Enable WAL mode and busy timeout for high concurrency without locks
            try { _context.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;"); } catch { }
            try { _context.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 5000;"); } catch { }
            try { _context.Database.ExecuteSqlRaw("PRAGMA synchronous = NORMAL;"); } catch { }

            // Safe auto-migration for newly added Partial Close columns and CloseReason
            try { _context.Database.ExecuteSqlRaw("ALTER TABLE Signals ADD COLUMN RealizedProfitPercent TEXT NOT NULL DEFAULT '0';"); } catch { }
            try { _context.Database.ExecuteSqlRaw("ALTER TABLE Signals ADD COLUMN RemainingPositionRatio TEXT NOT NULL DEFAULT '1.0';"); } catch { }
            try { _context.Database.ExecuteSqlRaw("ALTER TABLE Signals ADD COLUMN IsPartial1Closed INTEGER NOT NULL DEFAULT 0;"); } catch { }
            try { _context.Database.ExecuteSqlRaw("ALTER TABLE Signals ADD COLUMN IsPartial2Closed INTEGER NOT NULL DEFAULT 0;"); } catch { }
            try { _context.Database.ExecuteSqlRaw("ALTER TABLE Signals ADD COLUMN CloseReason TEXT NULL;"); } catch { }

            // Data cleanup: fix any corrupt signals where Status was Success but ResultPercent was negative or TakeProfit3 was 0
            try { _context.Database.ExecuteSqlRaw("UPDATE Signals SET Status = 2 WHERE Status = 1 AND (ResultPercent < 0 OR (TakeProfit3 <= 0 AND CloseReason = 'TP3'));"); } catch { }

            // Safe SuperAdmin initialization & validation: Always guarantee TelegramChatId and TelegramUserId are populated
            var adminUser = _context.Users.FirstOrDefault(u => u.Username == "Ali" || u.Role == UserRole.Admin);
            if (adminUser == null)
            {
                var hash = BCrypt.Net.BCrypt.HashPassword("23031999Am");
                _context.Users.Add(new UserAccount
                {
                    Username = "Ali",
                    PasswordHash = hash,
                    Role = UserRole.Admin,
                    TelegramUsername = "Ali_Mahammadov",
                    TelegramUserId = 1219998176,
                    TelegramChatId = "1219998176",
                    IsActive = true,
                    CreatedAtUtc = System.DateTime.UtcNow,
                    LastLoginAt = System.DateTime.UtcNow
                });
                _context.SaveChanges();
            }
            else
            {
                bool modified = false;
                if (string.IsNullOrEmpty(adminUser.TelegramChatId))
                {
                    adminUser.TelegramChatId = "1219998176";
                    modified = true;
                }
                if (!adminUser.TelegramUserId.HasValue || adminUser.TelegramUserId <= 0)
                {
                    adminUser.TelegramUserId = 1219998176;
                    modified = true;
                }
                if (string.IsNullOrEmpty(adminUser.TelegramUsername))
                {
                    adminUser.TelegramUsername = "Ali_Mahammadov";
                    modified = true;
                }
                if (modified)
                {
                    _context.SaveChanges();
                }
            }

            // Ensure all passwords in Users table are encrypted with BCrypt (never plaintext)
            try
            {
                var unhashedUsers = _context.Users.Where(u => !u.PasswordHash.StartsWith("$2")).ToList();
                if (unhashedUsers.Count > 0)
                {
                    foreach (var u in unhashedUsers)
                    {
                        u.PasswordHash = BCrypt.Net.BCrypt.HashPassword(u.PasswordHash);
                    }
                    _context.SaveChanges();
                }
            }
            catch { }
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
                TelegramChatId = "1219998176",
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
