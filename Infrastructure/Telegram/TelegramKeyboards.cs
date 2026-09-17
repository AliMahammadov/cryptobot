using CryptoSense.Application.DTOs;

namespace CryptoSense.Infrastructure.Telegram
{
    public static class TelegramKeyboards
    {
        public static object BuildTerminalInlineKeyboard(UserSettings settings, bool isTestMode = false, bool isAdmin = false)
        {
            var toggleText = settings.IsActive ? "🛑 Bildirişləri Dayandır" : "▶️ Bildirişləri Başlat";
            var stdLabel = settings.PortfolioMode == "Standard40" ? "🪙 Standart 40 Koin 🟢" : "🪙 Standart 40 Koin";
            var custLabel = settings.PortfolioMode == "Custom" ? "⭐ Seçilmiş Koinlərim 🟢" : 
                           (settings.PortfolioMode == "Combined" ? "🔥 40 + Fərdi Koin 🟢" : "⭐ Seçilmiş Koinlərim");

            var rows = new System.Collections.Generic.List<object[]>
            {
                new object[]
                {
                    new { text = stdLabel, callback_data = "cb_portfolio_std40" },
                    new { text = custLabel, callback_data = "cb_portfolio_custom" }
                },
                new object[]
                {
                    new { text = "🧭 Bitcoin Kompası", callback_data = "cb_btc" },
                    new { text = "📊 Statistika", callback_data = "cb_stats" }
                },
                new object[]
                {
                    new { text = "📰 Xəbərlər", callback_data = "cb_news" },
                    new { text = "🔄 Yenilə", callback_data = "cb_refresh" }
                }
            };

            // "🧹 Siqnalları Sıfırla" is strictly reserved for Admin
            if (isAdmin)
            {
                rows.Add(new object[]
                {
                    new { text = "ℹ️ Sistem Statusu", callback_data = "cb_status" },
                    new { text = "🧹 Siqnalları Sıfırla", callback_data = "cb_reset" }
                });
            }
            else
            {
                rows.Add(new object[]
                {
                    new { text = "ℹ️ Sistem Statusu", callback_data = "cb_status" }
                });
            }

            rows.Add(new object[]
            {
                new { text = toggleText, callback_data = "cb_toggle" }
            });

            rows.Add(new object[]
            {
                new { text = "🔽 Menyunı Bağla", callback_data = "cb_close_terminal" }
            });

            return new
            {
                inline_keyboard = rows.ToArray()
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

        public static object BuildStatsKeyboard(bool isAllTime = false)
        {
            var toggleBtn = isAllTime
                ? new { text = "📅 Yalnız Bugünkü", callback_data = "cb_stats_today" }
                : new { text = "🌐 Hamısı (All-Time)", callback_data = "cb_stats_alltime" };

            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        toggleBtn,
                        new { text = "⬅️ Terminala Qayıt", callback_data = "cb_menu" }
                    }
                }
            };
        }

        public static object BuildPortfolioSummaryKeyboard()
        {
            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "🔄 Yenilə", callback_data = "cb_refresh_portfolio" },
                        new { text = "⬅️ Terminala Qayıt", callback_data = "cb_menu" }
                    }
                }
            };
        }

        public static object BuildAlreadyActiveKeyboard()
        {
            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "📊 Portfelin Canlı Qiymətləri", callback_data = "cb_show_portfolio" },
                        new { text = "⬅️ Əsas Terminal", callback_data = "cb_menu" }
                    }
                }
            };
        }

        public static object BuildStandard40TimeframeKeyboard()
        {
            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "⏱ 1 Saat (1h)", callback_data = "cb_std_tf_1h" },
                        new { text = "⏱ 4 Saat (4h)", callback_data = "cb_std_tf_4h" }
                    },
                    new[]
                    {
                        new { text = "🌟 Hər İkisi (1h + 4h)", callback_data = "cb_std_tf_all" }
                    },
                    new[]
                    {
                        new { text = "⬅️ Terminala Qayıt", callback_data = "cb_menu" }
                    }
                }
            };
        }

        public static object BuildCustomCoinsKeyboard(UserSettings settings)
        {
            var onlyCustomLabel = settings.PortfolioMode == "Custom" ? "🎯 Yalnız Fərdi Coinlər 🟢" : "🎯 Yalnız Fərdi Coinlər";
            var combinedLabel = settings.PortfolioMode == "Combined" ? "🔥 40 + Fərdi Coinlər 🟢" : "🔥 40 + Fərdi Coinlər";

            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = onlyCustomLabel, callback_data = "cb_cust_mode_only" },
                        new { text = combinedLabel, callback_data = "cb_cust_mode_comb" }
                    },
                    new[]
                    {
                        new { text = "➕ Coin Əlavə Et", callback_data = "cb_custom_add" },
                        new { text = "🗑 Coin Sil", callback_data = "cb_custom_del" }
                    },
                    new[]
                    {
                        new { text = "⏱ 1 Saat (1h)", callback_data = "cb_cust_tf_1h" },
                        new { text = "⏱ 4 Saat (4h)", callback_data = "cb_cust_tf_4h" }
                    },
                    new[]
                    {
                        new { text = "🌟 Hər İkisi (1h + 4h)", callback_data = "cb_cust_tf_all" }
                    },
                    new[]
                    {
                        new { text = "⬅️ Terminala Qayıt", callback_data = "cb_menu" }
                    }
                }
            };
        }

        public static object BuildCoinsInlineKeyboard(UserSettings? settings = null)
        {
            return BuildCustomCoinsKeyboard(settings ?? new UserSettings());
        }

        public static object BuildStopConfirmationKeyboard()
        {
            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "✅ Bəli, bildirişləri dayandır", callback_data = "cb_toggle_stop_confirm" }
                    },
                    new[]
                    {
                        new { text = "❌ İmtina", callback_data = "cb_menu" }
                    }
                }
            };
        }

        public static object BuildResetConfirmationKeyboard()
        {
            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "🛑 Bəli, Sistemi Sıfırla", callback_data = "cb_reset_confirm" }
                    },
                    new[]
                    {
                        new { text = "❌ İmtina Et", callback_data = "cb_menu" }
                    }
                }
            };
        }

        public static object BuildAdminTerminalInlineKeyboard()
        {
            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "➕ İstifadəçi Yarat", callback_data = "cb_admin_create_user" },
                        new { text = "👥 İstifadəçilər", callback_data = "cb_admin_list_users" }
                    },
                    new[]
                    {
                        new { text = "🗑 İstifadəçi Sil", callback_data = "cb_admin_del_user" },
                        new { text = "🔑 Parolu Dəyiş", callback_data = "cb_admin_change_pwd" }
                    },
                    new[]
                    {
                        new { text = "📥 Bazanı Yüklə", callback_data = "cb_admin_export_db" },
                        new { text = "📈 Dərin Statistika", callback_data = "cb_admin_stats" }
                    },
                    new[]
                    {
                        new { text = "🚫 Bloklanmış ID-lər", callback_data = "cb_admin_blocked_ids" }
                    },
                    new[]
                    {
                        new { text = "🧹 Test Hesabları Sil", callback_data = "cb_admin_purge_test_users" },
                        new { text = "🧪 Bütün Bildirişləri Test Et", callback_data = "cb_admin_test_all" }
                    },
                    new[]
                    {
                        new { text = "⬅️ Əsas Terminala Qayıt", callback_data = "cb_menu" },
                        new { text = "🔽 Paneli Bağla", callback_data = "cb_close_admin" }
                    }
                }
            };
        }


        public static object BuildBackToAdminKeyboard()
        {
            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "⬅️ Admin Panelinə Qayıt", callback_data = "cb_admin_menu" },
                        new { text = "🎛 Əsas Terminal", callback_data = "cb_menu" }
                    }
                }
            };
        }

        public static object BuildCombinedTimeframeKeyboard()
        {
            return new
            {
                inline_keyboard = new[]
                {
                    new[]
                    {
                        new { text = "⏱ 1 Saat (1h)", callback_data = "cb_comb_tf_1h" },
                        new { text = "⏱ 4 Saat (4h)", callback_data = "cb_comb_tf_4h" }
                    },
                    new[]
                    {
                        new { text = "🌟 Hər İkisi (1h + 4h)", callback_data = "cb_comb_tf_all" }
                    },
                    new[]
                    {
                        new { text = "⬅️ Fərdi Portfelə Qayıt", callback_data = "cb_portfolio_custom" }
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
                        new[] { new { text = "🎛 Əsas Terminal" }, new { text = "👑 Admin Paneli" } }
                    },
                    resize_keyboard = true,
                    one_time_keyboard = false
                };
            }

            return new
            {
                keyboard = new[]
                {
                    new[] { new { text = "🎛 Əsas Terminal" } }
                },
                resize_keyboard = true,
                one_time_keyboard = false
            };
        }

        public static object BuildCoinSelectionKeyboard() => BuildUserKeyboard(new UserSettings(), false);
        public static object BuildAdminKeyboard() => BuildUserKeyboard(new UserSettings(), true);
        public static object BuildAllSignalsTimeframeKeyboard() => BuildUserKeyboard(new UserSettings(), false);
        public static object BuildTimeframeKeyboard() => BuildUserKeyboard(new UserSettings(), false);
    }
}
