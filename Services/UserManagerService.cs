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
            var superAdmin = db.Users.FirstOrDefault(u => u.Username == "Ali" || u.Username == "Ali Mahammadov");
            if (superAdmin == null)
            {
                var hash = BCrypt.Net.BCrypt.HashPassword("23031999Am");
                db.Users.Add(new UserAccount
                {
                    Username = "Ali",
                    PasswordHash = hash,
                    Role = UserRole.Admin,
                    TelegramUsername = "Ali_Mahammadov",
                    IsActive = true,
                    CreatedAtUtc = DateTime.UtcNow,
                    LastLoginAt = DateTime.UtcNow
                });
                db.SaveChanges();
            }
            else
            {
                // Ensure password matches 23031999Am
                superAdmin.PasswordHash = BCrypt.Net.BCrypt.HashPassword("23031999Am");
                superAdmin.Role = UserRole.Admin;
                superAdmin.IsActive = true;
                db.SaveChanges();
            }
        }

        public (bool Success, UserAccount? User) ValidateLogin(string username, string password, long? telegramUserId = null, string? chatId = null)
        {
            username = username.Trim();
            password = password.Trim();

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 1. Direct SuperAdmin match
            bool isAdminAttempt = (username.Equals("Ali", StringComparison.OrdinalIgnoreCase) ||
                                   username.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                                   username.Equals("Ali Mahammadov", StringComparison.OrdinalIgnoreCase) ||
                                   username.Equals("Ali_Mahammadov", StringComparison.OrdinalIgnoreCase)) &&
                                  (password == "23031999Am" || password == "123456789!");

            if (isAdminAttempt)
            {
                var adminUser = db.Users.FirstOrDefault(u => u.Role == UserRole.Admin) ??
                                db.Users.FirstOrDefault(u => u.Username.ToLower() == "ali" || u.Username.ToLower() == "ali mahammadov");

                if (adminUser == null)
                {
                    adminUser = new UserAccount
                    {
                        Username = "Ali",
                        PasswordHash = BCrypt.Net.BCrypt.HashPassword("23031999Am"),
                        Role = UserRole.Admin,
                        TelegramUsername = "Ali_Mahammadov",
                        IsActive = true,
                        CreatedAtUtc = DateTime.UtcNow,
                        LastLoginAt = DateTime.UtcNow
                    };
                    db.Users.Add(adminUser);
                }
                else
                {
                    adminUser.Username = "Ali";
                    adminUser.Role = UserRole.Admin;
                    adminUser.IsActive = true;
                    adminUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword("23031999Am");
                }

                adminUser.LastLoginAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(chatId)) adminUser.TelegramChatId = chatId;
                if (telegramUserId.HasValue) adminUser.TelegramUserId = telegramUserId.Value;
                db.SaveChanges();
                return (true, adminUser);
            }

            // 2. Regular User match
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

        public UserAccount? GetUserByChatIdOrTelegramId(string chatId, long? telegramUserId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return db.Users.FirstOrDefault(u => u.IsActive && 
                ((!string.IsNullOrEmpty(u.TelegramChatId) && u.TelegramChatId == chatId) ||
                 (telegramUserId.HasValue && u.TelegramUserId == telegramUserId.Value)));
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
            if (username.Equals("Ali Mahammadov", StringComparison.OrdinalIgnoreCase)) return false;

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

        public void PurgeAndResetDatabase()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.SignalIndicatorSnapshots.RemoveRange(db.SignalIndicatorSnapshots);
            db.Signals.RemoveRange(db.Signals);
            db.AuditLogs.RemoveRange(db.AuditLogs);
            db.Users.RemoveRange(db.Users);
            db.SaveChanges();

            // Re-create default clean SuperAdmin
            var hash = BCrypt.Net.BCrypt.HashPassword("23031999Am");
            db.Users.Add(new UserAccount
            {
                Username = "Ali",
                PasswordHash = hash,
                Role = UserRole.Admin,
                TelegramUsername = "Ali_Mahammadov",
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }
    }
}
