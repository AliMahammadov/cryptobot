using System.Threading.Tasks;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Application.Interfaces
{
    public interface ITelegramBotService
    {
        Task<bool> SendMessageAsync(string message, string targetChatId, object? replyMarkup = null);
        Task<long?> SendMessageReturnIdAsync(string message, string targetChatId, object? replyMarkup = null);
        Task<bool> EditMessageTextAsync(string chatId, long messageId, string text, object? replyMarkup = null);
        Task<bool> AnswerCallbackQueryAsync(string callbackQueryId, string? text = null);
        Task<bool> DeleteMessageAsync(string chatId, long messageId);
        Task<bool> SendSignalAlertAsync(FuturesSignal signal, string? specificChatId = null);
        Task SendOutcomeAlertAsync(FuturesSignal signal, string outcomeType, decimal hitPrice, decimal profitPct);
        Task SendVolatilityRiskAlertAsync(string symbol, decimal currentPrice, decimal priceChange24h, decimal volatilityRatio, string reason);
        Task SendUrgentNewsAlertAsync(CryptoNewsItem newsItem, bool isListing = false);
        Task NotifySuperAdminUserLoginAsync(string username, string platform);
        Task RevokeUserSessionAsync(string username);
        Task BroadcastSystemAlertAsync(string message);
        Task SendCircuitBreakerAlertAsync(string message, IEnumerable<int> lossSignalIds);
        Task SendDailyReportAsync();
        Task<bool> SendDocumentAsync(string filePath, string targetChatId, string caption = "");
        Task<bool> CanReceivePushAsync(string chatId);
        Task MirrorToChannelIfUserbotAsync(string? username, string sourceChatId, string messageText);
        string GetEffectiveUsername(string chatId, string? fallback = null);
    }
}
