using System.Collections.Generic;

namespace CryptoSense.Application.DTOs
{
    public class AppConfig
    {
        public string SuperAdminTelegram { get; set; } = "@Ali_Mahammadov";
        /// <summary>Set via SUPER_ADMIN_CHAT_ID env variable in production.</summary>
        public string SuperAdminChatId { get; set; } = "1219998176";
        public long SuperAdminUserId { get; set; } = 1219998176;
        /// <summary>Set via TELEGRAM_BOT_TOKEN env variable. Never commit a live token here.</summary>
        public string TelegramBotToken { get; set; } = "";
        /// <summary>Initial admin password — set via ADMIN_PASSWORD env variable. Falls back to empty (safe).</summary>
        public string AdminSeedPassword { get; set; } = "";
        public bool AutoScanEnabled { get; set; } = true;
        /// <summary>Minimum confluence score for 1h/4h signals (75%).</summary>
        public int MinConfidenceThreshold { get; set; } = 75;
        public List<string> SelectedCoins { get; set; } = new();
        public bool AlertAllCoins { get; set; } = true;
        public string DefaultTimeframe { get; set; } = "1h";
    }

    public class UserSettings
    {
        public bool IsActive { get; set; } = true;
        public string Timeframe { get; set; } = "1h";
        public string PortfolioMode { get; set; } = "Standard40"; // "Standard40", "Custom", "Combined"
        public List<string> Coins { get; set; } = new(); // Currently active monitored coins
        public List<string> CustomCoins { get; set; } = new(); // User's private custom-added coins
        public long? LastTerminalMessageId { get; set; }
        public bool IsTerminalOpen { get; set; } = false;
        public long? LastAdminMessageId { get; set; }
        public bool IsAdminOpen { get; set; } = false;
        public System.DateTime LastResumeTime { get; set; } = System.DateTime.UtcNow;
        public System.DateTime LastSignalSentUtc { get; set; } = System.DateTime.UtcNow;
        public System.DateTime LastHeartbeatSentUtc { get; set; } = System.DateTime.UtcNow;
        public long? LastHeartbeatMessageId { get; set; }
        public long? LastPortfolioSummaryMessageId { get; set; }
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
