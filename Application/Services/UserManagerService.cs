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

                // Unbind any other user previously bound to this specific Telegram chat
                if (!string.IsNullOrEmpty(chatId))
                {
                    var existingUsers = await _unitOfWork.Users.GetAllActiveUsersAsync();
                    foreach (var u in existingUsers)
                    {
                        if (u.Id != adminUser.Id && u.TelegramChatId == chatId)
                        {
                            u.TelegramChatId = "";
                            u.TelegramUserId = null;
                            await _unitOfWork.Users.UpdateAsync(u);
                        }
                    }
                }

                adminUser.LastLoginAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(chatId)) adminUser.TelegramChatId = chatId;
                if (telegramUserId.HasValue && telegramUserId.Value > 0) adminUser.TelegramUserId = telegramUserId.Value;
                if (!string.IsNullOrEmpty(telegramUsername)) adminUser.TelegramUsername = telegramUsername;

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
            }
            catch
            {
            }

            if (valid)
            {
                // Unbind any other user previously bound to this specific Telegram chat
                if (!string.IsNullOrEmpty(chatId))
                {
                    var existingUsers = await _unitOfWork.Users.GetAllActiveUsersAsync();
                    foreach (var u in existingUsers)
                    {
                        if (u.Id != user.Id && u.TelegramChatId == chatId)
                        {
                            u.TelegramChatId = "";
                            u.TelegramUserId = null;
                            await _unitOfWork.Users.UpdateAsync(u);
                        }
                    }
                }

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
            var users = await _unitOfWork.Users.GetAllActiveUsersAsync();
            foreach (var u in users)
            {
                bool modified = false;
                if (!string.IsNullOrEmpty(u.TelegramChatId) && u.TelegramChatId == chatId)
                {
                    u.TelegramChatId = "";
                    modified = true;
                }
                if (telegramUserId.HasValue && telegramUserId.Value > 0 && u.TelegramUserId == telegramUserId.Value)
                {
                    u.TelegramUserId = null;
                    modified = true;
                }
                if (modified)
                {
                    await _unitOfWork.Users.UpdateAsync(u);
                }
            }
            await _unitOfWork.SaveChangesAsync();
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
            return await _unitOfWork.Users.GetAllActiveUsersAsync();
        }

        public void PurgeAndResetDatabase()
        {
            _unitOfWork.PurgeAndResetDatabase();
        }
    }
}
