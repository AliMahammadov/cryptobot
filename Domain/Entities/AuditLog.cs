using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CryptoSense.Domain.Entities
{
    [Table("AuditLogs")]
    public class AuditLog
    {
        [Key]
        public int Id { get; set; }
        
        public int? AdminUserId { get; set; }
        
        [Required]
        [MaxLength(50)]
        public string Action { get; set; } = "";
        
        [MaxLength(100)]
        public string TargetUsername { get; set; } = "";
        
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
