using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CryptoSense.Domain.Entities
{
    [Table("UserSignalDeliveries")]
    public class UserSignalDelivery
    {
        [Key]
        public int Id { get; set; }

        public int SignalId { get; set; }

        [Required]
        [MaxLength(50)]
        public string TelegramChatId { get; set; } = "";

        public int UserSignalNumber { get; set; }

        public DateTime DeliveredAtUtc { get; set; } = DateTime.UtcNow;
    }
}
