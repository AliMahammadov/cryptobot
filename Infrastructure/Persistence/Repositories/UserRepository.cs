using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CryptoSense.Infrastructure.Persistence.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly AppDbContext _context;

        public UserRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<UserAccount?> GetByIdAsync(int id)
        {
            return await _context.Users.FindAsync(id);
        }

        public async Task<UserAccount?> GetByUsernameAsync(string username)
        {
            var lower = username.ToLower();
            return await _context.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == lower);
        }

        public async Task<UserAccount?> GetByChatIdOrTelegramUserIdAsync(string chatId, long? telegramUserId)
        {
            if (!string.IsNullOrEmpty(chatId))
            {
                var userByChat = await _context.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId);
                if (userByChat != null) return userByChat;
            }

            if (telegramUserId.HasValue && telegramUserId.Value > 0)
            {
                return await _context.Users.FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId.Value);
            }

            return null;
        }

        public async Task<List<UserAccount>> GetAllActiveUsersAsync()
        {
            return await _context.Users
                .Where(u => u.IsActive)
                .OrderByDescending(u => u.Role == UserRole.Admin)
                .ThenBy(u => u.Username)
                .ToListAsync();
        }

        public async Task<List<UserAccount>> GetAllLoggedInActiveUsersAsync()
        {
            return await _context.Users
                .Where(u => u.IsActive && u.IsLoggedIn && !string.IsNullOrEmpty(u.TelegramChatId))
                .OrderByDescending(u => u.Role == UserRole.Admin)
                .ThenBy(u => u.Username)
                .ToListAsync();
        }

        public async Task<List<UserAccount>> GetAllUsersAsync()
        {
            return await _context.Users
                .OrderByDescending(u => u.Role == UserRole.Admin)
                .ThenBy(u => u.Username)
                .ToListAsync();
        }

        public async Task AddAsync(UserAccount user)
        {
            await _context.Users.AddAsync(user);
        }

        public Task UpdateAsync(UserAccount user)
        {
            _context.Users.Update(user);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(UserAccount user)
        {
            user.IsActive = false;
            _context.Users.Update(user);
            return Task.CompletedTask;
        }

        public async Task<bool> ExistsByUsernameAsync(string username)
        {
            var lower = username.ToLower();
            return await _context.Users.AnyAsync(u => u.Username.ToLower() == lower);
        }
    }
}
