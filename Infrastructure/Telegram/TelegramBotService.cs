using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CryptoSense.Infrastructure.Telegram
{
    public class TelegramBotService : BackgroundService, ITelegramBotService
    {
        private readonly HttpClient _httpClient;
        private readonly AppConfig _config;
        private readonly IServiceProvider _serviceProvider;
        private long _lastUpdateId = 0;

        public static string? SuperAdminChatId = null;
        public static ConcurrentDictionary<string, UserSettings> UserPreferences { get; } = new();
        private static readonly ConcurrentDictionary<string, string> _userStates = new();
        private static readonly ConcurrentDictionary<string, int> _signalUserNumberMap = new();
        private static readonly string DataDirectory = Directory.Exists("/app/data")
            ? "/app/data"
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string SettingsFilePath = Path.Combine(DataDirectory, "user_preferences.json");
        private static readonly string SignalMapFilePath = Path.Combine(DataDirectory, "signal_user_numbers.json");

        static TelegramBotService()
        {
            LoadSettings();
        }

        private static void LoadSettings()
        {
            try
            {
                if (!Directory.Exists(DataDirectory))
                {
                    Directory.CreateDirectory(DataDirectory);
                }

                // Auto-migrate legacy files from base directory if present
                var legacySettings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user_preferences.json");
                if (!File.Exists(SettingsFilePath) && File.Exists(legacySettings))
                {
                    try { File.Copy(legacySettings, SettingsFilePath); } catch { }
                }

                var legacySignalMap = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "signal_user_numbers.json");
                if (!File.Exists(SignalMapFilePath) && File.Exists(legacySignalMap))
                {
                    try { File.Copy(legacySignalMap, SignalMapFilePath); } catch { }
                }

                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, UserSettings>>(json);
                    if (loaded != null)
                    {
                        foreach (var kvp in loaded)
                        {
                            UserPreferences[kvp.Key] = kvp.Value;
                        }
                    }
                }

                if (File.Exists(SignalMapFilePath))
                {
                    var mapJson = File.ReadAllText(SignalMapFilePath);
                    var loadedMap = JsonSerializer.Deserialize<Dictionary<string, int>>(mapJson);
                    if (loadedMap != null)
                    {
                        foreach (var kvp in loadedMap)
                        {
                            _signalUserNumberMap[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            catch { }
        }

        public static void SaveSettings()
        {
            try
            {
                if (!Directory.Exists(DataDirectory))
                {
                    Directory.CreateDirectory(DataDirectory);
                }

                var dict = new Dictionary<string, UserSettings>(UserPreferences);
                var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);

                var mapDict = new Dictionary<string, int>(_signalUserNumberMap);
                var mapJson = JsonSerializer.Serialize(mapDict);
                File.WriteAllText(SignalMapFilePath, mapJson);
            }
            catch { }
        }

        public TelegramBotService(HttpClient httpClient, IOptions<AppConfig> config, IServiceProvider serviceProvider)
        {
            _httpClient = httpClient;
            _config = config.Value;
            _serviceProvider = serviceProvider;
            if (!string.IsNullOrEmpty(_config.SuperAdminChatId))
            {
                SuperAdminChatId = _config.SuperAdminChatId;
            }
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
                Console.WriteLine($"[TelegramBotService] Send error: {ex.Message}");
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

        public async Task RevokeUserSessionAsync(string username)
        {
            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var allUsers = await userManager.GetAllUsersAsync();
            var target = allUsers.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (target != null && !string.IsNullOrEmpty(target.TelegramChatId))
            {
                var chatId = target.TelegramChatId;
                UserPreferences.TryRemove(chatId, out _);
                _userStates.TryRemove(chatId, out _);

                var kickMsg = "⛔ <b>HESABINIZ SİLİNDİ VƏ SİSTEMDƏN ÇIXARILDINIZ!</b>\n\n" +
                              "Hörmətli istifadəçi, hesabınız sistemdən silinmişdir və bütün aktiv prosesləriniz dayandırılmışdır.\n\n" +
                              "Yenidən giriş icazəsi üçün <b>Super Admin</b> ilə əlaqə saxlayın:\n" +
                              "👉 <a href=\"https://t.me/Ali_Mahammadov\">@Ali_Mahammadov</a>";

                await SendMessageAsync(kickMsg, chatId, new { remove_keyboard = true });
            }
        }

        public async Task SendSignalAlertAsync(FuturesSignal signal, string? specificChatId = null)
        {
            if (!string.IsNullOrEmpty(specificChatId))
            {
                var settings = GetSettings(specificChatId);
                var userSigNum = signal.UserSignalNumbers.GetOrAdd(specificChatId, _ => ++settings.AlertCounter);
                _signalUserNumberMap[$"{signal.Id}_{specificChatId}"] = userSigNum;
                SaveSettings();
                var msg = TelegramMessageFormatter.FormatSignalAlert(signal, userSigNum);
                settings.LastSignalSentUtc = DateTime.UtcNow;
                settings.LastHeartbeatSentUtc = DateTime.UtcNow;
                await SendMessageAsync(msg, specificChatId);
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var activeUsers = await userManager.GetAllUsersAsync();

            foreach (var user in activeUsers)
            {
                if (string.IsNullOrEmpty(user.TelegramChatId) || !user.IsActive) continue;
                var chatId = user.TelegramChatId;

                var settings = GetSettings(chatId);
                if (!settings.IsActive) continue;

                // Strict Timeframe check
                if (settings.Timeframe != "Hamısı" && settings.Timeframe != "Hamisi" && settings.Timeframe != signal.Timeframe)
                {
                    continue;
                }

                // Strict Chronological check: never send a signal generated before the user selected timeframe / resumed
                if (signal.GeneratedAt < settings.LastResumeTime.AddSeconds(-2))
                {
                    continue;
                }

                // Coin filter check
                if (settings.Coins.Count > 0 && !settings.Coins.Contains(signal.Symbol)) continue;

                var userSigNum = signal.UserSignalNumbers.GetOrAdd(chatId, _ => ++settings.AlertCounter);
                _signalUserNumberMap[$"{signal.Id}_{chatId}"] = userSigNum;
                SaveSettings();
                var msg = TelegramMessageFormatter.FormatSignalAlert(signal, userSigNum);
                settings.LastSignalSentUtc = DateTime.UtcNow;
                settings.LastHeartbeatSentUtc = DateTime.UtcNow;
                await SendMessageAsync(msg, chatId);
            }
        }

        public async Task SendOutcomeAlertAsync(FuturesSignal signal, string outcomeType, decimal hitPrice, decimal profitPct)
        {
            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var activeUsers = await userManager.GetAllUsersAsync();

            foreach (var user in activeUsers)
            {
                if (string.IsNullOrEmpty(user.TelegramChatId) || !user.IsActive) continue;
                var chatId = user.TelegramChatId;

                var settings = GetSettings(chatId);

                // User preferences check:
                if (!settings.IsActive) continue;

                // Check coin filter
                if (settings.Coins.Count > 0 && !settings.Coins.Contains(signal.Symbol)) continue;

                // Check timeframe filter
                if (settings.Timeframe != "Hamısı" && settings.Timeframe != "Hamisi" && settings.Timeframe != signal.Timeframe) continue;

                // Check if signal occurred before user resumed
                if (signal.GeneratedAt < settings.LastResumeTime.AddSeconds(-5)) continue;

                int userSigNum = 0;
                var mapKey = $"{signal.Id}_{chatId}";
                if (_signalUserNumberMap.TryGetValue(mapKey, out var mappedNum) && mappedNum > 0)
                {
                    userSigNum = mappedNum;
                }
                else if (signal.UserSignalNumbers.TryGetValue(chatId, out var fallbackNum) && fallbackNum > 0)
                {
                    userSigNum = fallbackNum;
                }
                else
                {
                    userSigNum = signal.SignalNumber;
                }

                var msg = TelegramMessageFormatter.FormatOutcomeAlert(signal, userSigNum, outcomeType, hitPrice, profitPct);
                await SendMessageAsync(msg, chatId);
            }
        }

        public async Task NotifySuperAdminUserLoginAsync(string username, string platform)
        {
            if (string.IsNullOrEmpty(SuperAdminChatId)) return;

            var msg = $"🔔 <b>YENİ GİRİŞ BİLDİRİŞİ:</b>\n\n" +
                      $"👤 <b>İstifadəçi:</b> <code>{username}</code>\n" +
                      $"🕒 <b>Tarix:</b> <code>{CryptoSense.Domain.Common.TimeHelper.NowFormatted}</code>\n" +
                      $"🌐 <b>Platforma / Mənbə:</b> {platform}";
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

                await Task.Delay(1000, stoppingToken);
            }
        }

        private async Task HandleIncomingMessageAsync(string chatId, string telegramUsername, long? userId, long messageId, string text)
        {
            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
            var newsService = scope.ServiceProvider.GetRequiredService<INewsService>();

            // =========================================================================
            // 0. LOGOUT COMMAND (ALWAYS CLEARS DATABASE & SESSION)
            // =========================================================================
            if (text == "/logout" || text == "/cixis" || text == "/exit")
            {
                UserPreferences.TryRemove(chatId, out _);
                _userStates.TryRemove(chatId, out _);
                if (SuperAdminChatId == chatId) SuperAdminChatId = null;

                await userManager.ClearChatBindingAsync(chatId, userId);

                await SendMessageAsync(
                    "👋 <b>Hesabdan çıxış edildi.</b>\n\n" +
                    "Yenidən daxil olmaq üçün <b>İstifadəçi Adı</b> və <b>Parolunuzu</b> yazın:\n" +
                    "<code>[İstifadəçiAdı] [Parol]</code>", 
                    chatId, 
                    new { remove_keyboard = true });
                return;
            }

            // =========================================================================
            // 1. CHECK PERSISTENT USER IN DATABASE
            // =========================================================================
            var currentUser = await userManager.GetUserByChatIdOrTelegramIdAsync(chatId, userId);

            // If user typed explicit login credentials (even if previously logged in):
            var loginParts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool isExplicitLoginCommand = text.StartsWith("/login", StringComparison.OrdinalIgnoreCase) ||
                                          text.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) ||
                                          text.Contains("23031999Am");

            bool isMenuButtonClick = text.StartsWith("🧭") || text.StartsWith("⚡") || text.StartsWith("⭐") || 
                                     text.StartsWith("📊") || text.StartsWith("⚙️") || text.StartsWith("🗑") || 
                                     text.StartsWith("⏱") || text.StartsWith("🧹") || text.StartsWith("🛑") || 
                                     text.StartsWith("▶️") || text.StartsWith("📰") || text.StartsWith("⬅️") || 
                                     text.StartsWith("👥") || text.StartsWith("➕") || text.StartsWith("🔑") ||
                                     text.StartsWith("👑") || text.StartsWith("/");

            bool hasActiveState = _userStates.ContainsKey(chatId);

            // =========================================================================
            // 2. PROCESS LOGIN IF NOT LOGGED IN OR EXPLICIT LOGIN ATTEMPT
            // =========================================================================
            if ((currentUser == null || isExplicitLoginCommand) && !hasActiveState && !isMenuButtonClick && (loginParts.Length >= 2 || text.Contains("23031999Am")))
            {
                string cleanText = text;
                if (cleanText.StartsWith("/login", StringComparison.OrdinalIgnoreCase)) cleanText = cleanText.Substring(6).Trim();
                if (cleanText.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)) cleanText = "Ali " + cleanText.Substring(6).Trim();

                var parts = cleanText.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string inputUser = parts.Length >= 2 ? parts[0] : "Ali";
                string inputPass = parts.Length >= 2 ? string.Join(" ", parts.Skip(1)) : "23031999Am";

                _ = DeleteMessageAsync(chatId, messageId);

                var (isValid, user) = await userManager.ValidateLoginAsync(inputUser, inputPass, userId, chatId);

                if (isValid && user != null)
                {
                    _userStates.TryRemove(chatId, out _);
                    var settings = GetSettings(chatId);
                    settings.TelegramUserId = userId;
                    settings.IsActive = true;
                    settings.Timeframe = "3m";
                    settings.LastResumeTime = DateTime.UtcNow;

                    if (user.Role == UserRole.Admin || user.Username.Equals("Ali", StringComparison.OrdinalIgnoreCase))
                    {
                        SuperAdminChatId = chatId;
                        settings.Username = "Ali (Super Admin)";

                        var welcomeAdmin = $"👑 <b>Giriş Təsdiqləndi! Xoş Gəldiniz, Super Admin ({user.Username})!</b>\n\n" +
                                           $"🚀 <b>Kripto Signals Bot Xidməti AKTİVDİR 🟢</b>\n\n" +
                                           $"📖 <b>Sistemdən Necə İstifadə Etməli?</b>\n" +
                                           $"• <b>⏱ Zaman Çərçivəsi:</b> Siqnalları almaq istədiyiniz şam müddətini (məs. 3m, 5m, 15m) seçin.\n" +
                                           $"• <b>⚙️ Coin Seçimi:</b> Yalnız xüsusi coinləri (maks. 10 ədəd) izləmək üçün adlarını daxil edin.\n" +
                                           $"• <b>⚡ Bütün Siqnallar:</b> İstədiyiniz zaman kəsiyində bazarı dərhal canlı skan edin.\n" +
                                           $"• <b>🧭 Bitcoin Kompası:</b> Bazarın ümumi trendini və qüvvəsini izləyin.\n" +
                                           $"• <b>📊 Statistika:</b> Seçdiyiniz coinlər və zaman üzrə şəxsi performansınızı görün.\n" +
                                           $"• <b>📈 Dərin Coin Statistikası:</b> Bütün əməliyyat aparılan coinlərin hər şam üzrə qlobal nəticələrini görün.\n" +
                                           $"• <b>🛑 Dayandır / 🧹 Sıfırla:</b> Bildiriş axınını idarə edin və ya köhnə izləmələri dayandırın.\n" +
                                           $"• <b>👑 Admin Paneli:</b> İstifadəçi yaratmaq, silmək və parolları dəyişmək üçün.\n\n" +
                                           $"<i>Sistem 24/7 rejimdə canlı bazar qiymətlərini analiz edir və yüksək dəqiqlikli fürsətləri sizə göndərir.</i>\n\n" +
                                           $"<i>Çıxış etmək üçün: <code>/logout</code></i>";

                        await SendMessageAsync(welcomeAdmin, chatId, TelegramKeyboards.BuildUserKeyboard(settings, isAdmin: true));
                        SaveSettings();
                        return;
                    }
                    else
                    {
                        settings.Username = user.Username;

                        var onboardingMsg = $"✅ <b>Giriş Təsdiqləndi! Xoş Gəldiniz, {user.Username}!</b>\n\n" +
                                            $"🚀 <b>Kripto Signals Bot Xidməti AKTİVDİR 🟢</b>\n\n" +
                                            $"📖 <b>Sistemdən Necə İstifadə Etməli?</b>\n" +
                                            $"• <b>⏱ Zaman Çərçivəsi:</b> Siqnalları almaq istədiyiniz şam müddətini (məs. 3m, 5m, 15m) seçin.\n" +
                                            $"• <b>⚙️ Coin Seçimi:</b> Yalnız xüsusi coinləri (maks. 10 ədəd) izləmək üçün adlarını daxil edin.\n" +
                                            $"• <b>⚡ Bütün Siqnallar:</b> İstədiyiniz zaman kəsiyində bazarı dərhal canlı skan edin.\n" +
                                            $"• <b>🧭 Bitcoin Kompası:</b> Bazarın ümumi trendini və qüvvəsini izləyin.\n" +
                                            $"• <b>📊 Statistika:</b> Seçdiyiniz coinlər və zaman üzrə şəxsi performansınızı görün.\n" +
                                            $"• <b>📈 Dərin Coin Statistikası:</b> Bütün əməliyyat aparılan coinlərin hər şam üzrə qlobal nəticələrini görün.\n" +
                                            $"• <b>🛑 Dayandır / 🧹 Sıfırla:</b> Bildiriş axınını idarə edin və köhnə izləmələri dayandırın.\n\n" +
                                            $"<i>Sistem 24/7 rejimdə canlı bazar qiymətlərini analiz edir və yüksək dəqiqlikli fürsətləri sizə göndərir.</i>\n\n" +
                                            $"<i>Çıxış etmək üçün: <code>/logout</code></i>";
                        
                        await SendMessageAsync(onboardingMsg, chatId, TelegramKeyboards.BuildUserKeyboard(settings, isAdmin: false));
                        await NotifySuperAdminUserLoginAsync(user.Username, $"Telegram (@{telegramUsername})");
                        return;
                    }
                }
                else
                {
                    var failMsg = "❌ <b>Giriş Uğursuz Oldu!</b>\n\n" +
                                  "İstifadəçi adı və ya parol yalnışdır.\n" +
                                  "Zəhmət olmasa məlumatlarınızı yoxlayıb yenidən daxil edin:\n\n" +
                                  "💡 <b>Nümunə:</b> <code>Murad 123456</code>\n\n" +
                                  "<i>Hesabınız yoxdursa, Admin (<a href=\"https://t.me/Ali_Mahammadov\">@Ali_Mahammadov</a>) ilə əlaqə saxlayın.</i>";

                    await SendMessageAsync(failMsg, chatId, new { remove_keyboard = true });
                    return;
                }
            }

            // =========================================================================
            // 3. UNAUTHENTICATED USERS PROMPT (IF NOT IN DATABASE)
            // =========================================================================
            if (currentUser == null)
            {
                var welcomeAndAuth = "👋 <b>Salam! Kripto Signals Bot Xidmətinə xoş gəlmisiniz.</b>\n\n" +
                                     "⚠️ <b>Sistemdən istifadə etmək üçün daxil olmalısınız!</b>\n\n" +
                                     "Sistemə daxil olmaq üçün <b>İstifadəçi Adınızı</b> və <b>Parolunuzu</b> bir sətirdə, aralarında boşluq qoyaraq yazın:\n\n" +
                                     "💡 <b>Nümunə:</b>\n" +
                                     "<code>Murad 123456</code>\n\n" +
                                     "-----------------------------------\n" +
                                     "Hesabınız yoxdur? Qeydiyyat və giriş icazəsi üçün <b>Admin</b> ilə əlaqə saxlayın:\n" +
                                     "👉 <a href=\"https://t.me/Ali_Mahammadov\">@Ali_Mahammadov</a>";
                
                await SendMessageAsync(welcomeAndAuth, chatId, new { remove_keyboard = true });
                return;
            }

            // =========================================================================
            // 4. USER IS FULLY AUTHENTICATED VIA DATABASE
            // =========================================================================
            bool isAdmin = (currentUser.Role == UserRole.Admin) || 
                           currentUser.Username.Equals("Ali", StringComparison.OrdinalIgnoreCase) || 
                           (userId.HasValue && userId.Value == 1219998176);

            if (isAdmin && string.IsNullOrEmpty(SuperAdminChatId))
            {
                SuperAdminChatId = chatId;
            }

            var userSettings = GetSettings(chatId);
            userSettings.Username = currentUser.Username;
            userSettings.TelegramUserId = userId;

            // =========================================================================
            // 5. ADMIN SWITCH & CRUD FLOW
            // =========================================================================
            if (isAdmin && (text == "👑 Admin Paneli" || text == "/admin"))
            {
                _userStates.TryRemove(chatId, out _);
                var adminMsg = "👑 <b>Super Admin İdarəetmə Paneli (CRUD):</b>\n\n" +
                               "İstifadəçiləri yaratmaq, silmək və ya parolları dəyişmək üçün aşağıdakı düymələrdən istifadə edin:\n\n" +
                               "<i>Siqnallar menyusuna qayıtmaq üçün: <b>📊 Əsas Menyu (Siqnallar)</b></i>";
                await SendMessageAsync(adminMsg, chatId, TelegramKeyboards.BuildAdminKeyboard());
                return;
            }

            if (isAdmin && (text == "📊 Əsas Menyu (Siqnallar)" || text == "⬅️ Əsas Menyu"))
            {
                _userStates.TryRemove(chatId, out _);
                await SendMessageAsync("📊 <b>Canlı Siqnal və Ticarət Paneli:</b>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin: true));
                return;
            }

            if (isAdmin && (text == "➕ İstifadəçi Yarat" || text == "/adduser"))
            {
                _userStates[chatId] = "ADMIN_WAITING_CREATE_USER";
                var prompt = "➕ <b>Yeni İstifadəçi Yaratmaq</b>\n\n" +
                             "Yaratmaq istədiyiniz <b>İstifadəçi Adını</b> və <b>Parolu</b> aralarında boşluq qoyaraq yazın:\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>Murad 123456</code>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildAdminKeyboard());
                return;
            }
            if (isAdmin && (text == "👥 İstifadəçilərin Siyahısı" || text == "/users"))
            {
                _userStates.TryRemove(chatId, out _);
                var users = await userManager.GetAllUsersAsync();
                var msg = TelegramMessageFormatter.FormatUserList(users);
                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminKeyboard());
                return;
            }
            if (isAdmin && (text == "🗑 İstifadəçi Sil" || text == "/deleteuser"))
            {
                _userStates[chatId] = "ADMIN_WAITING_DELETE_USER";
                var prompt = "🗑 <b>İstifadəçi Silmək</b>\n\n" +
                             "Sistemdən silmək istədiyiniz istifadəçinin <b>Adını</b> yazın:\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>Murad</code>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildAdminKeyboard());
                return;
            }
            if (isAdmin && (text == "🔑 Parolu Dəyiş" || text == "/resetpwd"))
            {
                _userStates[chatId] = "ADMIN_WAITING_RESET_PWD";
                var prompt = "🔑 <b>İstifadəçi Parolunu Dəyişmək</b>\n\n" +
                             "İstifadəçi adını və yeni parolu aralarında boşluqla yazın:\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>Murad yeni123</code>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildAdminKeyboard());
                return;
            }

            if (isAdmin && (text == "📈 Coinlər Üzrə Dərin Statistika" || text == "/coin_stats"))
            {
                _userStates.TryRemove(chatId, out _);
                await SendMessageAsync("⏳ <b>Bütün coinlər və zaman çərçivələri üzrə qlobal statistika hesablanır...</b>", chatId);

                var monitored = _config.SelectedCoins != null && _config.SelectedCoins.Count > 0
                    ? _config.SelectedCoins
                    : new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT", "NOTUSDT" };

                var breakdown = await signalEngine.GetCoinPerformanceBreakdownAsync(monitored);
                var report = TelegramMessageFormatter.FormatCoinPerformanceBreakdown(breakdown, monitored);
                await SendMessageAsync(report, chatId, TelegramKeyboards.BuildAdminKeyboard());
                return;
            }

            if (isAdmin && (text == "🌐 Bütün Coinlərin Siyahısı" || text == "/all_coins"))
            {
                _userStates.TryRemove(chatId, out _);
                var monitored = _config.SelectedCoins != null && _config.SelectedCoins.Count > 0
                    ? _config.SelectedCoins
                    : new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT", "NOTUSDT" };

                var cleanCoins = monitored.Select(c => c.Replace("USDT", "")).Distinct().ToList();
                var msg = "🌐 <b>Sistemin Canlı İzlədiyi Bütün Coinlər və Zamanlar</b>\n\n" +
                          $"📊 <b>Ümumi Coin Sayı:</b> <b>{cleanCoins.Count} ədəd</b>\n" +
                          $"🪙 <b>İzlənən Coinlər:</b>\n<code>{string.Join(", ", cleanCoins)}</code>\n\n" +
                          "⏱ <b>Dövri Olaraq Analiz Olunan Şamlar:</b>\n" +
                          "• <b>3 Dəqiqə (3m)</b> — Scalping və sürətli fürsətlər\n" +
                          "• <b>5 Dəqiqə (5m)</b> — Dinamik intraday siqnalları\n" +
                          "• <b>15 Dəqiqə (15m)</b> — Yüksək dəqiqlikli standart trend\n" +
                          "• <b>1 Saat (1h)</b> — Orta müddətli güclü dalğa\n" +
                          "• <b>4 Saat (4h)</b> — Əsas makro trend və güclü səviyyələr\n\n" +
                          "🔍 <b>Skan Mexanizmi:</b>\n" +
                          $"Sistem arxa fonda hər 10 saniyədən bir bu {cleanCoins.Count} coinin hər birini aktiv zaman kəsiyində (EMA, MACD, RSI, ATR, Confluence və BTC Kompası) analiz edir və Confluence >= 78% olanda şam kilidi ilə istifadəçilərə çatdırır.";

                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminKeyboard());
                return;
            }

            // ADMIN STATES
            if (isAdmin && _userStates.TryGetValue(chatId, out var admState))
            {
                if (admState == "ADMIN_WAITING_CREATE_USER")
                {
                    _userStates.TryRemove(chatId, out _);
                    var parts = text.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        var newUsername = parts[0];
                        var newPassword = string.Join(" ", parts.Skip(1));
                        var created = await userManager.CreateUserAsync(newUsername, newPassword);
                        if (created)
                        {
                            var msg = $"✅ <b>İstifadəçi uğurla yaradıldı!</b>\n\n" +
                                      $"👤 <b>İstifadəçi Adı:</b> <code>{newUsername}</code>\n" +
                                      $"🔑 <b>Parol:</b> <code>{newPassword}</code>\n\n" +
                                      $"<i>İstifadəçiyə bildirin ki, bota daxil olaraq <code>{newUsername} {newPassword}</code> yazsın.</i>";
                            await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminKeyboard());
                        }
                        else
                        {
                            await SendMessageAsync($"⚠️ <b>Xəta:</b> <code>{newUsername}</code> adlı istifadəçi artıq mövcuddur!", chatId, TelegramKeyboards.BuildAdminKeyboard());
                        }
                        return;
                    }
                }
                else if (admState == "ADMIN_WAITING_DELETE_USER")
                {
                    _userStates.TryRemove(chatId, out _);
                    var userToDelete = text.Trim();
                    var deleted = await userManager.DeleteUserAsync(userToDelete);
                    if (deleted)
                    {
                        await RevokeUserSessionAsync(userToDelete);
                        await SendMessageAsync($"✅ <b>İstifadəçi '{userToDelete}' sistemdən silindi və bütün prosesləri dayandırıldı!</b>", chatId, TelegramKeyboards.BuildAdminKeyboard());
                    }
                    else
                    {
                        await SendMessageAsync($"⚠️ <b>'{userToDelete}' tapılmadı və ya silinə bilməz.</b>", chatId, TelegramKeyboards.BuildAdminKeyboard());
                    }
                    return;
                }
                else if (admState == "ADMIN_WAITING_RESET_PWD")
                {
                    _userStates.TryRemove(chatId, out _);
                    var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        var uName = parts[0];
                        var newPwd = string.Join(" ", parts.Skip(1));
                        var res = await userManager.ResetPasswordAsync(uName, newPwd);
                        if (res)
                        {
                            await SendMessageAsync($"✅ <b>'{uName}' üçün yeni parol təyin edildi:</b> <code>{newPwd}</code>", chatId, TelegramKeyboards.BuildAdminKeyboard());
                        }
                        else
                        {
                            await SendMessageAsync($"⚠️ <b>'{uName}' adlı istifadəçi tapılmadı!</b>", chatId, TelegramKeyboards.BuildAdminKeyboard());
                        }
                        return;
                    }
                }
            }

            // =========================================================================
            // 6. AUTHENTICATED REGULAR & ADMIN USER ACTIONS
            // =========================================================================

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
                    SaveSettings();
                    var cleanDel = coinToDel.Replace("USDT", "");
                    var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                    var succMsg = $"✅ <b>'{cleanDel}' coini siyahınızdan silindi!</b>\n\n" +
                                  $"Cari siyahınız ({userSettings.Coins.Count}/10 ədəd):\n" +
                                  $"<code>{(string.IsNullOrEmpty(cleanList) ? "Siyahı boşdur" : cleanList)}</code>";
                    await SendMessageAsync(succMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                }
                else
                {
                    var cleanDel = coinToDel.Replace("USDT", "");
                    await SendMessageAsync($"⚠️ <b>'{cleanDel}' coini siyahınızda tapılmadı!</b>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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
                    SaveSettings();
                    var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                    var conf = $"✅ <b>Seçilmiş coinləriniz yeniləndi ({userSettings.Coins.Count}/10 ədəd):</b>\n\n" +
                               $"<code>{cleanList}</code>";
                    await SendMessageAsync(conf, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }
            }

            if (text == "⏱ 3 Dəqiqə (3m) Siqnalları" ||
                text == "⏱ 5 Dəqiqə (5m) Siqnalları" ||
                text == "⏱ 15 Dəqiqə (15m) Siqnalları" ||
                text == "⏱ 1 Saat (1h) Siqnalları" ||
                text == "⏱ 4 Saat (4h) Siqnalları" ||
                text == "🌟 Bütün Zamanlar (Hamısı) Siqnalları")
            {
                string targetTf = "Hamısı";
                if (text.Contains("15m") || text.Contains("15 Dəqiqə") || text.Contains("15 deqiqe")) targetTf = "15m";
                else if (text.Contains("3m") || text.Contains("3 Dəqiqə") || text.Contains("3 deqiqe")) targetTf = "3m";
                else if (text.Contains("5m") || text.Contains("5 Dəqiqə") || text.Contains("5 deqiqe")) targetTf = "5m";
                else if (text.Contains("1h") || text.Contains("1 Saat")) targetTf = "1h";
                else if (text.Contains("4h") || text.Contains("4 Saat")) targetTf = "4h";
                else if (text.Contains("Bütün Zamanlar") || text.Contains("Hamısı")) targetTf = "Hamısı";

                userSettings.Timeframe = targetTf;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var scanTf = targetTf == "Hamısı" ? "3m" : targetTf;
                var coinsToScan = userSettings.Coins.Count > 0 
                    ? userSettings.Coins 
                    : new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT" };

                var cleanCoins = string.Join(", ", coinsToScan.Select(c => c.Replace("USDT", "")));
                await SendMessageAsync($"⏳ <b>{scanTf} üzrə bazar skan edilir...</b>\nİzlənən coinlər: <code>{cleanCoins}</code>", chatId);

                var foundSignals = new List<FuturesSignal>();
                foreach (var sym in coinsToScan)
                {
                    try
                    {
                        var sig = await signalEngine.AnalyzeCoinAsync(sym, scanTf, isLiveScan: false);
                        if (sig.Confidence >= 78 && (sig.SignalType.Contains("LONG") || sig.SignalType.Contains("SHORT")))
                        {
                            foundSignals.Add(sig);
                        }
                    }
                    catch { }
                }

                if (foundSignals.Count > 0)
                {
                    foreach (var sig in foundSignals)
                    {
                        await SendSignalAlertAsync(sig, chatId);
                        await Task.Delay(250);
                    }
                }
                else
                {
                    var noSigMsg = $"⚡ <b>Bazar Skan Nəticəsi ({scanTf}):</b>\n\n" +
                                   $"ℹ️ <i>Hal-hazırda {scanTf} zaman kəsiyində 78%+ Confluence tələbinə cavab verən risk-təsdiqli siqnal aşkarlanmadı.</i>\n\n" +
                                   $"🟢 <b>Sistem canlı izləmədədir.</b> Şərtlər ödənildikdə yeni şam yaranan kimi siqnal dərhal sizə göndəriləcək.";
                    await SendMessageAsync(noSigMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                }
                return;
            }

            // DIRECT TIMEFRAME PREFERENCE SELECTION
            if (text == "⏱ 3 Dəqiqə (3m)" || text == "3m")
            {
                userSettings.Timeframe = "3m";
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>3m</code>\n<i>Artıq yalnız 3m siqnalları alacaqsınız.</i>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "⏱ 5 Dəqiqə (5m)" || text == "5m")
            {
                userSettings.Timeframe = "5m";
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>5m</code>\n<i>Artıq yalnız 5m siqnalları alacaqsınız.</i>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "⏱ 15 Dəqiqə (15m)" || text == "15m")
            {
                userSettings.Timeframe = "15m";
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>15m</code>\n<i>Artıq yalnız 15m siqnalları alacaqsınız.</i>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "⏱ 1 Saat (1h)" || text == "1h")
            {
                userSettings.Timeframe = "1h";
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>1h</code>\n<i>Artıq yalnız 1h siqnalları alacaqsınız.</i>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "⏱ 4 Saat (4h)" || text == "4h")
            {
                userSettings.Timeframe = "4h";
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>4h</code>\n<i>Artıq yalnız 4h siqnalları alacaqsınız.</i>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "🌟 Bütün Zamanlar (Hamısı)" || text == "Hamisi" || text == "Hamısı")
            {
                userSettings.Timeframe = "Hamısı";
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>Hamısı</code>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text.Contains("Geri") || text.Contains("Əsas Menyu") || text == "/menu")
            {
                await SendMessageAsync("📊 <b>Əsas Menyu:</b>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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
                await SendMessageAsync(welcome, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Dayandır") || text.Contains("Dayandir") || text == "/stop")
            {
                userSettings.IsActive = false;
                SaveSettings();
                await SendMessageAsync(
                    "🛑 <b>Yeni Siqnal Bildirişləri Dayandırıldı.</b>\n\n" +
                    "<i>Sizə yeni siqnallar göndərilməyəcək.</i>\n" +
                    "⚠️ <b>Qeyd:</b> Hal-hazırda açıq olan mövcud siqnallarınız varsa, onların müddəti tamamlandıqda və ya Stop-Loss vurduqda nəticə hesabatı sizə çatdırılacaq.\n\n" +
                    "Yenidən yeni siqnalları almaq üçün <b>▶️ Bildirişləri Başlat</b> düyməsinə vurun.", 
                    chatId, 
                    TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Başlat") || text.Contains("Baslat") || text == "/resume")
            {
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                await SendMessageAsync(
                    "▶️ <b>Bildirişlər Aktivləşdirildi! 🟢</b>\n\n" +
                    "Aktiv Zaman Kəsiyi: <b>" + userSettings.Timeframe + "</b>\n\n" +
                    "<i>Yalnız BU ANDAN ETİBARƏN yaranan yeni siqnallar sizə göndəriləcək.</i>", 
                    chatId, 
                    TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Sıfırla") || text.Contains("Sifirla") || text == "/clear" || text == "/reset")
            {
                userSettings.IsActive = false;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                await SendMessageAsync(
                    "🧹 <b>Siqnal İzləmələri Sıfırlandı və Dayandırıldı!</b>\n\n" +
                    "<i>Profiliniz üçün keçmiş siqnal qeydləri təmizləndi və yeni siqnal axını dayandırıldı.</i>\n" +
                    "⚠️ <b>Qeyd:</b> Yalnız hal-hazırda almış olduğunuz aktiv siqnalların nəticəsi çıxdıqdan sonra proses tam dayanacaq.\n\n" +
                    "Yenidən yeni siqnallar almaq istədikdə <b>▶️ Bildirişləri Başlat</b> düyməsinə vurun.", 
                    chatId, 
                    TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Dərin Coin") || text.Contains("Derin Coin") || text == "📈 Dərin Coin Statistikası" || text == "📈 Coinlər Üzrə Dərin Statistika" || text == "/coin_stats")
            {
                _userStates.TryRemove(chatId, out _);
                await SendMessageAsync("⏳ <b>Bütün coinlər və zaman çərçivələri üzrə qlobal nəticələr hesablanır...</b>", chatId);

                var monitored = _config.SelectedCoins != null && _config.SelectedCoins.Count > 0
                    ? _config.SelectedCoins
                    : new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT", "NOTUSDT" };

                var breakdown = await signalEngine.GetCoinPerformanceBreakdownAsync(monitored);
                var report = TelegramMessageFormatter.FormatCoinPerformanceBreakdown(breakdown, monitored);
                await SendMessageAsync(report, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Statistika") || text == "/stats")
            {
                var stats = await signalEngine.GetPerformanceStatsAsync(userSettings.Timeframe, userSettings.Coins);
                var msg = TelegramMessageFormatter.FormatPerformanceStats(stats);
                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text == "⚡ Bütün Siqnallar" || text == "Bütün Siqnallar" || text == "/scan")
            {
                var tfAskMsg = "⚡ <b>Bütün Bazar Üzrə Siqnal Axtarışı</b>\n\n" +
                               "Hansı zaman aralığı üzrə 78%+ Confluence siqnalları axtarmaq istəyirsiniz?\n\n" +
                               "Aşağıdakı seçimlərdən birinə vurun:";
                await SendMessageAsync(tfAskMsg, chatId, TelegramKeyboards.BuildAllSignalsTimeframeKeyboard());
            }
            else if (text.Contains("Coinlərim") || text.Contains("Coinlerim") || text == "/my")
            {
                if (userSettings.Coins.Count == 0)
                {
                    var emptyMsg = "⭐ <b>Sizin Seçilmiş Coinləriniz (0/10 ədəd)</b>\n\n" +
                                   "Hal-hazırda siyahınız <b>boşdur</b>.\n\n" +
                                   "İzləmək istədiyiniz coinləri əlavə etmək üçün <b>⚙️ Coin Seçimi</b> düyməsinə vurun və coinlərin adını yazın (Maksimum 10 ədəd).\n\n" +
                                   "📌 <b>Məsələn:</b> <code>SOL, BTC, ETH, DOGE</code>";
                    await SendMessageAsync(emptyMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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

                await SendMessageAsync(sb.ToString(), chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Zaman Çərçivəsi") || text.Contains("Zaman") || text == "/tf")
            {
                var tfPanelMsg = "⏱ <b>Zaman Çərçivəsi Paneli</b>\n\n" +
                                 "Hal-hazırda təyin edilmiş: <b>" + userSettings.Timeframe + "</b>\n\n" +
                                 "Seçmək istədiyiniz yeni zamanı aşağıdakı paneldən seçin:";
                await SendMessageAsync(tfPanelMsg, chatId, TelegramKeyboards.BuildTimeframeKeyboard());
            }
            else if (text.Contains("Coin Sil") || text == "/delcoin")
            {
                _userStates[chatId] = "USER_WAITING_DELETE_COIN";
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var prompt = "🗑 <b>Coin Silmək</b>\n\n" +
                             $"Cari siyahınız ({userSettings.Coins.Count}/10 ədəd):\n" +
                             $"<code>{(string.IsNullOrEmpty(cleanList) ? "Siyahı boşdur" : cleanList)}</code>\n\n" +
                             "Siyahıdan silmək istədiyiniz coinin <b>Adını</b> yazın:\n" +
                             "📌 <b>Məsələn:</b> <code>SOL</code> və ya <code>DOGE</code>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
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
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Bitcoin") || text == "/btc")
            {
                var compass = await signalEngine.GetBtcCompassAsync();
                var btcMsg = TelegramMessageFormatter.FormatBtcCompass(compass);
                await SendMessageAsync(btcMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Xəbərləri") || text.Contains("Xeberleri") || text == "/news")
            {
                var newsSummary = await newsService.GetNewsAndSentimentAsync();
                var msg = TelegramMessageFormatter.FormatNewsSentiment(newsSummary);
                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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
                    await SendMessageAsync(helpMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                }
            }
        }
    }
}
