using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CryptoSense.Services
{
    public class TelegramBotService : BackgroundService
    {
        private readonly HttpClient _httpClient;
        private readonly AppConfig _config;
        private readonly IServiceProvider _serviceProvider;
        private long _lastUpdateId = 0;

        public const string SuperAdminUsername = "alimahammadov";
        public static string? SuperAdminChatId = null;

        // API compatibility properties
        public static string UserTimeframe { get; set; } = "3m";
        public static bool AlertAllCoins { get; set; } = true;

        public class UserSettings
        {
            public bool IsActive { get; set; } = true;
            public string Timeframe { get; set; } = "3m"; // Strict user timeframe
            public List<string> Coins { get; set; } = new() { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT" };
            public DateTime LastResumeTime { get; set; } = DateTime.UtcNow;
            public string Username { get; set; } = "";
            public long? TelegramUserId { get; set; }
            public int AlertCounter { get; set; } = 0;
        }

        public static HashSet<string> AuthenticatedChats { get; } = new();
        public static ConcurrentDictionary<string, UserSettings> UserPreferences { get; } = new();
        private static readonly ConcurrentDictionary<string, string> _userStates = new();

        public TelegramBotService(HttpClient httpClient, IOptions<AppConfig> config, IServiceProvider serviceProvider)
        {
            _httpClient = httpClient;
            _config = config.Value;
            _serviceProvider = serviceProvider;
        }

        public static UserSettings GetSettings(string chatId)
        {
            return UserPreferences.GetOrAdd(chatId, _ => new UserSettings());
        }

        public async Task<bool> SendMessageAsync(string message, string targetChatId, object? replyMarkup = null)
        {
            if (string.IsNullOrWhiteSpace(_config.TelegramBotToken) || string.IsNullOrWhiteSpace(targetChatId))
            {
                return false;
            }

            try
            {
                var url = $"https://api.telegram.org/bot{_config.TelegramBotToken}/sendMessage";
                var payload = new Dictionary<string, object>
                {
                    { "chat_id", targetChatId },
                    { "text", message },
                    { "parse_mode", "HTML" },
                    { "disable_web_page_preview", false }
                };

                if (replyMarkup != null)
                {
                    payload["reply_markup"] = replyMarkup;
                }

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Telegram send error: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DeleteMessageAsync(string chatId, long messageId)
        {
            if (string.IsNullOrWhiteSpace(_config.TelegramBotToken) || string.IsNullOrWhiteSpace(chatId)) return false;
            try
            {
                var url = $"https://api.telegram.org/bot{_config.TelegramBotToken}/deleteMessage";
                var payload = new { chat_id = chatId, message_id = messageId };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public static async Task RevokeUserAsync(string username, TelegramBotService botService)
        {
            var targetChats = new List<string>();
            foreach (var kvp in UserPreferences)
            {
                if (kvp.Value.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                {
                    targetChats.Add(kvp.Key);
                }
            }

            foreach (var chatId in targetChats)
            {
                AuthenticatedChats.Remove(chatId);
                UserPreferences.TryRemove(chatId, out _);

                var kickMsg = "\u26D4 <b>HESABINIZ S\u0130L\u0130ND\u0130 V\u018F S\u0130STEMD\u018FN \u00C7IXARILDINIZ!</b>\n\n" +
                              "H\u00F6rm\u0259tli istifad\u0259\u00E7i, hesab\u0131n\u0131z sistemd\u0259n silinmi\u015Fdir v\u0259 <b>b\u00FCt\u00FCn aktiv prosesl\u0259riniz d\u0259rhal dayand\u0131r\u0131lm\u0131\u015Fd\u0131r.</b>\n\n" +
                              "Yenid\u0259n giri\u015F icaz\u0259si \u00FC\u00E7\u00FCn <b>Super Admin</b> il\u0259 \u0259laq\u0259 saxlay\u0131n:\n" +
                              "\U0001F449 <a href=\"https://t.me/alimahammadov\">@alimahammadov</a> (Ali Muhammadov)";

                await botService.SendMessageAsync(kickMsg, chatId, new { remove_keyboard = true });
            }
        }

        // DISPATCH REAL-TIME SIGNALS WITH CONFLUENCE SCORING & TRANSPARENT RISK NOTE
        // DISPATCH REAL-TIME SIGNALS WITH CONFLUENCE SCORING & TRANSPARENT RISK NOTE
        public async Task SendSignalAlertAsync(FuturesSignal signal, string? specificChatId = null)
        {
            var isLong = signal.Direction == SignalDirection.Buy || signal.SignalType.Contains("LONG");
            var cleanSymbol = signal.Symbol.Replace("USDT", "");
            var statusIcon = isLong ? "🟢" : "🔴";
            var directionText = isLong ? "AL (LONG)" : "SAT (SHORT)";
            var sentimentText = signal.NewsSentimentImpact.Contains("BULLISH") || signal.NewsSentimentImpact.Contains("MUSBET") ? "Müsbət 🟢" : (signal.NewsSentimentImpact.Contains("BEARISH") || signal.NewsSentimentImpact.Contains("MENFI") ? "Mənfi 🔴" : "Neytral ⚪");

            string BuildSignalMessage(int userSigNum)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"#{userSigNum} {statusIcon} <b>SİQNAL</b>");
                sb.AppendLine();
                sb.AppendLine($"🪙 <b>Cütlük:</b> {cleanSymbol} Futures ({signal.Timeframe})");
                sb.AppendLine($"🧭 <b>İstiqamət:</b> <b>{directionText}</b>");
                sb.AppendLine($"⏱ <b>Zaman Çərçivəsi:</b> {signal.Timeframe}");
                sb.AppendLine($"🕒 <b>Verilmə Tarixi:</b> {signal.TimestampFormatted}");
                sb.AppendLine($"🎯 <b>Confluence Razılaşma Balı:</b> <b>{signal.ConfluenceScore}%</b> (İndiqatorların razılığı)");
                sb.AppendLine($"💵 <b>Cari Giriş Qiyməti:</b> ${signal.CurrentPrice}");
                sb.AppendLine($"📰 <b>Xəbər Sentimenti:</b> {sentimentText}");
                sb.AppendLine("-----------------------------------");
                sb.AppendLine($"📍 <b>Giriş Zonası:</b> ${signal.EntryLow} - ${signal.EntryHigh}");
                sb.AppendLine($"🎯 <b>Hədəf 1 (TP1):</b> ${signal.TakeProfit1}");
                sb.AppendLine($"🎯 <b>Hədəf 2 (TP2):</b> ${signal.TakeProfit2}");
                sb.AppendLine($"🌟 <b>Hədəf 3 (TP3):</b> ${signal.TakeProfit3}");
                sb.AppendLine($"⛔ <b>Stop Loss (SL):</b> ${signal.StopLoss}");
                sb.AppendLine("-----------------------------------");
                sb.AppendLine("📊 <b>Texniki Əsaslandırma:</b>");
                foreach (var r in signal.AnalysisReasons)
                {
                    sb.AppendLine($"- {r}");
                }
                sb.AppendLine("-----------------------------------");
                sb.AppendLine("⚠️ <i>Bu maliyyə məsləhəti deyil. Confluence balı indiqatorların razılığıdır, zəmanət deyil. Risk menecmentinə riayət edin.</i>");
                return sb.ToString();
            }

            if (!string.IsNullOrEmpty(specificChatId))
            {
                var settings = GetSettings(specificChatId);
                var userSigNum = signal.UserSignalNumbers.GetOrAdd(specificChatId, _ => ++settings.AlertCounter);
                await SendMessageAsync(BuildSignalMessage(userSigNum), specificChatId);
                return;
            }

            foreach (var chatId in AuthenticatedChats)
            {
                var settings = GetSettings(chatId);
                if (!settings.IsActive) continue;

                // STRICT TIMEFRAME CHECK
                if (settings.Timeframe != "Hamısı" && settings.Timeframe != "Hamisi" && settings.Timeframe != signal.Timeframe)
                {
                    continue;
                }

                // Coin filter check
                if (settings.Coins.Count > 0 && !settings.Coins.Contains(signal.Symbol)) continue;

                var userSigNum = signal.UserSignalNumbers.GetOrAdd(chatId, _ => ++settings.AlertCounter);
                await SendMessageAsync(BuildSignalMessage(userSigNum), chatId);
            }
        }

        // TRANSPARENT OUTCOME REPORT DISPATCHER
        public async Task SendOutcomeAlertAsync(FuturesSignal signal, string outcomeType, decimal hitPrice, decimal profitPct)
        {
            bool isWin;
            if (outcomeType.Contains("Stop Loss") || outcomeType.Contains("SL") || outcomeType.Contains("Mənfi") || outcomeType.Contains("Menfi"))
            {
                isWin = false;
                if (profitPct > 0) profitPct = -Math.Abs(profitPct);
            }
            else if (outcomeType.Contains("Hədəf") || outcomeType.Contains("TP") || outcomeType.Contains("Müsbət") || outcomeType.Contains("Musbet"))
            {
                isWin = true;
                if (profitPct < 0) profitPct = Math.Abs(profitPct);
            }
            else
            {
                isWin = profitPct > 0.05m;
            }

            var icon = isWin ? "🎯" : (Math.Abs(profitPct) <= 0.05m ? "⚪" : "⛔");
            var statusText = isWin ? $"{outcomeType} (UĞURLU) ✅" : (Math.Abs(profitPct) <= 0.05m ? $"{outcomeType} (NEYTRAL) ⚪" : $"{outcomeType} (UĞURSUZ) ❌");
            var cleanSymbol = signal.Symbol.Replace("USDT", "");
            var directionStr = (signal.Direction == SignalDirection.Buy || signal.SignalType.Contains("LONG")) ? "LONG" : "SHORT";

            foreach (var chatId in AuthenticatedChats)
            {
                var settings = GetSettings(chatId);

                // 1. Check if user paused notifications
                if (!settings.IsActive) continue;

                // 2. Check if user changed timeframe
                if (settings.Timeframe != "Hamısı" && settings.Timeframe != "Hamisi" && settings.Timeframe != signal.Timeframe)
                {
                    continue;
                }

                // 3. Check if user filtered this coin
                if (settings.Coins.Count > 0 && !settings.Coins.Contains(signal.Symbol)) continue;

                // 4. Check if signal was generated before user's last reset/filter change
                if (signal.GeneratedAt < settings.LastResumeTime) continue;

                signal.UserSignalNumbers.TryGetValue(chatId, out var userSigNum);
                if (userSigNum == 0) userSigNum = signal.SignalNumber;

                var sb = new StringBuilder();
                sb.AppendLine($"{icon} <b>#{userSigNum} NƏTİCƏ HESABATI:</b>");
                sb.AppendLine($"<b>{statusText}</b>");
                sb.AppendLine();
                sb.AppendLine($"🪙 <b>Cütlük:</b> {cleanSymbol} Futures ({directionStr} - {signal.Timeframe})");
                sb.AppendLine($"📍 <b>İlkin Giriş Qiyməti:</b> ${signal.EntryPrice}");
                sb.AppendLine($"💵 <b>Bağlanış Qiyməti:</b> ${hitPrice}");
                sb.AppendLine($"📈 <b>Xalis Nəticə (PnL):</b> <b>{(profitPct >= 0 ? "+" : "")}{profitPct:F2}%</b>");
                sb.AppendLine($"🕒 <b>Siqnal Vaxtı:</b> {signal.TimestampFormatted}");
                sb.AppendLine($"🕒 <b>Bağlanma Vaxtı:</b> {DateTime.Now:dd.MM.yyyy | HH:mm:ss}");

                await SendMessageAsync(sb.ToString(), chatId);
            }
        }

        public async Task NotifySuperAdminUserLoginAsync(string username, string platform)
        {
            if (string.IsNullOrEmpty(SuperAdminChatId)) return;

            var msg = $"🔔 <b>YENİ GİRİŞ BİLDİRİŞİ:</b>\n\n" +
                      $"👤 <b>İstifadəçi:</b> <code>{username}</code>\n" +
                      $"🕒 <b>Tarix:</b> <code>{DateTime.Now:dd.MM.yyyy | HH:mm:ss}</code>\n" +
                      $"🌐 <b>Mənbə:</b> {platform}";
            await SendMessageAsync(msg, SuperAdminChatId);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (string.IsNullOrWhiteSpace(_config.TelegramBotToken)) return;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var url = $"https://api.telegram.org/bot{_config.TelegramBotToken}/getUpdates?offset={_lastUpdateId + 1}&timeout=20";
                    var response = await _httpClient.GetStringAsync(url, stoppingToken);
                    using var doc = JsonDocument.Parse(response);
                    
                    if (doc.RootElement.TryGetProperty("result", out var resultArr))
                    {
                        foreach (var item in resultArr.EnumerateArray())
                        {
                            _lastUpdateId = item.GetProperty("update_id").GetInt64();
                            if (item.TryGetProperty("message", out var msg))
                            {
                                var chatId = msg.GetProperty("chat").GetProperty("id").GetInt64().ToString();
                                var messageId = msg.GetProperty("message_id").GetInt64();
                                var text = (msg.TryGetProperty("text", out var txtEl) ? txtEl.GetString() : "")?.Trim() ?? "";

                                var fromUser = "";
                                long? fromUserId = null;
                                if (msg.TryGetProperty("from", out var fromEl))
                                {
                                    if (fromEl.TryGetProperty("username", out var uNameEl))
                                    {
                                        fromUser = (uNameEl.GetString() ?? "").TrimStart('@');
                                    }
                                    if (fromEl.TryGetProperty("id", out var idEl))
                                    {
                                        fromUserId = idEl.GetInt64();
                                    }
                                }

                                await HandleIncomingMessageAsync(chatId, fromUser, fromUserId, messageId, text);
                            }
                        }
                    }
                }
                catch
                {
                }

                await Task.Delay(1500, stoppingToken);
            }
        }

        private async Task HandleIncomingMessageAsync(string chatId, string username, long? userId, long messageId, string text)
        {
            using var scope = _serviceProvider.CreateScope();
            var userManager = (UserManagerService)scope.ServiceProvider.GetService(typeof(UserManagerService))!;
            var signalEngine = (SignalEngine)scope.ServiceProvider.GetService(typeof(SignalEngine))!;
            var newsService = (NewsService)scope.ServiceProvider.GetService(typeof(NewsService))!;

            bool isSuperAdminUser = false; // Disabled auto-superadmin: no user automatically sees the admin panel

            if (isSuperAdminUser)
            {
                SuperAdminChatId = chatId;
                AuthenticatedChats.Remove(chatId);
                await HandleSuperAdminFlowAsync(chatId, text, userManager);
                return;
            }

            // 1. AUTO-AUTHENTICATION FROM DATABASE FOR REGULAR USERS
            if (!AuthenticatedChats.Contains(chatId))
            {
                var existingUser = userManager.GetUserByChatIdOrTelegramId(chatId, userId);
                if (existingUser != null)
                {
                    AuthenticatedChats.Add(chatId);
                    var set = GetSettings(chatId);
                    set.Username = existingUser.Username;
                    set.TelegramUserId = userId;
                }
            }

            // 2. UNAUTHENTICATED USERS: LOGIN ONLY
            if (!AuthenticatedChats.Contains(chatId))
            {
                var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                bool isLoginAttempt = !text.StartsWith("/") && !text.StartsWith("🧭") && !text.StartsWith("⚡") && 
                                     !text.StartsWith("⭐") && !text.StartsWith("📊") && !text.StartsWith("⚙️") && 
                                     !text.StartsWith("🗑") && !text.StartsWith("⏱") && !text.StartsWith("🧹") && 
                                     !text.StartsWith("🛑") && !text.StartsWith("▶️") && !text.StartsWith("📰") && 
                                     !text.StartsWith("⬅️") && parts.Length >= 2;

                if (isLoginAttempt)
                {
                    var inputUser = parts[0];
                    var inputPass = string.Join(" ", parts.Skip(1));

                    var (isValid, user) = userManager.ValidateLogin(inputUser, inputPass, userId, chatId);
                    _ = DeleteMessageAsync(chatId, messageId);

                    if (isValid && user != null)
                    {
                        AuthenticatedChats.Add(chatId);
                        var settings = GetSettings(chatId);
                        settings.Username = user.Username;
                        settings.TelegramUserId = userId;
                        settings.IsActive = true;
                        settings.Timeframe = "3m"; // STRICT 3M DEFAULT
                        settings.LastResumeTime = DateTime.UtcNow;

                        var onboardingMsg = $"✅ <b>Giriş Təsdiqləndi! Xoş Gəldiniz, {user.Username}!</b>\n\n" +
                                            $"🚀 <b>KriptoBot v2 Kvantitativ Ticarət Sistemi AKTİVDİR 🟢</b>\n\n" +
                                            $"Aktiv Zaman Çərçivəsi: <b>{settings.Timeframe}</b>\n\n" +
                                            $"Yalnız seçdiyiniz <b>{settings.Timeframe}</b> zamanı üzrə 24/7 siqnallar və nəticələr göndəriləcək.";
                        
                        await SendMessageAsync(onboardingMsg, chatId, BuildUserKeyboard(settings));
                        await NotifySuperAdminUserLoginAsync(user.Username, $"Telegram (@{username})");
                        return;
                    }
                    else
                    {
                        await SendMessageAsync("❌ <b>İstifadəçi adı və ya parol yanlışdır!</b>\n\nQeydiyyat və giriş icazəsi üçün <b>Super Admin</b> ilə əlaqə saxlayın:\n👉 <a href=\"https://t.me/alimahammadov\">@alimahammadov</a> (Ali Muhammadov)", chatId, new { remove_keyboard = true });
                        return;
                    }
                }

                // If not trying to log in (e.g. /start or random text)
                var welcomeAndAuth = "👋 <b>Salam! KriptoBot Xidmətinə xoş gəlmisiniz.</b>\n\n" +
                                     "⚠️ <b>Sistemdən istifadə etmək üçün QEYDİYYATDAN KEÇMƏLİ və daxil olmalısınız!</b>\n\n" +
                                     "Sistemə daxil olmaq üçün <b>İstifadəçi Adınızı</b> və <b>Parolunuzu</b> bir sətirdə, aralarında boşluq qoyaraq yazın:\n\n" +
                                     "📌 <b>Düzgün Format:</b>\n" +
                                     "<code>[İstifadəçiAdı] [Parol]</code>\n\n" +
                                     "💡 <b>Nümunə:</b>\n" +
                                     "<code>Murad 123456</code>\n\n" +
                                     "-----------------------------------\n" +
                                     "Hesabınız yoxdur? Qeydiyyat və giriş icazəsi üçün <b>Super Admin</b> ilə əlaqə saxlayın:\n" +
                                     "👉 <a href=\"https://t.me/alimahammadov\">@alimahammadov</a> (Ali Muhammadov)";
                
                await SendMessageAsync(welcomeAndAuth, chatId, new { remove_keyboard = true });
                return;
            }

            // 3. AUTHENTICATED USERS FLOW
            var userSettings = GetSettings(chatId);

            // Check if user was deleted
            var isStillRegistered = userManager.GetAllUsers().Any(u => u.Username.Equals(userSettings.Username, StringComparison.OrdinalIgnoreCase));
            if (!isStillRegistered)
            {
                AuthenticatedChats.Remove(chatId);
                UserPreferences.TryRemove(chatId, out _);

                var kickMsg = "⛔ <b>HESABINIZ SİLİNDİ VƏ SİSTEMDƏN ÇIXARILDINIZ!</b>\n\n" +
                              "Hörmətli istifadəçi, hesabınız sistemdən silinmişdir və <b>bütün prosesləriniz dayandırılmışdır.</b>\n\n" +
                              "Yenidən giriş və qeydiyyat üçün <b>Super Admin</b> ilə əlaqə saxlayın:\n" +
                              "👉 <a href=\"https://t.me/alimahammadov\">@alimahammadov</a> (Ali Muhammadov)";

                await SendMessageAsync(kickMsg, chatId, new { remove_keyboard = true });
                return;
            }

            // If user re-types username + password while already logged in
            var userParts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (userParts.Length == 2 && !text.StartsWith("/") && !text.StartsWith("🧭") && !text.StartsWith("⚡") && 
                !text.StartsWith("⭐") && !text.StartsWith("📊") && !text.StartsWith("⚙️") && 
                !text.StartsWith("🗑") && !text.StartsWith("⏱") && !text.StartsWith("🧹") && 
                !text.StartsWith("🛑") && !text.StartsWith("▶️") && !text.StartsWith("📰") && 
                !text.StartsWith("⬅️") && !text.StartsWith("👑") && !text.StartsWith("➕") && !text.StartsWith("👥"))
            {
                _ = DeleteMessageAsync(chatId, messageId);
                await SendMessageAsync($"✅ <b>Siz artıq sistemə daxil olmusunuz!</b>\nİstifadəçi: <b>{userSettings.Username}</b>", chatId, BuildUserKeyboard(userSettings));
                return;
            }

            // STATE: DELETING A SPECIFIC COIN
            if (_userStates.TryGetValue(chatId, out var coinDelState) && coinDelState == "USER_WAITING_DELETE_COIN")
            {
                _userStates.TryRemove(chatId, out _);
                var coinToDel = text.Trim().ToUpper();
                if (!coinToDel.EndsWith("USDT")) coinToDel += "USDT";

                if (userSettings.Coins.Contains(coinToDel))
                {
                    userSettings.Coins.Remove(coinToDel);
                    userSettings.LastResumeTime = DateTime.UtcNow;
                    var cleanDel = coinToDel.Replace("USDT", "");
                    var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                    var succMsg = $"✅ <b>'{cleanDel}' coini siyahınızdan silindi!</b>\n\n" +
                                  $"Cari siyahınız ({userSettings.Coins.Count}/10 ədəd):\n" +
                                  $"<code>{(string.IsNullOrEmpty(cleanList) ? "Siyahı boşdur" : cleanList)}</code>";
                    await SendMessageAsync(succMsg, chatId, BuildUserKeyboard(userSettings));
                }
                else
                {
                    var cleanDel = coinToDel.Replace("USDT", "");
                    await SendMessageAsync($"⚠️ <b>'{cleanDel}' coini siyahınızda tapılmadı!</b>", chatId, BuildUserKeyboard(userSettings));
                }
                return;
            }

            // STATE: ADDING COINS (MAX 10)
            if (_userStates.TryGetValue(chatId, out var coinAddState) && coinAddState == "WAITING_COIN_INPUT")
            {
                _userStates.TryRemove(chatId, out _);
                var parts = text.Split(new[] { ',', ' ', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 0 && !text.StartsWith("/"))
                {
                    foreach (var p in parts)
                    {
                        var clean = p.ToUpper();
                        if (!clean.EndsWith("USDT")) clean += "USDT";
                        if (!userSettings.Coins.Contains(clean))
                        {
                            if (userSettings.Coins.Count < 10)
                            {
                                userSettings.Coins.Add(clean);
                            }
                        }
                    }

                    userSettings.LastResumeTime = DateTime.UtcNow;
                    var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                    var conf = $"✅ <b>Seçilmiş coinləriniz yeniləndi ({userSettings.Coins.Count}/10 ədəd):</b>\n\n" +
                               $"<code>{cleanList}</code>";
                    await SendMessageAsync(conf, chatId, BuildUserKeyboard(userSettings));
                    return;
                }
            }

            // SUBMENU ACTION: SCAN SPECIFIC TIMEFRAME
            if (text == "⏱ 3 Dəqiqə (3m) Siqnalları" ||
                text == "⏱ 5 Dəqiqə (5m) Siqnalları" ||
                text == "⏱ 15 Dəqiqə (15m) Siqnalları" ||
                text == "⏱ 1 Saat (1h) Siqnalları" ||
                text == "⏱ 4 Saat (4h) Siqnalları" ||
                text == "🌟 Bütün Zamanlar (Hamısı) Siqnalları")
            {
                var sampleCoins = new[] { "SOLUSDT", "BTCUSDT", "ETHUSDT", "DOGEUSDT", "XRPUSDT", "BNBUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT" };

                if (text.Contains("Bütün Zamanlar") || text.Contains("Hamısı"))
                {
                    userSettings.Timeframe = "Hamısı";
                    userSettings.LastResumeTime = DateTime.UtcNow;
                    await SendMessageAsync("⏳ <b>Bütün zaman çərçivələri (3m, 5m, 15m, 1h, 4h) üzrə bazar skan edilir...</b>", chatId, BuildUserKeyboard(userSettings));
                    var tfs = new[] { "3m", "5m", "15m", "1h", "4h" };
                    int totalFound = 0;
                    foreach (var tf in tfs)
                    {
                        foreach (var sym in sampleCoins.Take(3))
                        {
                            var sig = await signalEngine.AnalyzeCoinAsync(sym, tf);
                            if (sig.Confidence >= 78 && (sig.SignalType.Contains("LONG") || sig.SignalType.Contains("SHORT")))
                            {
                                await SendSignalAlertAsync(sig, chatId);
                                totalFound++;
                                await Task.Delay(250);
                            }
                        }
                    }
                    return;
                }
                else
                {
                    string targetTf = "3m";
                    if (text.Contains("3m")) targetTf = "3m";
                    else if (text.Contains("5m")) targetTf = "5m";
                    else if (text.Contains("15m")) targetTf = "15m";
                    else if (text.Contains("1h")) targetTf = "1h";
                    else if (text.Contains("4h")) targetTf = "4h";

                    userSettings.Timeframe = targetTf;
                    userSettings.LastResumeTime = DateTime.UtcNow;

                    await SendMessageAsync($"⏳ <b>Yalnız ({targetTf}) üzrə 78%+ Confluence siqnalları axtarılır... (Profiliniz '{targetTf}' olaraq təyin edildi)</b>", chatId, BuildUserKeyboard(userSettings));

                    int found = 0;
                    foreach (var sym in sampleCoins)
                    {
                        var sig = await signalEngine.AnalyzeCoinAsync(sym, targetTf);
                        if (sig.Confidence >= 78 && (sig.SignalType.Contains("LONG") || sig.SignalType.Contains("SHORT")))
                        {
                            await SendSignalAlertAsync(sig, chatId);
                            found++;
                            await Task.Delay(250);
                        }
                    }
                    if (found == 0)
                    {
                        await SendMessageAsync($"ℹ️ Hal-hazırda {targetTf} zamanında tələblərə cavab verən aktiv siqnal yoxdur.", chatId, BuildUserKeyboard(userSettings));
                    }
                    return;
                }
            }

            // DIRECT TIMEFRAME PREFERENCE SELECTION
            if (text == "⏱ 3 Dəqiqə (3m)" || text == "3m")
            {
                userSettings.Timeframe = "3m";
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>3m</code>\n<i>Artıq yalnız 3m siqnalları alacaqsınız.</i>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text == "⏱ 5 Dəqiqə (5m)" || text == "5m")
            {
                userSettings.Timeframe = "5m";
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>5m</code>\n<i>Artıq yalnız 5m siqnalları alacaqsınız.</i>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text == "⏱ 15 Dəqiqə (15m)" || text == "15m")
            {
                userSettings.Timeframe = "15m";
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>15m</code>\n<i>Artıq yalnız 15m siqnalları alacaqsınız.</i>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text == "⏱ 1 Saat (1h)" || text == "1h")
            {
                userSettings.Timeframe = "1h";
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>1h</code>\n<i>Artıq yalnız 1h siqnalları alacaqsınız.</i>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text == "⏱ 4 Saat (4h)" || text == "4h")
            {
                userSettings.Timeframe = "4h";
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>4h</code>\n<i>Artıq yalnız 4h siqnalları alacaqsınız.</i>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text == "🌟 Bütün Zamanlar (Hamısı)" || text == "Hamisi" || text == "Hamısı")
            {
                userSettings.Timeframe = "Hamısı";
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>Hamısı</code>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text.Contains("Geri") || text.Contains("Əsas Menyu") || text == "/menu")
            {
                await SendMessageAsync("📊 <b>Əsas Menyu:</b>", chatId, BuildUserKeyboard(userSettings));
                return;
            }

            if (text == "/start" || text == "/help" || text.Contains("Menyu"))
            {
                var welcome = "📊 <b>KriptoBot v2 Xidməti - Canlı Bazar Paneli</b>\n\n" +
                              "👤 İstifadəçi: <b>" + userSettings.Username + "</b>\n" +
                              "Bildiriş Statusu: " + (userSettings.IsActive ? "<b>AKTİV 🟢</b>" : "<b>DAYANDIRILIB 🔴</b>") + "\n" +
                              "Aktiv Zaman Çərçivəsi: <b>" + userSettings.Timeframe + "</b>\n" +
                              "Seçilmiş Coinlər: <b>" + userSettings.Coins.Count + "/10 ədəd</b>\n\n" +
                              "Əməliyyatlar üçün aşağıdakı menyudan istifadə edin:";
                await SendMessageAsync(welcome, chatId, BuildUserKeyboard(userSettings));
            }
            // STOP NOTIFICATIONS
            else if (text.Contains("Dayandır") || text.Contains("Dayandir") || text == "/stop")
            {
                userSettings.IsActive = false;
                await SendMessageAsync("🛑 <b>Bildirişlər və nəticə hesabatları dayandırıldı.</b>\n\nArxa fondan sizə heç bir siqnal və nəticə göndərilməyəcək.\nYenidən başlamaq üçün 'Bildirişləri Başlat' düyməsinə vurun.", chatId, BuildUserKeyboard(userSettings));
            }
            // RESUME NOTIFICATIONS
            else if (text.Contains("Başlat") || text.Contains("Baslat") || text == "/resume")
            {
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync("▶️ <b>Bildirişlər aktivləşdirildi!</b>\n\nSeçdiyiniz zaman çərçivəsi (" + userSettings.Timeframe + ") üzrə 24/7 siqnallar və nəticələr göndəriləcək.", chatId, BuildUserKeyboard(userSettings));
            }
            // RESET / CLEAR PAST SIGNALS
            else if (text.Contains("Sıfırla") || text.Contains("Sifirla") || text == "/clear" || text == "/reset")
            {
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync("🧹 <b>Keçmiş siqnal izləmələri profiliniz üçün sıfırlandı!</b>\n\nBundan əvvəlki heç bir köhnə siqnalın nəticəsi sizə gəlməyəcək. Yalnız bu andan etibarən yaranan təzə siqnallar izlənəcək.", chatId, BuildUserKeyboard(userSettings));
            }
            // PERFORMANCE STATS
            else if (text.Contains("Statistika") || text == "/stats")
            {
                var stats = await signalEngine.GetPerformanceStatsAsync();
                var sb = new StringBuilder();
                sb.AppendLine("📊 <b>Sistemin Real Statistik Performansı (Şəffaf İzləmə):</b>");
                sb.AppendLine("-----------------------------------");
                sb.AppendLine($"📌 <b>Ümumi Siqnallar:</b> {stats.TotalSignals} ədəd");
                sb.AppendLine($"🟡 <b>Açıq İzlənən:</b> {stats.OpenSignals} ədəd");
                sb.AppendLine($"✅ <b>Uğurlu (TP/Müsbət):</b> {stats.SuccessSignals} ədəd");
                sb.AppendLine($"❌ <b>Uğursuz (SL/Mənfi):</b> {stats.FailedSignals} ədəd");
                sb.AppendLine($"⚪ <b>Neytral:</b> {stats.NeutralSignals} ədəd");
                sb.AppendLine("-----------------------------------");
                sb.AppendLine($"🎯 <b>Real Qələbə Faiz (Win Rate):</b> <b>{stats.WinRatePercent}%</b>");
                sb.AppendLine($"📈 <b>Ümumi Xalis PnL:</b> <b>{(stats.TotalNetProfitPercent >= 0 ? "+" : "")}{stats.TotalNetProfitPercent}%</b>");
                sb.AppendLine($"📊 <b>Orta Əməliyyat Gəliri:</b> {(stats.AvgProfitPerTradePercent >= 0 ? "+" : "")}{stats.AvgProfitPerTradePercent}%");
                sb.AppendLine("-----------------------------------");
                sb.AppendLine("<i>Qeyd: Bütün uğursuz (Stop Loss) və uğurlu əməliyyatlar heç bir gizlədilmə olmadan qeyd olunur.</i>");

                await SendMessageAsync(sb.ToString(), chatId, BuildUserKeyboard(userSettings));
            }
            // MAIN BUTTON: USER CLICKS 'BÜTÜN SİQNALLAR'
            else if (text == "⚡ Bütün Siqnallar" || text == "Bütün Siqnallar" || text == "/scan")
            {
                var tfAskMsg = "⚡ <b>Bütün Bazar Üzrə Siqnal Axtarışı</b>\n\n" +
                               "Hansı zaman aralığı üzrə 78%+ Confluence siqnalları axtarmaq istəyirsiniz?\n\n" +
                               "Aşağıdakı seçimlərdən birinə vurun:";
                await SendMessageAsync(tfAskMsg, chatId, BuildAllSignalsTimeframeKeyboard());
            }
            // MY COINS
            else if (text.Contains("Coinlərim") || text.Contains("Coinlerim") || text == "/my")
            {
                if (userSettings.Coins.Count == 0)
                {
                    var emptyMsg = "⭐ <b>Sizin Seçilmiş Coinləriniz</b>\n\n" +
                                   "Hal-hazırda siyahınız boşdur.\n" +
                                   "Coin əlavə etmək üçün <b>⚙️ Coin Seçimi</b> düyməsinə vurun (Maks: 10 ədəd).";
                    await SendMessageAsync(emptyMsg, chatId, BuildUserKeyboard(userSettings));
                    return;
                }

                var tf = (userSettings.Timeframe == "Hamısı" || userSettings.Timeframe == "Hamisi") ? "3m" : userSettings.Timeframe;
                await SendMessageAsync($"⏳ Seçdiyiniz {userSettings.Coins.Count} coin üzrə ({tf}) analizi aparılır...", chatId);

                var sb = new StringBuilder();
                sb.AppendLine("⭐ <b>Mənim Coinlərim - Canlı Kvantitativ Analiz</b>");
                sb.AppendLine($"Cəmi: <b>{userSettings.Coins.Count}/10 ədəd</b> | Aktiv Zaman: <b>{userSettings.Timeframe}</b>");
                sb.AppendLine("-----------------------------------");

                foreach (var sym in userSettings.Coins)
                {
                    var sig = await signalEngine.AnalyzeCoinAsync(sym, tf);
                    var cleanSym = sym.Replace("USDT", "");
                    var trendIcon = sig.SignalType.Contains("LONG") ? "🟢 YÜKSƏLİŞ" : (sig.SignalType.Contains("SHORT") ? "🔴 ENİŞ" : "⚪ NEYTRAL");
                    sb.AppendLine($"• <b>{cleanSym}</b> (${sig.CurrentPrice}) — {trendIcon} (Confluence: {sig.ConfluenceScore}%)");

                    if (sig.Confidence >= 78 && (sig.SignalType.Contains("LONG") || sig.SignalType.Contains("SHORT")))
                    {
                        await SendSignalAlertAsync(sig, chatId);
                        await Task.Delay(200);
                    }
                }
                sb.AppendLine("-----------------------------------");
                sb.AppendLine("Yeni coin əlavə etmək üçün: <b>⚙️ Coin Seçimi</b>");
                sb.AppendLine("Coini siyahıdan silmək üçün: <b>🗑 Coin Sil</b>");

                await SendMessageAsync(sb.ToString(), chatId, BuildUserKeyboard(userSettings));
            }
            // TIME PANEL
            else if (text.Contains("Zaman Çərçivəsi") || text.Contains("Zaman") || text == "/tf")
            {
                var tfPanelMsg = "⏱ <b>Zaman Çərçivəsi Paneli</b>\n\n" +
                                 "Hal-hazırda təyin edilmiş: <b>" + userSettings.Timeframe + "</b>\n\n" +
                                 "Seçmək istədiyiniz yeni zamanı aşağıdakı paneldən seçin:";
                await SendMessageAsync(tfPanelMsg, chatId, BuildTimeframeKeyboard());
            }
            // DELETE COIN
            else if (text.Contains("Coin Sil") || text == "/delcoin")
            {
                _userStates[chatId] = "USER_WAITING_DELETE_COIN";
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var prompt = "🗑 <b>Coin Silmək</b>\n\n" +
                             $"Cari siyahınız ({userSettings.Coins.Count}/10 ədəd):\n" +
                             $"<code>{(string.IsNullOrEmpty(cleanList) ? "Siyahı boşdur" : cleanList)}</code>\n\n" +
                             "Siyahıdan silmək istədiyiniz coinin <b>Adını</b> yazın:\n" +
                             "📌 <b>Məsələn:</b> <code>SOL</code> və ya <code>DOGE</code>";
                await SendMessageAsync(prompt, chatId, BuildUserKeyboard(userSettings));
            }
            // ADD COINS
            else if (text.Contains("Coin Seçimi") || text.Contains("Coin Secimi") || text == "/setcoins")
            {
                _userStates[chatId] = "WAITING_COIN_INPUT";
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var prompt = "⚙️ <b>Coin Seçimi (Maksimum 10 ədəd haqqınız var):</b>\n\n" +
                             $"Hal-hazırda seçilmiş: <b>{userSettings.Coins.Count}/10 ədəd</b>\n" +
                             $"Cari siyahı: <code>{(string.IsNullOrEmpty(cleanList) ? "Boşdur" : cleanList)}</code>\n\n" +
                             "Əlavə etmək istədiyiniz coinlərin adını aşağıya <b>vergüllə</b> yazıb göndərin.\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>BTC, ETH, SOL, SUI, DOGE, PEPE, AVAX</code>";
                await SendMessageAsync(prompt, chatId, BuildUserKeyboard(userSettings));
            }
            // BITCOIN
            else if (text.Contains("Bitcoin") || text == "/btc")
            {
                var compass = await signalEngine.GetBtcCompassAsync();
                var btcMsg = $"🧭 <b>Bitcoin Bazar Kompası</b>\n\n" +
                             $"🕒 Tarix: <b>{compass.TimestampFormatted}</b>\n" +
                             $"💵 Cari Qiymət: <b>${compass.Price}</b>\n" +
                             $"📈 Trend İstiqaməti: <b>{compass.Trend}</b>\n" +
                             $"🎯 Təhlil Gücü: <b>{compass.BullishScore}%</b>\n\n" +
                             $"<i>{compass.Summary}</i>";
                await SendMessageAsync(btcMsg, chatId, BuildUserKeyboard(userSettings));
            }
            // NEWS
            else if (text.Contains("Xəbərləri") || text.Contains("Xeberleri") || text == "/news")
            {
                var newsSummary = await newsService.GetNewsAndSentimentAsync();
                var sb = new StringBuilder();
                sb.AppendLine("📰 <b>Qlobal Kripto Xəbərləri & Sentimenti (Azərbaycan Dilində)</b>");
                sb.AppendLine($"📊 Ümumi Bazar Əhvalı: <b>{newsSummary.Status} ({newsSummary.OverallScore}%)</b>");
                sb.AppendLine();
                sb.AppendLine("Son xəbərlər (keçid üçün xəbərə klikləyin):");
                sb.AppendLine("-----------------------------------");
                foreach (var n in newsSummary.LatestNews.Take(5))
                {
                    var safeTitle = System.Net.WebUtility.HtmlEncode(n.Title);
                    sb.AppendLine($"• <b>[{n.Source}]</b> <a href=\"{n.Url}\">{safeTitle}</a> - <i>{n.Sentiment}</i>");
                }
                sb.AppendLine("-----------------------------------");
                await SendMessageAsync(sb.ToString(), chatId, BuildUserKeyboard(userSettings));
            }
            else
            {
                var potentialSym = text.ToUpper();
                if (!potentialSym.EndsWith("USDT")) potentialSym += "USDT";
                var tf = (userSettings.Timeframe == "Hamısı" || userSettings.Timeframe == "Hamisi") ? "3m" : userSettings.Timeframe;
                var sig = await signalEngine.AnalyzeCoinAsync(potentialSym, tf);
                if (sig.SignalType != "MƏLUMAT AZDIR")
                {
                    await SendSignalAlertAsync(sig, chatId);
                }
                else
                {
                    var helpMsg = "ℹ️ <b>Nə etmək lazımdır?</b>\n\n" +
                                  "Aşağıdakı menyudan seçim edin və ya analiz etmək istədiyiniz coinin adını yazın.\n" +
                                  "📌 Məsələn: <code>SOL</code>, <code>BTC</code>, <code>ETH</code>, <code>DOGE</code>";
                    await SendMessageAsync(helpMsg, chatId, BuildUserKeyboard(userSettings));
                }
            }
        }

        private async Task HandleSuperAdminFlowAsync(string chatId, string text, UserManagerService userManager)
        {
            if (_userStates.TryGetValue(chatId, out var state) && state == "ADMIN_WAITING_CREATE_USER")
            {
                _userStates.TryRemove(chatId, out _);
                var parts = text.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    var newUsername = parts[0];
                    var newPassword = parts[1];
                    var created = userManager.CreateUser(newUsername, newPassword);
                    if (created)
                    {
                        var msg = $"✅ <b>İstifadəçi uğurla yaradıldı!</b>\n\n" +
                                  $"👤 <b>İstifadəçi Adı:</b> <code>{newUsername}</code>\n" +
                                  $"🔑 <b>Parol:</b> <code>{newPassword}</code>\n\n" +
                                  $"<i>İstifadəçiyə bildirin ki, bota daxil olaraq <code>{newUsername} {newPassword}</code> yazsın.</i>";
                        await SendMessageAsync(msg, chatId, BuildAdminKeyboard());
                    }
                    else
                    {
                        await SendMessageAsync($"⚠️ <b>Xəta:</b> <code>{newUsername}</code> adlı istifadəçi artıq mövcuddur!", chatId, BuildAdminKeyboard());
                    }
                    return;
                }
                else
                {
                    await SendMessageAsync("⚠️ <b>Formatı düzgün daxil edin!</b>\nMəsələn: <code>Murad 123456</code>", chatId, BuildAdminKeyboard());
                    return;
                }
            }

            if (_userStates.TryGetValue(chatId, out var delState) && delState == "ADMIN_WAITING_DELETE_USER")
            {
                _userStates.TryRemove(chatId, out _);
                var userToDelete = text.Trim();
                var deleted = userManager.DeleteUser(userToDelete);
                if (deleted)
                {
                    await RevokeUserAsync(userToDelete, this);
                    await SendMessageAsync($"✅ <b>İstifadəçi '{userToDelete}' sistemdən silindi və bütün prosesləri dayandırıldı!</b>", chatId, BuildAdminKeyboard());
                }
                else
                {
                    await SendMessageAsync($"⚠️ <b>'{userToDelete}' tapılmadı və ya silinə bilməz.</b>", chatId, BuildAdminKeyboard());
                }
                return;
            }

            if (_userStates.TryGetValue(chatId, out var pwdState) && pwdState == "ADMIN_WAITING_RESET_PWD")
            {
                _userStates.TryRemove(chatId, out _);
                var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    var uName = parts[0];
                    var newPwd = parts[1];
                    var res = userManager.ResetPassword(uName, newPwd);
                    if (res)
                    {
                        await SendMessageAsync($"✅ <b>'{uName}' üçün yeni parol təyin edildi:</b> <code>{newPwd}</code>", chatId, BuildAdminKeyboard());
                    }
                    else
                    {
                        await SendMessageAsync($"⚠️ <b>'{uName}' adlı istifadəçi tapılmadı!</b>", chatId, BuildAdminKeyboard());
                    }
                    return;
                }
                else
                {
                    await SendMessageAsync("⚠️ <b>Formatı düzgün daxil edin!</b>\nMəsələn: <code>Murad yeni123</code>", chatId, BuildAdminKeyboard());
                    return;
                }
            }

            if (text == "➕ İstifadəçi Yarat" || text == "/adduser")
            {
                _userStates[chatId] = "ADMIN_WAITING_CREATE_USER";
                var prompt = "➕ <b>Yeni İstifadəçi Yaratmaq</b>\n\n" +
                             "Yaratmaq istədiyiniz <b>İstifadəçi Adını</b> və <b>Parolu</b> aralarında boşluq qoyaraq yazın:\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>Murad 123456</code>";
                await SendMessageAsync(prompt, chatId, BuildAdminKeyboard());
                return;
            }
            else if (text == "👥 İstifadəçilərin Siyahısı" || text == "/users")
            {
                var users = userManager.GetAllUsers();
                var sb = new StringBuilder();
                sb.AppendLine("👥 <b>Sistemdəki Qeydiyyatlı İstifadəçilər:</b>");
                sb.AppendLine($"Ümumi say: <b>{users.Count} nəfər</b>");
                sb.AppendLine("-----------------------------------");
                int index = 1;
                foreach (var u in users)
                {
                    var tgName = !string.IsNullOrEmpty(u.TelegramUsername) ? $"@{u.TelegramUsername}" : (u.TelegramChatId != null ? $"ID: {u.TelegramChatId}" : "Daxil olmayıb");
                    sb.AppendLine($"{index}. <b>{u.Username}</b> | Rol: <code>{u.Role}</code> | Status: Aktiv 🟢");
                    sb.AppendLine($"   Telegram: <code>{tgName}</code>");
                    index++;
                }
                sb.AppendLine("-----------------------------------");
                await SendMessageAsync(sb.ToString(), chatId, BuildAdminKeyboard());
                return;
            }
            else if (text == "🗑 İstifadəçi Sil" || text == "/deleteuser")
            {
                _userStates[chatId] = "ADMIN_WAITING_DELETE_USER";
                var prompt = "🗑 <b>İstifadəçi Silmək</b>\n\n" +
                             "Sistemdən silmək istədiyiniz istifadəçinin <b>Adını</b> yazın:\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>Murad</code>";
                await SendMessageAsync(prompt, chatId, BuildAdminKeyboard());
                return;
            }
            else if (text == "🔑 Parolu Dəyiş" || text == "/resetpwd")
            {
                _userStates[chatId] = "ADMIN_WAITING_RESET_PWD";
                var prompt = "🔑 <b>İstifadəçi Parolunu Dəyişmək</b>\n\n" +
                             "İstifadəçi adını və yeni parolu aralarında boşluqla yazın:\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>Murad yeni123</code>";
                await SendMessageAsync(prompt, chatId, BuildAdminKeyboard());
                return;
            }
            else
            {
                var welcomeAdmin = "👑 <b>Super Admin İdarəetmə Paneli (CRUD):</b>\n\n" +
                                   "İstifadəçiləri yaratmaq, silmək və ya parolları dəyişmək üçün aşağıdakı düymələrdən istifadə edin:";
                await SendMessageAsync(welcomeAdmin, chatId, BuildAdminKeyboard());
            }
        }

        private static object BuildUserKeyboard(UserSettings settings)
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

        private static object BuildAdminKeyboard()
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

        private static object BuildAllSignalsTimeframeKeyboard()
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

        private static object BuildTimeframeKeyboard()
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