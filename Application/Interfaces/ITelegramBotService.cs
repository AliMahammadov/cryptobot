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
        Task SendVolatilityRiskAlertAsync(string symbol, decimal currentPrice, decimal priceChange24h, decimal volatilityRatio, string reason);
        Task SendUrgentNewsAlertAsync(CryptoNewsItem newsItem, bool isListing = false);
        Task NotifySuperAdminUserLoginAsync(string username, string platform);
        Task RevokeUserSessionAsync(string username);
        Task BroadcastSystemAlertAsync(string message);
        Task SendDailyReportAsync();
    }
}
