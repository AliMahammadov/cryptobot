using CryptoSense.Application.DTOs;

namespace CryptoSense.Infrastructure.Telegram
{
    public static class TelegramKeyboards
    {
        public static object BuildUserKeyboard(UserSettings settings, bool isAdmin = false)
        {
            var toggleBtn = settings.IsActive ? "🛑 Bildirişləri Dayandır" : "▶️ Bildirişləri Başlat";

            if (isAdmin)
            {
                return new
                {
                    keyboard = new[]
                    {
                        new[] { new { text = "👑 Admin Paneli" }, new { text = "🧭 Bitcoin Kompası" } },
                        new[] { new { text = "⭐ Mənim Coinlərim" }, new { text = "⚙️ Coin Seçimi" } },
                        new[] { new { text = "🗑 Coin Sil" }, new { text = "ℹ️ Bot Statusu" } },
                        new[] { new { text = "📊 Statistika" }, new { text = "📈 Dərin Statistika" } },
                        new[] { new { text = "📰 Bazar Xəbərləri" }, new { text = "🧹 Siqnalları Sıfırla" } },
                        new[] { new { text = toggleBtn } }
                    },
                    resize_keyboard = true,
                    one_time_keyboard = false
                };
            }

            return new
            {
                keyboard = new[]
                {
                    new[] { new { text = "⭐ Mənim Coinlərim" }, new { text = "🧭 Bitcoin Kompası" } },
                    new[] { new { text = "⚙️ Coin Seçimi" }, new { text = "🗑 Coin Sil" } },
                    new[] { new { text = "📊 Statistika" }, new { text = "ℹ️ Bot Statusu" } },
                    new[] { new { text = "📰 Bazar Xəbərləri" }, new { text = "🧹 Siqnalları Sıfırla" } },
                    new[] { new { text = toggleBtn } }
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
                    new[] { new { text = "⏱ 15 Dəqiqə (15m) Siqnalları" } },
                    new[] { new { text = "🌟 Bütün Əsas Zamanlar (15m, 1h, 4h)" } },
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
                    new[] { new { text = "⏱ 15 Dəqiqə (15m)" } },
                    new[] { new { text = "🌟 Bütün Əsas Zamanlar (15m, 1h, 4h)" } },
                    new[] { new { text = "⬅️ Əsas Menyu" } }
                },
                resize_keyboard = true,
                one_time_keyboard = false
            };
        }
    }
}
