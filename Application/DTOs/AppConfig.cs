using System.Collections.Generic;

namespace CryptoSense.Application.DTOs
{
    public class AppConfig
    {
        public string SuperAdminTelegram { get; set; } = "@Ali_Mahammadov";
        public string SuperAdminChatId { get; set; } = "1219998176";
        public long SuperAdminUserId { get; set; } = 1219998176;
        public string TelegramBotToken { get; set; } = "8671151605:AAHHPojQbGiSKtJUkeQQCnITfTHInQ3tX5U";
        public bool AutoScanEnabled { get; set; } = true;
        public int MinConfidenceThreshold { get; set; } = 78;
        public List<string> SelectedCoins { get; set; } = new();
        public bool AlertAllCoins { get; set; } = true;
        public string DefaultTimeframe { get; set; } = "3m";
    }

    public class UserSettings
    {
        public bool IsActive { get; set; } = true;
        public string Timeframe { get; set; } = "3m";
        public List<string> Coins { get; set; } = new(); // Starts completely EMPTY so user selects their own coins
        public System.DateTime LastResumeTime { get; set; } = System.DateTime.UtcNow;
        public System.DateTime LastSignalSentUtc { get; set; } = System.DateTime.UtcNow;
        public System.DateTime LastHeartbeatSentUtc { get; set; } = System.DateTime.UtcNow;
        public string Username { get; set; } = "";
        public long? TelegramUserId { get; set; }
        public int AlertCounter { get; set; } = 0;
    }

    public class LoginRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public class CreateUserRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
