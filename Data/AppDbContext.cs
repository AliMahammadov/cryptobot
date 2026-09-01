using CryptoSense.Models;
using Microsoft.EntityFrameworkCore;

namespace CryptoSense.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<UserAccount> Users => Set<UserAccount>();
        public DbSet<FuturesSignal> Signals => Set<FuturesSignal>();
        public DbSet<SignalIndicatorSnapshot> SignalIndicatorSnapshots => Set<SignalIndicatorSnapshot>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<UserAccount>(entity =>
            {
                entity.HasIndex(u => u.Username).IsUnique();
                entity.HasIndex(u => u.TelegramUserId).IsUnique();
            });

            modelBuilder.Entity<FuturesSignal>(entity =>
            {
                // Section 4 & 6: Unique constraint for deduplication on symbol, timeframe, and candle open time
                entity.HasIndex(s => new { s.Symbol, s.Timeframe, s.SourceCandleOpenTimeUtc }).IsUnique();
                entity.HasIndex(s => s.SignalNumber);
                entity.HasIndex(s => s.Status);
                entity.HasIndex(s => s.GeneratedAt);
            });

            modelBuilder.Entity<SignalIndicatorSnapshot>(entity =>
            {
                entity.HasIndex(i => i.SignalId);
            });
        }
    }
}
