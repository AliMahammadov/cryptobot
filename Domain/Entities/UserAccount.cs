using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CryptoSense.Domain.Enums;

namespace CryptoSense.Domain.Entities
{
    [Table("Users")]
    public class UserAccount
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(50)]
        public string Username { get; set; } = "";
        
        [Required]
        [MaxLength(255)]
        public string PasswordHash { get; set; } = "";
        
        [NotMapped]
        public string Password { get; set; } = "";
        
        public UserRole Role { get; set; } = UserRole.User;
        
        [MaxLength(100)]
        public string TelegramUsername { get; set; } = "";
        
        [MaxLength(50)]
        public string TelegramChatId { get; set; } = "";
        
        public long? TelegramUserId { get; set; }
        
        public bool IsActive { get; set; } = true;
        public bool IsLoggedIn { get; set; } = false;
        
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;
    }
}
