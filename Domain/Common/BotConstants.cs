namespace CryptoSense.Domain.Common
{
    /// <summary>
    /// Central constants eliminating magic strings across the codebase.
    /// All FSM states, portfolio modes, timeframe labels, and UI keys are defined here.
    /// </summary>
    public static class BotConstants
    {
        // Admin FSM States
        public static class AdminState
        {
            public const string WaitingCreateUser  = "ADMIN_WAITING_CREATE_USER";
            public const string WaitingDeleteUser  = "ADMIN_WAITING_DELETE_USER";
            public const string WaitingResetPassword = "ADMIN_WAITING_RESET_PWD";
        }

        // User FSM States
        public static class UserState
        {
            public const string WaitingAddCustomCoin = "WAITING_ADD_CUSTOM_COIN";
            public const string WaitingCoinInput     = "WAITING_COIN_INPUT";
            public const string WaitingDeleteCoin    = "USER_WAITING_DELETE_COIN";
        }

        // Portfolio Modes
        public static class PortfolioMode
        {
            public const string Standard40 = "Standard40";
            public const string Custom     = "Custom";
            public const string Combined   = "Combined";
            public const string Unset      = "Təyin olunmayıb";
        }

        // Timeframes
        public static class Timeframe
        {
            public const string OneHour  = "1h";
            public const string FourHour = "4h";
            public const string All      = "Hamısı";
            public const string Unset    = "Təyin olunmayıb";

            public static bool IsAll(string? tf)
            {
                if (string.IsNullOrWhiteSpace(tf)) return false;
                var clean = tf.Trim();
                return clean.Equals("Hamısı", System.StringComparison.OrdinalIgnoreCase)
                    || clean.Equals("Hamisi", System.StringComparison.OrdinalIgnoreCase)
                    || clean.Equals("1h, 4h", System.StringComparison.OrdinalIgnoreCase)
                    || clean.Equals("1h,4h", System.StringComparison.OrdinalIgnoreCase)
                    || clean.Equals("1h + 4h", System.StringComparison.OrdinalIgnoreCase)
                    || clean.Equals("1h+4h", System.StringComparison.OrdinalIgnoreCase)
                    || clean.Equals("AllTime", System.StringComparison.OrdinalIgnoreCase)
                    || clean.Equals("Hamısı (Bütün Tarix)", System.StringComparison.OrdinalIgnoreCase);
            }
        }

        // Signal Type Labels
        public static class SignalType
        {
            public const string Waiting          = "GÖZLƏMƏ ⚪";
            public const string NeutralWaiting   = "NEYTRAL (GÖZLƏMƏ) ⚪";
            public const string InsufficientData = "MƏLUMAT AZDIR";
        }

        // Thresholds — single source of truth for all numeric constants
        public static class Thresholds
        {
            /// <summary>Min confluence score for 1h/4h signals</summary>
            public const decimal MinConfluence1h4h        = 75m;
            /// <summary>Min confluence score for 5m signals</summary>
            public const decimal MinConfluence5m          = 80m;
            /// <summary>Min confluence score for 1m/3m signals</summary>
            public const decimal MinConfluence1m3m        = 90m;
            /// <summary>Minimum R:R ratio for signal dispatch</summary>
            public const decimal MinRiskReward            = 2.00m;
            /// <summary>No-signal heartbeat interval (minutes)</summary>
            public const int     HeartbeatIntervalMinutes = 60;
            /// <summary>Max allowable WebSocket DataAge in milliseconds</summary>
            public const long    MaxDataAgeMs             = 3500;
            /// <summary>Max global concurrent open signals</summary>
            public const int     MaxGlobalOpenPositions   = 20;
            /// <summary>Max daily loss threshold percentage before trading halts</summary>
            public const decimal DailyLossThreshold       = -3.0m;
            /// <summary>Min ADX for 1h signals</summary>
            public const decimal MinAdx1h                 = 22m;
            /// <summary>Min ADX for 4h signals</summary>
            public const decimal MinAdx4h                 = 16m;

            public const decimal MinSlAtr = 1.20m;
            public const decimal MaxSlAtr = 3.00m;
            public const decimal SlBufferAtr = 0.20m;
            public const decimal Tp1R = 1.50m;
            public const decimal PartialTp1 = 0.40m;      // close 40% at TP_A
            public const decimal PartialTp2 = 0.50m;      // close half of remaining at TP_B -> 30% original left
            public const decimal TrailAtr = 2.00m;
            public const decimal BeBufferAtr = 0.20m;
            public const decimal ThinBookAtr = 1.70m;
            public const decimal Tp1Weight = 0.40m;
            public const decimal Tp2Weight = 0.60m;
        }

        // Super Admin identifiers — never hardcode inline
        public static class SuperAdmin
        {
            public const long   TelegramUserId = 1219998176L;
            public const string Username       = "Ali";
            public const string TelegramHandle = "Ali_Mahammadov";
            public const string ChatId         = "1219998176";
        }
    }
}