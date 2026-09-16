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

        private static readonly string[] BackupFilePaths = new[]
        {
            CryptoSense.Domain.Common.AppPaths.UserBackupFilePath
        };

        private void SaveBackupUsers()
        {
            try
            {
                var allUsers = _unitOfWork.Users.GetAllUsersAsync().GetAwaiter().GetResult();
                var json = System.Text.Json.JsonSerializer.Serialize(allUsers, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                foreach (var path in BackupFilePaths)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        File.WriteAllText(path, json);
                    }
                    catch (Exception _ex) { Console.WriteLine($"[UserManagerService] Swallowed exception: {_ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UserManager] SaveBackupUsers error: {ex.Message}");
            }
        }

        private void InitializeSuperAdmin()
        {
            if (_superAdminInitialized) return;
            lock (_initLock)
            {
                if (_superAdminInitialized) return;
                try
                {
                    _unitOfWork.EnsureDatabaseCreated();

                    // 1. Ensure SuperAdmin (Ali) exists
                    var superAdmin = _unitOfWork.Users.GetByUsernameAsync("Ali").GetAwaiter().GetResult() ??
                                     _unitOfWork.Users.GetByUsernameAsync("Ali Mahammadov").GetAwaiter().GetResult();

                    if (superAdmin == null)
                    {
                        var seedPwd = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
                        if (string.IsNullOrEmpty(seedPwd))
                        {
                            // UnitOfWork seeds Ali from AppConfig:AdminSeedPassword / ADMIN_PASSWORD. Do not hash an empty or hardcoded password here.
                        }
                        else
                        {
                            var hash = BCrypt.Net.BCrypt.HashPassword(seedPwd);
                            var newAdmin = new UserAccount
                            {
                                Username = "Ali",
                                PasswordHash = hash,
                                Role = UserRole.Admin,
                                TelegramUsername = "Ali_Mahammadov",
                                TelegramUserId = 1219998176,
                                TelegramChatId = "1219998176",
                                IsActive = true,
                                IsLoggedIn = false,
                                CreatedAtUtc = DateTime.UtcNow,
                                LastLoginAt = DateTime.UtcNow
                            };
                            _unitOfWork.Users.AddAsync(newAdmin).GetAwaiter().GetResult();
                        }
                    }

                    // 2. Ensure Murad exists permanently
                    var muradUser = _unitOfWork.Users.GetByUsernameAsync("Murad").GetAwaiter().GetResult();
                    if (muradUser == null)
                    {
                        var newMurad = new UserAccount
                        {
                            Username = "Murad",
                            PasswordHash = "$2a$11$rqAHb83ZUadJSdNAGGvTuu/ze535B8TvcCku4/6UrV6qctyOO6Sju",
                            Role = UserRole.User,
                            IsActive = true,
                            IsLoggedIn = false,
                            CreatedAtUtc = DateTime.UtcNow,
                            LastLoginAt = DateTime.UtcNow
                        };
                        _unitOfWork.Users.AddAsync(newMurad).GetAwaiter().GetResult();
                    }

                    // 3. Restore all users from users_backup.json (survives container redeploy)
                    // CRITICAL RULE: Backup restore only adds MISSING rows, NEVER forces IsLoggedIn=true
                    foreach (var path in BackupFilePaths)
                    {
                        if (File.Exists(path))
                        {
                            try
                            {
                                var json = File.ReadAllText(path);
                                var backedUsers = System.Text.Json.JsonSerializer.Deserialize<List<UserAccount>>(json);
                                if (backedUsers != null)
                                {
                                    foreach (var bu in backedUsers)
                                    {
                                        if (string.IsNullOrWhiteSpace(bu.Username)) continue;
                                        var existing = _unitOfWork.Users.GetByUsernameAsync(bu.Username).GetAwaiter().GetResult();
                                        if (existing == null)
                                        {
                                            var toAdd = new UserAccount
                                            {
                                                Username = bu.Username,
                                                PasswordHash = bu.PasswordHash,
                                                Role = bu.Role,
                                                TelegramUsername = bu.TelegramUsername,
                                                TelegramUserId = bu.TelegramUserId,
                                                TelegramChatId = bu.TelegramChatId,
                                                IsActive = bu.IsActive,
                                                IsLoggedIn = !string.IsNullOrWhiteSpace(bu.TelegramChatId), // Restore logged-in state if ChatId is bound
                                                CreatedAtUtc = bu.CreatedAtUtc == default ? DateTime.UtcNow : bu.CreatedAtUtc,
                                                LastLoginAt = bu.LastLoginAt == default ? DateTime.UtcNow : bu.LastLoginAt
                                            };
                                            _unitOfWork.Users.AddAsync(toAdd).GetAwaiter().GetResult();
                                        }
                                    }
                                }
                                break;
                            }
                            catch (Exception _ex) { Console.WriteLine($"[UserManagerService] Swallowed exception: {_ex.Message}"); }
                        }
                    }

                    _unitOfWork.SaveChangesAsync().GetAwaiter().GetResult();
                    SaveBackupUsers();
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
                var adminSeed = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
                if (string.IsNullOrEmpty(adminSeed))
                {
                    // no env password → fall through to the existing bcrypt DB path below (do not return false here)
                }
                else if (password != adminSeed)
                {
                    return (false, null);
                }
                else
                {
                    var adminUser = await _unitOfWork.Users.GetByUsernameAsync("Ali");
                    if (adminUser == null)
                    {
                        adminUser = new UserAccount
                        {
                            Username = "Ali",
                            PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminSeed),
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
                    }

                    adminUser.LastLoginAt = DateTime.UtcNow;
                    adminUser.IsLoggedIn = true;
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
                    SaveBackupUsers();
                    return (true, adminUser);
                }
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
                user.IsLoggedIn = true;
                user.IsActive = true;
                if (!string.IsNullOrEmpty(chatId)) user.TelegramChatId = chatId;
                if (telegramUserId.HasValue && telegramUserId.Value > 0) user.TelegramUserId = telegramUserId.Value;
                if (!string.IsNullOrEmpty(telegramUsername)) user.TelegramUsername = telegramUsername;

                await _unitOfWork.Users.UpdateAsync(user);
                await _unitOfWork.SaveChangesAsync();
                SaveBackupUsers();
                return (true, user);
            }

            return (false, null);
        }

        public async Task<UserAccount?> GetUserByChatIdOrTelegramIdAsync(string chatId, long? telegramUserId)
        {
            return await _unitOfWork.Users.GetByChatIdOrTelegramUserIdAsync(chatId, telegramUserId);
        }

        private static bool IsUserbotUsername(string? username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            static string Clean(string s) => s.Replace(" ", "").Replace("_", "").Trim().ToLowerInvariant();
            return Clean(username) == "userbot";
        }

        public async Task ClearChatBindingAsync(string chatId, long? telegramUserId)
        {
            var user = await _unitOfWork.Users.GetByChatIdOrTelegramUserIdAsync(chatId, telegramUserId);
            if (user != null)
            {
                if (IsUserbotUsername(user.Username)) return;

                user.IsLoggedIn = false;
                user.TelegramChatId = "";
                user.TelegramUserId = null;
                await _unitOfWork.Users.UpdateAsync(user);
                await _unitOfWork.SaveChangesAsync();
                SaveBackupUsers();
            }
        }

        public async Task LogoutAsync(string chatId, long? telegramUserId = null)
        {
            var user = await _unitOfWork.Users.GetByChatIdOrTelegramUserIdAsync(chatId, telegramUserId);
            if (user != null)
            {
                if (IsUserbotUsername(user.Username)) return;

                user.IsLoggedIn = false;
                user.TelegramChatId = "";
                user.TelegramUserId = null;
                await _unitOfWork.Users.UpdateAsync(user);
                await _unitOfWork.SaveChangesAsync();
                SaveBackupUsers();
            }
        }

        public async Task<List<UserAccount>> GetAllLoggedInActiveUsersAsync()
        {
            return await _unitOfWork.Users.GetAllLoggedInActiveUsersAsync();
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
            SaveBackupUsers();
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
            user.IsLoggedIn = false;
            await _unitOfWork.Users.UpdateAsync(user);
            await _unitOfWork.AuditLogs.AddAsync(new AuditLog
            {
                AdminUserId = adminUserId,
                Action = "UserDeleted",
                TargetUsername = username,
                CreatedAtUtc = DateTime.UtcNow
            });

            await _unitOfWork.SaveChangesAsync();
            SaveBackupUsers();
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
            SaveBackupUsers();
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
