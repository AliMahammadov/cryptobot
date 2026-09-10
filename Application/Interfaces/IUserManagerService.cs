using System.Collections.Generic;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;

namespace CryptoSense.Application.Interfaces
{
    public interface IUserManagerService
    {
        Task<(bool Success, UserAccount? User)> ValidateLoginAsync(string username, string password, long? telegramUserId = null, string? chatId = null, string? telegramUsername = null);
        Task<UserAccount?> GetUserByChatIdOrTelegramIdAsync(string chatId, long? telegramUserId);
        Task ClearChatBindingAsync(string chatId, long? telegramUserId);
        Task LogoutAsync(string chatId, long? telegramUserId = null);
        Task<bool> CreateUserAsync(string username, string password, UserRole role = UserRole.User, int? adminUserId = null);
        Task<bool> DeleteUserAsync(string username, int? adminUserId = null);
        Task<bool> ResetPasswordAsync(string username, string newPassword, int? adminUserId = null);
        Task<List<UserAccount>> GetAllUsersAsync();
        Task<List<UserAccount>> GetAllLoggedInActiveUsersAsync();
        void PurgeAndResetDatabase();
    }
}
