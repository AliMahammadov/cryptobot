using CryptoSense.Application.DTOs;

namespace CryptoSense.Infrastructure.Telegram
{
    public static class TelegramKeyboards
    {
        public static object BuildTerminalInlineKeyboard(UserSettings settings)
        {
            var toggleText = settings.IsActive ? "🛑 Bildirişləri Dayandır" : "▶️ Bildirişləri Başlat";
            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "⏱ 1h Siqnalları", callback_data = "cb_sig_1h" },
                        new { text = "⏱ 4h Siqnalları", callback_data = "cb_sig_4h" }
                    },
                    new[]
                    {
                        new { text = "🧭 Bitcoin Trend", callback_data = "cb_btc" },
                        new { text = "📊 Canlı Statistika", callback_data = "cb_stats" }
                    },
                    new[]
                    {
                        new { text = "🪙 Coinləri İdarə Et", callback_data = "cb_coins" },
                        new { text = "ℹ️ Sistem Statusu", callback_data = "cb_status" }
                    },
                    new[]
                    {
                        new { text = toggleText, callback_data = "cb_toggle" }
                    }
                }
            };
        }

        public static object BuildBackToTerminalKeyboard()
        {
            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "⬅️ Terminala Qayıt", callback_data = "cb_menu" }
                    }
                }
            };
        }

        public static object BuildCoinsInlineKeyboard()
        {
            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "📋 Standart 40 Coini Seç", callback_data = "cb_coins_40" }
                    },
                    new[]
                    {
                        new { text = "➕ Coin Əlavə Et", callback_data = "cb_coin_add" },
                        new { text = "🗑 Coin Sil", callback_data = "cb_coin_del" }
                    },
                    new[]
                    {
                        new { text = "⬅️ Terminala Qayıt", callback_data = "cb_menu" }
                    }
                }
            };
        }

        public static object BuildUserKeyboard(UserSettings settings, bool isAdmin = false)
        {
            if (isAdmin)
            {
                return new
                {
                    keyboard = new[]
                    {
                        new[] { new { text = "🎛 Əsas Terminal" }, new { text = "👑 Admin Paneli" } },
                        new[] { new { text = "ℹ️ Bot Statusu" }, new { text = "📊 Statistika" } }
                    },
                    resize_keyboard = true,
                    one_time_keyboard = false
                };
            }

            return new
            {
                keyboard = new[]
                {
                    new[] { new { text = "🎛 Əsas Terminal" }, new { text = "ℹ️ Bot Statusu" } }
                },
                resize_keyboard = true,
                one_time_keyboard = false
            };
        }

        public static object BuildCoinSelectionKeyboard()
        {
            return new
            {
                keyboard = new[]
                {
                    new[] { new { text = "📋 Standart 40 Coini Seç" } },
                    new[] { new { text = "➕ Öz coini əlavə et" }, new { text = "🗑 Coin Sil" } },
                    new[] { new { text = "⬅️ Əsas Menyu" } }
                },
                resize_keyboard = true,
                one_time_keyboard = false
            };
        }

        public static object BuildAdminKeyboard()
        {
            return new
            {
                keyboard = new[]
                {
                    new[] { new { text = "➕ İstifadəçi Yarat" }, new { text = "👥 İstifadəçilərin Siyahısı" } },
                    new[] { new { text = "🗑 İstifadəçi Sil" }, new { text = "🔑 Parolu Dəyiş" } },
                    new[] { new { text = "📥 Bazanı Yüklə" }, new { text = "📈 Dərin Statistika" } },
                    new[] { new { text = "🌐 Bütün Coinlərin Siyahısı" }, new { text = "📊 Əsas Menyu (Siqnallar)" } }
                },
                resize_keyboard = true,
                one_time_keyboard = false
            };
        }

        public static object BuildAllSignalsTimeframeKeyboard()
        {
            return new
            {
                keyboard = new[]
                {
                    new[] { new { text = "⏱ 1 Saat (1h) Siqnalları" }, new { text = "⏱ 4 Saat (4h) Siqnalları" } },
                    new[] { new { text = "🌟 Bütün Əsas Zamanlar (1h, 4h)" } },
                    new[] { new { text = "⬅️ Əsas Menyu" } }
                },
                resize_keyboard = true,
                one_time_keyboard = false
            };
        }

        public static object BuildTimeframeKeyboard()
        {
            return new
            {
                keyboard = new[]
                {
                    new[] { new { text = "⏱ 1 Saat (1h)" }, new { text = "⏱ 4 Saat (4h)" } },
                    new[] { new { text = "🌟 Bütün Əsas Zamanlar (1h, 4h)" } },
                    new[] { new { text = "⬅️ Əsas Menyu" } }
                },
                resize_keyboard = true,
                one_time_keyboard = false
            };
        }
    }
}
