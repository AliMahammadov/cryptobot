using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CryptoSense.Domain.Entities
{
    [Table("TelegramLoginBlocks")]
    public class TelegramLoginBlock
    {
        [Key]
        public int Id { get; set; }

        public long TelegramUserId { get; set; }

        [MaxLength(100)]
        public string? TelegramChatId { get; set; }

        [MaxLength(100)]
        public string? TelegramUsername { get; set; }

        public int FailedAttemptCount { get; set; } = 0;

        public bool IsBlocked { get; set; } = false;

        [MaxLength(100)]
        public string? LastAttemptUsername { get; set; }

        public DateTime LastAttemptAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime? BlockedAtUtc { get; set; }

        public DateTime? UnblockedAtUtc { get; set; }
    }
}
