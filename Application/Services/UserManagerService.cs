using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;

namespace CryptoSense.Application.Services
{
    public class UserManagerService : IUserManagerService
    {
        private readonly IUnitOfWork _unitOfWork;

        public UserManagerService(IUnitOfWork unitOfWork)
        {
            _unitOfWork = unitOfWork;
            InitializeSuperAdmin();
        }

        private static bool _superAdminInitialized = false;
        private static readonly object _initLock = new();

        private void InitializeSuperAdmin()
        {
            if (_superAdminInitialized) return;
            lock (_initLock)
            {
                if (_superAdminInitialized) return;
                try
                {
                    _unitOfWork.EnsureDatabaseCreated();
                    var superAdmin = _unitOfWork.Users.GetByUsernameAsync("Ali").GetAwaiter().GetResult() ??
                                     _unitOfWork.Users.GetByUsernameAsync("Ali Mahammadov").GetAwaiter().GetResult();

                    if (superAdmin == null)
                    {
                        var hash = BCrypt.Net.BCrypt.HashPassword("23031999Am");
                        var newAdmin = new UserAccount
                        {
                            Username = "Ali",
                            PasswordHash = hash,
                            Role = UserRole.Admin,
                            TelegramUsername = "Ali_Mahammadov",
                            TelegramUserId = 1219998176,
                            TelegramChatId = "1219998176",
                            IsActive = true,
                            CreatedAtUtc = DateTime.UtcNow,
                            LastLoginAt = DateTime.UtcNow
                        };
                        _unitOfWork.Users.AddAsync(newAdmin).GetAwaiter().GetResult();
                        _unitOfWork.SaveChangesAsync().GetAwaiter().GetResult();
                    }
                    _superAdminInitialized = true;
                }
                catch
                {
                }
            }
        }

        public async Task<(bool Success, UserAccount? User)> ValidateLoginAsync(string username, string password, long? telegramUserId = null, string? chatId = null, string? telegramUsername = null)
        {
            username = username.Trim();
            password = password.Trim();

            // 1. Direct SuperAdmin match
            // 1. Super Admin shortcut or database lookup
            if (username.Equals("Ali", StringComparison.OrdinalIgnoreCase))
            {
                if (password != "23031999Am")
                {
                    return (false, null);
                }

                var adminUser = await _unitOfWork.Users.GetByUsernameAsync("Ali");
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
                    await _unitOfWork.Users.AddAsync(adminUser);
                }
                else
                {
                    adminUser.Username = "Ali";
                    adminUser.Role = UserRole.Admin;
                    adminUser.IsActive = true;
                    adminUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword("23031999Am");
                }

                adminUser.LastLoginAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(chatId))
                {
                    adminUser.TelegramChatId = chatId;
                }
                else if (string.IsNullOrEmpty(adminUser.TelegramChatId))
                {
                    adminUser.TelegramChatId = "1219998176";
                }

                if (telegramUserId.HasValue && telegramUserId.Value > 0)
                {
                    adminUser.TelegramUserId = telegramUserId.Value;
                }
                else if (!adminUser.TelegramUserId.HasValue || adminUser.TelegramUserId <= 0)
                {
                    adminUser.TelegramUserId = 1219998176;
                }

                if (!string.IsNullOrEmpty(telegramUsername))
                {
                    adminUser.TelegramUsername = telegramUsername;
                }
                else if (string.IsNullOrEmpty(adminUser.TelegramUsername))
                {
                    adminUser.TelegramUsername = "Ali_Mahammadov";
                }

                await _unitOfWork.Users.UpdateAsync(adminUser);
                await _unitOfWork.SaveChangesAsync();
                return (true, adminUser);
            }

            // 2. Regular User match
            var user = await _unitOfWork.Users.GetByUsernameAsync(username);
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
                else if (user.PasswordHash == password)
                {
                    user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
                    valid = true;
                }
            }
            catch
            {
                if (user.PasswordHash == password)
                {
                    user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
                    valid = true;
                }
            }

            if (valid)
            {
                user.LastLoginAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(chatId)) user.TelegramChatId = chatId;
                if (telegramUserId.HasValue && telegramUserId.Value > 0) user.TelegramUserId = telegramUserId.Value;
                if (!string.IsNullOrEmpty(telegramUsername)) user.TelegramUsername = telegramUsername;

                await _unitOfWork.Users.UpdateAsync(user);
                await _unitOfWork.SaveChangesAsync();
                return (true, user);
            }

            return (false, null);
        }

        public async Task<UserAccount?> GetUserByChatIdOrTelegramIdAsync(string chatId, long? telegramUserId)
        {
            return await _unitOfWork.Users.GetByChatIdOrTelegramUserIdAsync(chatId, telegramUserId);
        }

        public async Task ClearChatBindingAsync(string chatId, long? telegramUserId)
        {
            var user = await _unitOfWork.Users.GetByChatIdOrTelegramUserIdAsync(chatId, telegramUserId);
            if (user != null)
            {
                user.TelegramChatId = "";
                user.TelegramUserId = null;
                await _unitOfWork.Users.UpdateAsync(user);
                await _unitOfWork.SaveChangesAsync();
            }
        }

        public async Task<bool> CreateUserAsync(string username, string password, UserRole role = UserRole.User, int? adminUserId = null)
        {
            username = username.Trim();
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;

            if (await _unitOfWork.Users.ExistsByUsernameAsync(username)) return false;

            var newUser = new UserAccount
            {
                Username = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                Role = role,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            };

            await _unitOfWork.Users.AddAsync(newUser);
            await _unitOfWork.AuditLogs.AddAsync(new AuditLog
            {
                AdminUserId = adminUserId,
                Action = "UserCreated",
                TargetUsername = username,
                CreatedAtUtc = DateTime.UtcNow
            });

            await _unitOfWork.SaveChangesAsync();
            Console.WriteLine($"[UserManager] User '{username}' successfully created and saved to database.");
            return true;
        }

        public async Task<bool> DeleteUserAsync(string username, int? adminUserId = null)
        {
            username = username.Trim();
            if (username.Equals("Ali", StringComparison.OrdinalIgnoreCase) || 
                username.Equals("Ali Mahammadov", StringComparison.OrdinalIgnoreCase) ||
                username.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return false;

            var user = await _unitOfWork.Users.GetByUsernameAsync(username);
            if (user == null) return false;

            user.IsActive = false; // Soft-delete
            await _unitOfWork.Users.UpdateAsync(user);
            await _unitOfWork.AuditLogs.AddAsync(new AuditLog
            {
                AdminUserId = adminUserId,
                Action = "UserDeleted",
                TargetUsername = username,
                CreatedAtUtc = DateTime.UtcNow
            });

            await _unitOfWork.SaveChangesAsync();
            return true;
        }

        public async Task<bool> ResetPasswordAsync(string username, string newPassword, int? adminUserId = null)
        {
            username = username.Trim();
            var user = await _unitOfWork.Users.GetByUsernameAsync(username);
            if (user == null) return false;

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            await _unitOfWork.Users.UpdateAsync(user);
            await _unitOfWork.AuditLogs.AddAsync(new AuditLog
            {
                AdminUserId = adminUserId,
                Action = "PasswordReset",
                TargetUsername = username,
                CreatedAtUtc = DateTime.UtcNow
            });

            await _unitOfWork.SaveChangesAsync();
            return true;
        }

        public async Task<List<UserAccount>> GetAllUsersAsync()
        {
            return await _unitOfWork.Users.GetAllUsersAsync();
        }

        public void PurgeAndResetDatabase()
        {
            _unitOfWork.PurgeAndResetDatabase();
        }
    }
}
