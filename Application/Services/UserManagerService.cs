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

        private void InitializeSuperAdmin()
        {
            try
            {
                _unitOfWork.EnsureDatabaseCreated();
                var superAdmin = _unitOfWork.Users.GetByUsernameAsync("Ali").GetAwaiter().GetResult() ??
                                 _unitOfWork.Users.GetByUsernameAsync("Ali Mahammadov").GetAwaiter().GetResult();

                var hash = BCrypt.Net.BCrypt.HashPassword("23031999Am");

                if (superAdmin == null)
                {
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
                else
                {
                    superAdmin.PasswordHash = hash;
                    superAdmin.Role = UserRole.Admin;
                    superAdmin.IsActive = true;
                    _unitOfWork.Users.UpdateAsync(superAdmin).GetAwaiter().GetResult();
                    _unitOfWork.SaveChangesAsync().GetAwaiter().GetResult();
                }
            }
            catch
            {
            }
        }

        public async Task<(bool Success, UserAccount? User)> ValidateLoginAsync(string username, string password, long? telegramUserId = null, string? chatId = null)
        {
            username = username.Trim();
            password = password.Trim();

            // 1. Direct SuperAdmin match
            bool isAdminAttempt = (username.Equals("Ali", StringComparison.OrdinalIgnoreCase) ||
                                   username.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                                   username.Equals("Ali Mahammadov", StringComparison.OrdinalIgnoreCase) ||
                                   username.Equals("Ali_Mahammadov", StringComparison.OrdinalIgnoreCase)) &&
                                  (password == "23031999Am" || password == "123456789!");

            if (isAdminAttempt)
            {
                var adminUser = await _unitOfWork.Users.GetByUsernameAsync("Ali") ??
                                await _unitOfWork.Users.GetByUsernameAsync("Ali Mahammadov");

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
                if (!string.IsNullOrEmpty(chatId)) adminUser.TelegramChatId = chatId;
                if (telegramUserId.HasValue) adminUser.TelegramUserId = telegramUserId.Value;

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
                user.LastLoginAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(chatId)) user.TelegramChatId = chatId;
                if (telegramUserId.HasValue) user.TelegramUserId = telegramUserId.Value;

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
                if ((!string.IsNullOrEmpty(u.TelegramChatId) && u.TelegramChatId == chatId) ||
                    (telegramUserId.HasValue && u.TelegramUserId == telegramUserId.Value))
                {
                    u.TelegramChatId = "";
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
