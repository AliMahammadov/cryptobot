using System;
using System.Collections.Generic;
using System.Linq;
using CryptoSense.Data;
using CryptoSense.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoSense.Services
{
    public class UserManagerService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly object _lock = new();

        public UserManagerService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();

            // Ensure SuperAdmin exists
            var superAdmin = db.Users.FirstOrDefault(u => u.Username == "Ali Muhammadov" || u.Username == "alimahammadov");
            if (superAdmin == null)
            {
                var hash = BCrypt.Net.BCrypt.HashPassword("123456789!");
                db.Users.Add(new UserAccount
                {
                    Username = "Ali Muhammadov",
                    PasswordHash = hash,
                    Role = UserRole.Admin,
                    TelegramUsername = "alimahammadov",
                    TelegramChatId = "1219998176",
                    TelegramUserId = 1219998176,
                    IsActive = true,
                    CreatedAtUtc = DateTime.UtcNow,
                    LastLoginAt = DateTime.UtcNow
                });
                db.SaveChanges();
            }
        }

        public (bool Success, UserAccount? User) ValidateLogin(string username, string password, long? telegramUserId = null, string? chatId = null)
        {
            username = username.Trim();
            if (username.Equals("Eli Mehemmedov", StringComparison.OrdinalIgnoreCase)) username = "Ali Muhammadov";

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var user = db.Users.FirstOrDefault(u => u.Username.ToLower() == username.ToLower());
            if (user == null || !user.IsActive)
            {
                return (false, null);
            }

            bool valid = false;
            try
            {
                if (BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                {
                    valid = true;
                }
            }
            catch
            {
                // Fallback for legacy plain password or admin override
                if (user.PasswordHash == password || password == "123456789!" || password == "2026")
                {
                    valid = true;
                    // Migrate to BCrypt
                    user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
                }
            }

            if (valid)
            {
                user.LastLoginAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(chatId)) user.TelegramChatId = chatId;
                if (telegramUserId.HasValue) user.TelegramUserId = telegramUserId.Value;
                db.SaveChanges();
                return (true, user);
            }

            return (false, null);
        }

        public bool CreateUser(string username, string password, UserRole role = UserRole.User, int? adminUserId = null)
        {
            username = username.Trim();
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (db.Users.Any(u => u.Username.ToLower() == username.ToLower())) return false;

            var newUser = new UserAccount
            {
                Username = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                Role = role,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            };

            db.Users.Add(newUser);
            db.AuditLogs.Add(new AuditLog
            {
                AdminUserId = adminUserId,
                Action = "UserCreated",
                TargetUsername = username,
                CreatedAtUtc = DateTime.UtcNow
            });

            db.SaveChanges();
            return true;
        }

        public bool DeleteUser(string username, int? adminUserId = null)
        {
            username = username.Trim();
            if (username.Equals("Ali Muhammadov", StringComparison.OrdinalIgnoreCase)) return false;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var user = db.Users.FirstOrDefault(u => u.Username.ToLower() == username.ToLower());
            if (user == null) return false;

            user.IsActive = false; // Soft-delete
            db.AuditLogs.Add(new AuditLog
            {
                AdminUserId = adminUserId,
                Action = "UserDeleted",
                TargetUsername = username,
                CreatedAtUtc = DateTime.UtcNow
            });

            db.SaveChanges();
            return true;
        }

        public bool ResetPassword(string username, string newPassword, int? adminUserId = null)
        {
            username = username.Trim();
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var user = db.Users.FirstOrDefault(u => u.Username.ToLower() == username.ToLower());
            if (user == null) return false;

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            db.AuditLogs.Add(new AuditLog
            {
                AdminUserId = adminUserId,
                Action = "PasswordReset",
                TargetUsername = username,
                CreatedAtUtc = DateTime.UtcNow
            });

            db.SaveChanges();
            return true;
        }

        public List<UserAccount> GetAllUsers()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return db.Users.Where(u => u.IsActive).OrderByDescending(u => u.Role == UserRole.Admin).ThenBy(u => u.Username).ToList();
        }
    }
}
