using System.Collections.Generic;
using System.Threading.Tasks;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Domain.Interfaces
{
    public interface IUserRepository
    {
        Task<UserAccount?> GetByIdAsync(int id);
        Task<UserAccount?> GetByUsernameAsync(string username);
        Task<UserAccount?> GetByChatIdOrTelegramUserIdAsync(string chatId, long? telegramUserId);
        Task<List<UserAccount>> GetAllActiveUsersAsync();
        Task<List<UserAccount>> GetAllLoggedInActiveUsersAsync();
        Task<List<UserAccount>> GetAllUsersAsync();
        Task AddAsync(UserAccount user);
        Task UpdateAsync(UserAccount user);
        Task DeleteAsync(UserAccount user);
        Task<bool> ExistsByUsernameAsync(string username);
    }
}
