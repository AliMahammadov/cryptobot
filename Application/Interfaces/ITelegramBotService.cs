using System.Threading.Tasks;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Application.Interfaces
{
    public interface ITelegramBotService
    {
        Task<bool> SendMessageAsync(string message, string targetChatId, object? replyMarkup = null);
        Task<bool> DeleteMessageAsync(string chatId, long messageId);
        Task SendSignalAlertAsync(FuturesSignal signal, string? specificChatId = null);
        Task SendOutcomeAlertAsync(FuturesSignal signal, string outcomeType, decimal hitPrice, decimal profitPct);
        Task NotifySuperAdminUserLoginAsync(string username, string platform);
        Task RevokeUserSessionAsync(string username);
    }
}
