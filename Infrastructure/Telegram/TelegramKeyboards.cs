using CryptoSense.Application.DTOs;

namespace CryptoSense.Infrastructure.Telegram
{
    public static class TelegramKeyboards
    {
        public static object BuildUserKeyboard(UserSettings settings)
        {
            var toggleBtn = settings.IsActive ? "🛑 Bildirişləri Dayandır" : "▶️ Bildirişləri Başlat";
            return new
            {
                keyboard = new[]
                {
                    new[] { new { text = "🧭 Bitcoin Kompası" }, new { text = "⚡ Bütün Siqnallar" } },
                    new[] { new { text = "⭐ Mənim Coinlərim" }, new { text = "📊 Statistika" } },
                    new[] { new { text = "⚙️ Coin Seçimi" }, new { text = "🗑 Coin Sil" } },
                    new[] { new { text = "⏱ Zaman Çərçivəsi" }, new { text = "🧹 Siqnalları Sıfırla" } },
                    new[] { new { text = toggleBtn }, new { text = "📰 Bazar Xəbərləri" } }
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
                    new[] { new { text = "🗑 İstifadəçi Sil" }, new { text = "🔑 Parolu Dəyiş" } }
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
                    new[] { new { text = "⏱ 3 Dəqiqə (3m) Siqnalları" }, new { text = "⏱ 5 Dəqiqə (5m) Siqnalları" } },
                    new[] { new { text = "⏱ 15 Dəqiqə (15m) Siqnalları" }, new { text = "⏱ 1 Saat (1h) Siqnalları" } },
                    new[] { new { text = "⏱ 4 Saat (4h) Siqnalları" }, new { text = "🌟 Bütün Zamanlar (Hamısı) Siqnalları" } },
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
                    new[] { new { text = "⏱ 3 Dəqiqə (3m)" }, new { text = "⏱ 5 Dəqiqə (5m)" } },
                    new[] { new { text = "⏱ 15 Dəqiqə (15m)" }, new { text = "⏱ 1 Saat (1h)" } },
                    new[] { new { text = "⏱ 4 Saat (4h)" }, new { text = "🌟 Bütün Zamanlar (Hamısı)" } },
                    new[] { new { text = "⬅️ Əsas Menyu" } }
                },
                resize_keyboard = true,
                one_time_keyboard = false
            };
        }
    }
}
