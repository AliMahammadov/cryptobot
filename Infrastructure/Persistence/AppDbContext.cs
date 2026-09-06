using CryptoSense.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoSense.Infrastructure.Persistence
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<UserAccount> Users => Set<UserAccount>();
        public DbSet<FuturesSignal> Signals => Set<FuturesSignal>();
        public DbSet<UserSignalDelivery> UserSignalDeliveries => Set<UserSignalDelivery>();
        public DbSet<SignalIndicatorSnapshot> SignalIndicatorSnapshots => Set<SignalIndicatorSnapshot>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<UserAccount>(entity =>
            {
                entity.HasIndex(u => u.Username).IsUnique();
                entity.HasIndex(u => u.TelegramUserId);
                entity.HasIndex(u => u.TelegramChatId);
            });

            modelBuilder.Entity<FuturesSignal>(entity =>
            {
                // Unique constraint for deduplication on symbol, timeframe, and candle open time
                entity.HasIndex(s => new { s.Symbol, s.Timeframe, s.SourceCandleOpenTimeUtc }).IsUnique();
                entity.HasIndex(s => s.SignalNumber);
                entity.HasIndex(s => s.Status);
                entity.HasIndex(s => s.GeneratedAt);
            });

            modelBuilder.Entity<UserSignalDelivery>(entity =>
            {
                entity.HasIndex(d => new { d.SignalId, d.TelegramChatId }).IsUnique();
                entity.HasIndex(d => d.TelegramChatId);
                entity.HasIndex(d => d.SignalId);
            });

            modelBuilder.Entity<SignalIndicatorSnapshot>(entity =>
            {
                entity.HasIndex(i => i.SignalId);
                entity.HasOne<FuturesSignal>()
                    .WithMany(s => s.IndicatorSnapshots)
                    .HasForeignKey(i => i.SignalId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
