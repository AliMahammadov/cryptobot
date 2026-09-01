using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        public static HashSet<string> AuthenticatedChats { get; } = new();
        public static HashSet<string> AdminChats { get; } = new();
        public static ConcurrentDictionary<string, UserSettings> UserPreferences { get; } = new();
        private static readonly ConcurrentDictionary<string, string> _userStates = new();

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
                AdminChats.Remove(chatId);
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
                var msg = TelegramMessageFormatter.FormatSignalAlert(signal, userSigNum);
                await SendMessageAsync(msg, specificChatId);
                return;
            }

            foreach (var chatId in AuthenticatedChats)
            {
                var settings = GetSettings(chatId);
                if (!settings.IsActive) continue;

                // Strict Timeframe check
                if (settings.Timeframe != "Hamısı" && settings.Timeframe != "Hamisi" && settings.Timeframe != signal.Timeframe)
                {
                    continue;
                }

                // Coin filter check
                if (settings.Coins.Count > 0 && !settings.Coins.Contains(signal.Symbol)) continue;

                var userSigNum = signal.UserSignalNumbers.GetOrAdd(chatId, _ => ++settings.AlertCounter);
                var msg = TelegramMessageFormatter.FormatSignalAlert(signal, userSigNum);
                await SendMessageAsync(msg, chatId);
            }
        }

        public async Task SendOutcomeAlertAsync(FuturesSignal signal, string outcomeType, decimal hitPrice, decimal profitPct)
        {
            foreach (var chatId in AuthenticatedChats)
            {
                var settings = GetSettings(chatId);
                if (!settings.IsActive) continue;

                bool receivedThisSignal = signal.UserSignalNumbers.ContainsKey(chatId);

                if (!receivedThisSignal)
                {
                    if (settings.Timeframe != "Hamısı" && settings.Timeframe != "Hamisi" && settings.Timeframe != signal.Timeframe)
                    {
                        continue;
                    }

                    if (settings.Coins.Count > 0 && !settings.Coins.Contains(signal.Symbol)) continue;
                }

                signal.UserSignalNumbers.TryGetValue(chatId, out var userSigNum);
                if (userSigNum == 0) userSigNum = signal.SignalNumber;

                var msg = TelegramMessageFormatter.FormatOutcomeAlert(signal, userSigNum, outcomeType, hitPrice, profitPct);
                await SendMessageAsync(msg, chatId);
            }
        }

        public async Task NotifySuperAdminUserLoginAsync(string username, string platform)
        {
            if (string.IsNullOrEmpty(SuperAdminChatId)) return;

            var msg = $"🔔 <b>YENİ GİRİŞ BİLDİRİŞİ:</b>\n\n" +
                      $"👤 <b>İstifadəçi:</b> <code>{username}</code>\n" +
                      $"🕒 <b>Tarix:</b> <code>{DateTime.Now:dd.MM.yyyy | HH:mm:ss}</code>\n" +
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

                await Task.Delay(1500, stoppingToken);
            }
        }

        private async Task HandleIncomingMessageAsync(string chatId, string username, long? userId, long messageId, string text)
        {
            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
            var newsService = scope.ServiceProvider.GetRequiredService<INewsService>();

            // =========================================================================
            // 0. LOGOUT COMMAND (ALWAYS AVAILABLE)
            // =========================================================================
            if (text == "/logout" || text == "/cixis" || text == "/exit")
            {
                AdminChats.Remove(chatId);
                AuthenticatedChats.Remove(chatId);
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

            bool isAlreadyLoggedIn = AdminChats.Contains(chatId) || AuthenticatedChats.Contains(chatId);
            bool isAdmin = AdminChats.Contains(chatId);

            // =========================================================================
            // 1. EXPLICIT LOGIN ATTEMPTS (ONLY IF NOT LOGGED IN OR EXPLICIT /login)
            // =========================================================================
            string cleanLoginText = text;
            if (cleanLoginText.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
            {
                cleanLoginText = cleanLoginText.Substring(6).Trim();
            }
            if (cleanLoginText.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
            {
                cleanLoginText = "Ali " + cleanLoginText.Substring(6).Trim();
            }

            var loginParts = cleanLoginText.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            
            bool isMenuButtonClick = text.StartsWith("🧭") || text.StartsWith("⚡") || text.StartsWith("⭐") || 
                                     text.StartsWith("📊") || text.StartsWith("⚙️") || text.StartsWith("🗑") || 
                                     text.StartsWith("⏱") || text.StartsWith("🧹") || text.StartsWith("🛑") || 
                                     text.StartsWith("▶️") || text.StartsWith("📰") || text.StartsWith("⬅️") || 
                                     text.StartsWith("👥") || text.StartsWith("➕") || text.StartsWith("🔑") ||
                                     text.StartsWith("👑") || text.StartsWith("/");

            bool isExplicitLoginCommand = text.StartsWith("/login", StringComparison.OrdinalIgnoreCase) || 
                                         text.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) || 
                                         text.Contains("23031999Am");

            bool hasActiveState = _userStates.ContainsKey(chatId);

            if ((!isAlreadyLoggedIn || isExplicitLoginCommand) && !hasActiveState && !isMenuButtonClick && (loginParts.Length >= 2 || text.Contains("23031999Am")))
            {
                string inputUser = loginParts.Length >= 2 ? loginParts[0] : "Ali";
                string inputPass = loginParts.Length >= 2 ? string.Join(" ", loginParts.Skip(1)) : "23031999Am";

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

                    if (user.Role == UserRole.Admin)
                    {
                        AdminChats.Add(chatId);
                        AuthenticatedChats.Add(chatId); // Super Admin can also receive alerts and test all functions!
                        SuperAdminChatId = chatId;
                        settings.Username = "Ali (Super Admin)";

                        var welcomeAdmin = "👑 <b>Super Admin Paneli və Canlı Ticarət Sistemi AKTİVDİR 🟢</b>\n\n" +
                                           "👤 Rol: <b>Super Admin</b>\n" +
                                           "Aktiv Zaman Çərçivəsi: <b>3m</b>\n\n" +
                                           "Həm istifadəçiləri idarə edə (<b>👑 Admin Paneli</b> düyməsi ilə), həm də canlı siqnalları və kompası izləyə bilərsiniz.\n\n" +
                                           "<i>Çıxış üçün: <code>/logout</code></i>";

                        await SendMessageAsync(welcomeAdmin, chatId, TelegramKeyboards.BuildUserKeyboard(settings, isAdmin: true));
                        return;
                    }
                    else
                    {
                        AuthenticatedChats.Add(chatId);
                        AdminChats.Remove(chatId);
                        settings.Username = user.Username;

                        var onboardingMsg = $"✅ <b>Giriş Təsdiqləndi! Xoş Gəldiniz, {user.Username}!</b>\n\n" +
                                            $"🚀 <b>KriptoBot v2 Kvantitativ Ticarət Sistemi AKTİVDİR 🟢</b>\n\n" +
                                            $"Aktiv Zaman Çərçivəsi: <b>{settings.Timeframe}</b>\n\n" +
                                            $"Yalnız seçdiyiniz <b>{settings.Timeframe}</b> zamanı üzrə 24/7 siqnallar və nəticələr göndəriləcək.\n\n" +
                                            $"<i>Çıxış etmək üçün: <code>/logout</code></i>";
                        
                        await SendMessageAsync(onboardingMsg, chatId, TelegramKeyboards.BuildUserKeyboard(settings, isAdmin: false));
                        await NotifySuperAdminUserLoginAsync(user.Username, $"Telegram (@{username})");
                        return;
                    }
                }
                else
                {
                    var failMsg = "❌ <b>Giriş Uğursuz Oldu!</b>\n\n" +
                                  "İstifadəçi adı və ya parol yalnışdır.\n" +
                                  "Zəhmət olmasa məlumatlarınızı yoxlayıb yenidən daxil edin:\n\n" +
                                  "📌 <b>Format:</b> <code>[İstifadəçiAdı] [Parol]</code>\n" +
                                  "💡 <b>Nümunə:</b> <code>Murad 123456</code>\n\n" +
                                  "<i>Hesabınız yoxdursa, Super Admin (<a href=\"https://t.me/Ali_Mahammadov\">@Ali_Mahammadov</a>) ilə əlaqə saxlayın.</i>";

                    await SendMessageAsync(failMsg, chatId, new { remove_keyboard = true });
                    return;
                }
            }

            // =========================================================================
            // 2. UNAUTHENTICATED USERS PROMPT
            // =========================================================================
            if (!isAlreadyLoggedIn)
            {
                var welcomeAndAuth = "👋 <b>Salam! KriptoBot Xidmətinə xoş gəlmisiniz.</b>\n\n" +
                                     "⚠️ <b>Sistemdən istifadə etmək üçün daxil olmalısınız!</b>\n\n" +
                                     "Sistemə daxil olmaq üçün <b>İstifadəçi Adınızı</b> və <b>Parolunuzu</b> bir sətirdə, aralarında boşluq qoyaraq yazın:\n\n" +
                                     "📌 <b>Düzgün Format:</b>\n" +
                                     "<code>[İstifadəçiAdı] [Parol]</code>\n\n" +
                                     "💡 <b>Nümunə:</b>\n" +
                                     "<code>Murad 123456</code>\n\n" +
                                     "-----------------------------------\n" +
                                     "Hesabınız yoxdur? Qeydiyyat və giriş icazəsi üçün <b>Super Admin</b> ilə əlaqə saxlayın:\n" +
                                     "👉 <a href=\"https://t.me/Ali_Mahammadov\">@Ali_Mahammadov</a>";
                
                await SendMessageAsync(welcomeAndAuth, chatId, new { remove_keyboard = true });
                return;
            }

            var userSettings = GetSettings(chatId);

            // =========================================================================
            // 3. ADMIN SWITCH & CRUD FLOW
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
            // 4. AUTHENTICATED REGULAR & ADMIN USER ACTIONS
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
                    var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                    var conf = $"✅ <b>Seçilmiş coinləriniz yeniləndi ({userSettings.Coins.Count}/10 ədəd):</b>\n\n" +
                               $"<code>{cleanList}</code>";
                    await SendMessageAsync(conf, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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
                    await SendMessageAsync("⏳ <b>Bütün zaman çərçivələri (3m, 5m, 15m, 1h, 4h) üzrə bazar skan edilir...</b>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    var tfs = new[] { "3m", "5m", "15m", "1h", "4h" };
                    foreach (var tf in tfs)
                    {
                        foreach (var sym in sampleCoins.Take(3))
                        {
                            var sig = await signalEngine.AnalyzeCoinAsync(sym, tf);
                            if (sig.Confidence >= 78 && (sig.SignalType.Contains("LONG") || sig.SignalType.Contains("SHORT")))
                            {
                                await SendSignalAlertAsync(sig, chatId);
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

                    await SendMessageAsync($"⏳ <b>Yalnız ({targetTf}) üzrə 78%+ Confluence siqnalları axtarılır... (Profiliniz '{targetTf}' olaraq təyin edildi)</b>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));

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
                        await SendMessageAsync($"ℹ️ Hal-hazırda {targetTf} zamanında tələblərə cavab verən aktiv siqnal yoxdur.", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    }
                    return;
                }
            }

            // DIRECT TIMEFRAME PREFERENCE SELECTION
            if (text == "⏱ 3 Dəqiqə (3m)" || text == "3m")
            {
                userSettings.Timeframe = "3m";
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>3m</code>\n<i>Artıq yalnız 3m siqnalları alacaqsınız.</i>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "⏱ 5 Dəqiqə (5m)" || text == "5m")
            {
                userSettings.Timeframe = "5m";
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>5m</code>\n<i>Artıq yalnız 5m siqnalları alacaqsınız.</i>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "⏱ 15 Dəqiqə (15m)" || text == "15m")
            {
                userSettings.Timeframe = "15m";
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>15m</code>\n<i>Artıq yalnız 15m siqnalları alacaqsınız.</i>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "⏱ 1 Saat (1h)" || text == "1h")
            {
                userSettings.Timeframe = "1h";
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>1h</code>\n<i>Artıq yalnız 1h siqnalları alacaqsınız.</i>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "⏱ 4 Saat (4h)" || text == "4h")
            {
                userSettings.Timeframe = "4h";
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync($"✅ <b>Zaman Çərçivəsi Təyin Edildi:</b> <code>4h</code>\n<i>Artıq yalnız 4h siqnalları alacaqsınız.</i>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "🌟 Bütün Zamanlar (Hamısı)" || text == "Hamisi" || text == "Hamısı")
            {
                userSettings.Timeframe = "Hamısı";
                userSettings.LastResumeTime = DateTime.UtcNow;
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
                await SendMessageAsync("🛑 <b>Bildirişlər və nəticə hesabatları dayandırıldı.</b>\n\nArxa fondan sizə heç bir siqnal və nəticə göndərilməyəcək.\nYenidən başlamaq üçün 'Bildirişləri Başlat' düyməsinə vurun.", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Başlat") || text.Contains("Baslat") || text == "/resume")
            {
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync("▶️ <b>Bildirişlər aktivləşdirildi!</b>\n\nSeçdiyiniz zaman çərçivəsi (" + userSettings.Timeframe + ") üzrə 24/7 siqnallar və nəticələr göndəriləcək.", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Sıfırla") || text.Contains("Sifirla") || text == "/clear" || text == "/reset")
            {
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync("🧹 <b>Keçmiş siqnal izləmələri profiliniz üçün sıfırlandı!</b>\n\nBundan əvvəlki köhnə siqnalların nəticəsi sizə gəlməyəcək. Yalnız bu andan etibarən yaranan təzə siqnallar izlənəcək.", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Statistika") || text == "/stats")
            {
                var stats = await signalEngine.GetPerformanceStatsAsync();
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
