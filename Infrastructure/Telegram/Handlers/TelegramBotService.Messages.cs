using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoSense.Infrastructure.Telegram
{
    // Partial class: incoming message handler + FSM
    public partial class TelegramBotService
    {
        private async Task SendMockTestSignalAsync(string chatId, UserSettings settings, string timeframe)
        {
            try
            {
                await Task.Delay(2000);
                if (!_testModeChats.ContainsKey(chatId)) return;

                var coin = settings.CustomCoins.Count > 0 ? settings.CustomCoins.Last() : (settings.Coins.Count > 0 ? settings.Coins.First() : "BTCUSDT");
                var cleanCoin = coin.Replace("USDT", "");
                var isBtc = cleanCoin == "BTC";
                var isSol = cleanCoin == "SOL";
                
                decimal entry = isBtc ? 63250.00m : (isSol ? 142.50m : 5.80m);
                decimal tp1 = isBtc ? 64010.00m : (isSol ? 144.20m : 5.87m);
                decimal tp2 = isBtc ? 64770.00m : (isSol ? 145.90m : 5.94m);
                decimal sl = isBtc ? 62800.00m : (isSol ? 141.50m : 5.76m);
                var tf = (timeframe == "Hamısı" || timeframe == "Hamisi" || string.IsNullOrWhiteSpace(timeframe) || timeframe == "Təyin olunmayıb") ? "1h" : timeframe;

                var mockSig = new FuturesSignal
                {
                    Id = (int)(DateTime.UtcNow.Ticks % 100000),
                    Symbol = coin,
                    SignalType = "STRONG_BUY_LONG",
                    Direction = SignalDirection.Buy,
                    EntryPrice = entry,
                    EntryLow = Math.Round(entry * 0.998m, 4),
                    EntryHigh = Math.Round(entry * 1.002m, 4),
                    TakeProfit1 = tp1,
                    TakeProfit2 = tp2,
                    StopLoss = sl,
                    ConfluenceScore = 84.5m,
                    Timeframe = tf,
                    GeneratedAt = DateTime.UtcNow,
                    TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted,
                    CandleCloseTimeUtc = DateTime.UtcNow,
                    PriceSource = "Binance Futures Test Engine",
                    DataAgeMs = 110,
                    NewsSentimentImpact = "BULLISH 🟢",
                    Status = SignalStatus.Open
                };

                // BƏND 1: Test sayğacı YOX. Nömrə YALNIZ real göndərilmiş siqnala aiddir.
                var userSigNum = 0;

                var alertMsg = TelegramMessageFormatter.FormatSignalAlert(mockSig, userSigNum);
                var note = "🧪 <b>[TEST REJİMİ CANLI SİMULYASİYASI]</b>\n" +
                           "<i>Bütün parametrlər və düymələr işləkdir. Göndərilən test siqnalı:</i>\n\n";
                await SendMessageAsync(note + alertMsg, chatId);

                // Simulate TP1 target reached after 6 seconds so user sees how win/target alert looks
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(6000);
                        if (!_testModeChats.ContainsKey(chatId)) return;
                        mockSig.Status = SignalStatus.Success;
                        mockSig.ClosePrice = tp1;
                        mockSig.GrossResultPercent = 1.20m;
                        mockSig.NetResultPercent = 1.10m;
                        mockSig.ClosedAt = DateTime.UtcNow;

                        var outcomeMsg = TelegramMessageFormatter.FormatOutcomeAlert(mockSig, userSigNum, "🎯 Take Profit 1 (TP1)", tp1, +1.20m);
                        var outNote = "🧪 <b>[TEST REJİMİ NƏTİCƏ SİMULYASİYASI]</b>\n\n";
                        await SendMessageAsync(outNote + outcomeMsg, chatId);
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TestMode] Error: {ex.Message}");
            }
        }

        private async Task HandleIncomingMessageAsync(string chatId, string telegramUsername, long? userId, long messageId, string text)
        {
            // Action debouncer: ignore rapid double-taps/clicks of the exact same action within 1.5 seconds,
            // EXCEPT for toggle buttons (🎛 Əsas Terminal and 👑 Admin Paneli) which require immediate double-tap to collapse!
            bool isToggleCommand = text == "🎛 Əsas Terminal" || 
                                   text.Contains("Əsas Terminal") || 
                                   text.Contains("Esas Terminal") || 
                                   text == "Terminal" ||
                                   text == "👑 Admin Paneli" || 
                                   text.Contains("Admin Paneli");

            if (!isToggleCommand)
            {
                var nowUtc = DateTime.UtcNow;
                if (_lastUserAction.TryGetValue(chatId, out var lastAct))
                {
                    if (lastAct.Text == text && (nowUtc - lastAct.Time).TotalMilliseconds < 1500)
                    {
                        return;
                    }
                }
                _lastUserAction[chatId] = (text, nowUtc);
            }

            // Immediately delete incoming user message for toggle buttons so chat remains 100% clean
            if (isToggleCommand)
            {
                _ = DeleteMessageAsync(chatId, messageId);
            }

            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
            var newsService = scope.ServiceProvider.GetRequiredService<INewsService>();

            bool isAdminUser = IsSuperAdmin(chatId, userId, telegramUsername);
            bool isLogoutCommand = text == "/logout" || text.Equals("/cixis", StringComparison.OrdinalIgnoreCase) || text.Equals("/exit", StringComparison.OrdinalIgnoreCase) || text.Equals("Çıxış", StringComparison.OrdinalIgnoreCase) || text.Equals("Cixis", StringComparison.OrdinalIgnoreCase) || text.Equals("logout", StringComparison.OrdinalIgnoreCase);

            // ZERO-PASSWORD ADMIN DIRECT ACCESS:
            // Admin (Ali) is recognized instantly by Telegram UserId (1219998176), TelegramUsername (@Ali_Mahammadov),
            // or SuperAdmin ChatId. No password is ever asked!
            if (isAdminUser && !_loggedOutChats.ContainsKey(chatId) && !isLogoutCommand)
            {
                _authenticatedSessions[chatId] = "Ali";
                SuperAdminChatId = chatId;
                _loggedOutChats.TryRemove(chatId, out _);
                // CRITICAL: Do NOT wipe FSM state if admin is mid-input (create/delete/reset user)
                bool adminInFsm = _userStates.TryGetValue(chatId, out var existingAdmState) &&
                                  (existingAdmState == "ADMIN_WAITING_CREATE_USER" ||
                                   existingAdmState == "ADMIN_WAITING_DELETE_USER" ||
                                   existingAdmState == "ADMIN_WAITING_RESET_PWD");
                if (!adminInFsm)
                    _userStates.TryRemove(chatId, out _);
                var aSettings = GetSettings(chatId);
                aSettings.TelegramUserId = userId ?? 1219998176;
                aSettings.Username = "Ali (Super Admin)";
                if (aSettings.Coins == null || aSettings.Coins.Count == 0)
                {
                    aSettings.Coins = new List<string>(Default40Coins);
                    aSettings.Timeframe = "1h, 4h";
                    aSettings.PortfolioMode = "Standard40";
                }
                SaveSettings();
            }

            // STRICT NON-ADMIN COMMAND RESTRICTION:
            // Commands like /clear, /clean, /test, /testmode, /teststop, start test, stop test, etc. are strictly reserved for Admin!
            bool isRestrictedAdminCommand = 
                text.Equals("/clear", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/cler", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/clean", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/clean-db", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/reset", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/reset-db", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/testmode", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/teststop", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("start test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/start test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("start_test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("stop test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/stop test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("stop_test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("testi dayandır", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("testi dayandir", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("test bitdi", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("başla", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("basla", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("test rejimi", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("testi başlat", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/adduser", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/createuser", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/deluser", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/test_signal", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/test_outcome", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/test_pipeline", StringComparison.OrdinalIgnoreCase);

            if (!isAdminUser && isRestrictedAdminCommand)
            {
                _ = DeleteMessageAsync(chatId, messageId);
                await SendMessageAsync(
                    "⛔ <b>Səlahiyyətiniz çatmır!</b>\n\nBu əmr və ya funksiya yalnız Sistem Admininə məxsusdur.", 
                    chatId);
                return;
            }

            bool isMenuButtonClick = text.StartsWith("🧭") || text.StartsWith("⚡") || text.StartsWith("⭐") || 
                                     text.StartsWith("📊") || text.StartsWith("📈") || text.StartsWith("⚙️") || 
                                     text.StartsWith("🗑") || text.StartsWith("⏱") || text.StartsWith("🌟") || 
                                     text.StartsWith("🧹") || text.StartsWith("🛑") || text.StartsWith("▶️") || 
                                     text.StartsWith("📰") || text.StartsWith("⬅️") || text.StartsWith("👥") || 
                                     text.StartsWith("🔑") || text.StartsWith("👑") || text.StartsWith("📋") || 
                                     text.StartsWith("ℹ️") || text.StartsWith("📥") || text.StartsWith("🎛") ||
                                     text == "➕ Öz coini əlavə et" || text == "➕ İstifadəçi Yarat" ||
                                     text.Contains("Siqnallar") || text.Contains("Menyu") || text.Contains("Statistika") ||
                                     (text.StartsWith("/") && !text.StartsWith("/login", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("/admin ", StringComparison.OrdinalIgnoreCase)
                                      && !text.StartsWith("/test_signal", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("/test_outcome", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("/test_pipeline", StringComparison.OrdinalIgnoreCase));

            // FSM guard: if user is mid text-input step, force isMenuButtonClick = false
            if (_userStates.TryGetValue(chatId, out var fsmGuardState) &&
                (fsmGuardState == "ADMIN_WAITING_CREATE_USER" ||
                 fsmGuardState == "ADMIN_WAITING_DELETE_USER" ||
                 fsmGuardState == "ADMIN_WAITING_RESET_PWD" ||
                 fsmGuardState == "WAITING_ADD_CUSTOM_COIN" ||
                 fsmGuardState == "USER_WAITING_DELETE_COIN"))
            {
                isMenuButtonClick = false;
            }

            // =========================================================================
            // 0. LOGOUT COMMAND (ALWAYS CLEARS DATABASE & SESSION)
            // =========================================================================
            if (text == "/logout" || text.Equals("/cixis", StringComparison.OrdinalIgnoreCase) || text.Equals("/exit", StringComparison.OrdinalIgnoreCase) || text.Equals("Çıxış", StringComparison.OrdinalIgnoreCase) || text.Equals("Cixis", StringComparison.OrdinalIgnoreCase) || text.Equals("logout", StringComparison.OrdinalIgnoreCase))
            {
                _ = DeleteMessageAsync(chatId, messageId);
                _authenticatedSessions.TryRemove(chatId, out _);
                _loggedOutChats[chatId] = true;
                if (UserPreferences.TryGetValue(chatId, out var pref))
                {
                    pref.IsActive = false;
                }
                _userStates.TryRemove(chatId, out _);
                if (SuperAdminChatId == chatId) SuperAdminChatId = null;
                SaveSettings();

                await userManager.LogoutAsync(chatId, userId);
                await userManager.ClearChatBindingAsync(chatId, userId);

                await SendMessageAsync(
                    "👋 <b>Hesabınızdan çıxış edildi!</b>\n\n" +
                    "Yenidən daxil olmaq üçün <b>İstifadəçi Adınızı</b> və <b>Parolunuzu</b> yazın:\n" +
                    "💡 <b>Nümunə:</b> <code>Murad 123456</code>", 
                    chatId, 
                    new { remove_keyboard = true });
                return;
            }

            // =========================================================================
            // 0.1 /START COMMAND (ZERO-PASSWORD FOR ADMIN, STRICT LOGIN FOR USERS)
            // =========================================================================
            if (text == "/start" || text.Equals("/baslat", StringComparison.OrdinalIgnoreCase) || text.Equals("start", StringComparison.OrdinalIgnoreCase))
            {
                _ = DeleteMessageAsync(chatId, messageId);

                if (isAdminUser)
                {
                    var uSettings = GetSettings(chatId);
                    if (uSettings.IsTerminalOpen && uSettings.LastTerminalMessageId.HasValue)
                    {
                        _ = DeleteMessageAsync(chatId, uSettings.LastTerminalMessageId.Value);
                        uSettings.IsTerminalOpen = false;
                        uSettings.LastTerminalMessageId = null;
                    }
                    if (uSettings.IsAdminOpen && uSettings.LastAdminMessageId.HasValue)
                    {
                        _ = DeleteMessageAsync(chatId, uSettings.LastAdminMessageId.Value);
                        uSettings.IsAdminOpen = false;
                        uSettings.LastAdminMessageId = null;
                    }
                    SaveSettings();

                    var welcomeAdmin = $"👑 <b>Xoş Gəldiniz, Baş Admin!</b>\n\n" +
                                       $"🚀 <b>CryptoSense Terminal Xidməti AKTİVDİR 🟢</b>\n\n" +
                                       $"Terminalı açmaq üçün aşağıdakı <b>🎛 Əsas Terminal</b> düyməsinə toxunun.";
                    await SendMessageAsync(welcomeAdmin, chatId, TelegramKeyboards.BuildUserKeyboard(uSettings, isAdmin: true));
                    _ = BuildAndSendPortfolioSummaryAsync(chatId, uSettings, forceRefresh: false);
                    return;
                }

                if (!_authenticatedSessions.TryGetValue(chatId, out var sessionUser))
                {
                    var dbUser = await userManager.GetUserByChatIdOrTelegramIdAsync(chatId, userId);
                    if (dbUser != null && dbUser.IsActive && dbUser.IsLoggedIn)
                    {
                        sessionUser = dbUser.Username;
                        _authenticatedSessions[chatId] = sessionUser;
                        _loggedOutChats.TryRemove(chatId, out _);
                    }
                }

                if (string.IsNullOrEmpty(sessionUser))
                {
                    // User has not logged in yet: Send ONLY the login screen with keyboard removed
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
                else
                {
                    // Already logged in user pressed /start: reset any open terminal state and show welcome greeting with button
                    var uSettings = GetSettings(chatId);
                    if (uSettings.IsTerminalOpen && uSettings.LastTerminalMessageId.HasValue)
                    {
                        _ = DeleteMessageAsync(chatId, uSettings.LastTerminalMessageId.Value);
                        uSettings.IsTerminalOpen = false;
                        uSettings.LastTerminalMessageId = null;
                    }
                    if (uSettings.IsAdminOpen && uSettings.LastAdminMessageId.HasValue)
                    {
                        _ = DeleteMessageAsync(chatId, uSettings.LastAdminMessageId.Value);
                        uSettings.IsAdminOpen = false;
                        uSettings.LastAdminMessageId = null;
                    }
                    SaveSettings();

                    var greeting = $"👋 <b>Salam, {sessionUser}! Kripto Signals Bot hazırdır 🟢</b>\n\n" +
                                   "Terminalı açmaq üçün aşağıdakı <b>🎛 Əsas Terminal</b> düyməsinə toxunun.";
                    await SendMessageAsync(greeting, chatId, TelegramKeyboards.BuildUserKeyboard(uSettings, isAdmin: false));
                    _ = BuildAndSendPortfolioSummaryAsync(chatId, uSettings, forceRefresh: false);
                    return;
                }
            }

            // =========================================================================
            // 1. AUTHENTICATION GATING (STRICT: NO AUTO-LOGIN BYPASS)
            // =========================================================================
            string? currentUsername = null;
            bool isAuthenticated = isAdminUser && !_loggedOutChats.ContainsKey(chatId);
            if (!isAuthenticated)
            {
                if (!_authenticatedSessions.TryGetValue(chatId, out currentUsername))
                {
                    var dbUser = await userManager.GetUserByChatIdOrTelegramIdAsync(chatId, userId);
                    if (dbUser != null && dbUser.IsActive && dbUser.IsLoggedIn)
                    {
                        currentUsername = dbUser.Username;
                        _authenticatedSessions[chatId] = currentUsername;
                        _loggedOutChats.TryRemove(chatId, out _);
                        isAuthenticated = true;
                    }
                }
                else
                {
                    isAuthenticated = true;
                }
            }
            else
            {
                _authenticatedSessions.TryGetValue(chatId, out currentUsername);
                if (string.IsNullOrEmpty(currentUsername)) currentUsername = "Ali";
            }

            if (!isAuthenticated)
            {
                var cleanLogin = text;
                if (cleanLogin.StartsWith("/login", StringComparison.OrdinalIgnoreCase)) cleanLogin = cleanLogin.Substring(6).Trim();
                if (cleanLogin.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)) cleanLogin = "Ali " + cleanLogin.Substring(6).Trim();

                var parts = cleanLogin.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                bool isCredentialAttempt = parts.Length >= 2 || text.Contains("23031999Am");

                if (isCredentialAttempt)
                {
                    string inputUser = parts.Length >= 2 ? parts[0] : "Ali";
                    string inputPass = parts.Length >= 2 ? string.Join(" ", parts.Skip(1)) : "23031999Am";

                    _ = DeleteMessageAsync(chatId, messageId);

                    var (isValid, user) = await userManager.ValidateLoginAsync(inputUser, inputPass, userId, chatId, telegramUsername);
                    if (isValid && user != null)
                    {
                        _authenticatedSessions[chatId] = user.Username;
                        _loggedOutChats.TryRemove(chatId, out _);
                        _userStates.TryRemove(chatId, out _);

                        var settings = GetSettings(chatId);
                        settings.TelegramUserId = userId;
                        if (settings.Coins == null || settings.Coins.Count == 0)
                        {
                            settings.Coins = new List<string>(Default40Coins);
                            settings.Timeframe = "1h, 4h";
                            settings.PortfolioMode = "Standard40";
                        }
                        settings.IsActive = true;
                        settings.LastResumeTime = DateTime.UtcNow;
                        settings.IsTerminalOpen = false;
                        settings.LastTerminalMessageId = null;
                        settings.IsAdminOpen = false;
                        settings.LastAdminMessageId = null;
                        SaveSettings();

                        bool isAdm = (user.Role == UserRole.Admin) || 
                                     user.Username.Equals("Ali", StringComparison.OrdinalIgnoreCase) || 
                                     (userId.HasValue && userId.Value == 1219998176);

                        if (isAdm)
                        {
                            SuperAdminChatId = chatId;
                            settings.Username = "Ali (Super Admin)";
                            SaveSettings();

                            var welcomeAdmin = $"👑 <b>Xoş Gəldiniz, Baş Admin ({user.Username})!</b>\n\n" +
                                               $"🚀 <b>CryptoSense Terminal Xidməti AKTİVDİR 🟢</b>\n\n" +
                                               $"Terminalı açmaq üçün aşağıdakı <b>🎛 Əsas Terminal</b> düyməsinə toxunun.\n\n" +
                                               $"<i>Çıxış etmək üçün: <code>/logout</code></i>";

                            await SendMessageAsync(welcomeAdmin, chatId, TelegramKeyboards.BuildUserKeyboard(settings, isAdmin: true));
                            return;
                        }
                        else
                        {
                            settings.Username = user.Username;
                            SaveSettings();

                            var onboardingMsg = $"✅ <b>Giriş Təsdiqləndi! Xoş Gəldiniz, {user.Username}!</b>\n\n" +
                                                $"🚀 <b>Kripto Signals Bot Xidməti AKTİVDİR 🟢</b>\n\n" +
                                                $"Terminalı açmaq üçün aşağıdakı <b>🎛 Əsas Terminal</b> düyməsinə toxunun.\n\n" +
                                                $"<i>Çıxış etmək üçün: <code>/logout</code></i>";
                            
                            await SendMessageAsync(onboardingMsg, chatId, TelegramKeyboards.BuildUserKeyboard(settings, isAdmin: false));
                            await NotifySuperAdminUserLoginAsync(user.Username, $"Telegram (@{telegramUsername})");
                            return;
                        }
                    }
                    else
                    {
                        _ = DeleteMessageAsync(chatId, messageId);
                        var failMsg = "❌ <b>Giriş Uğursuz Oldu!</b>\n\n" +
                                      "İstifadəçi adı və ya parol yalnışdır.\n" +
                                      "Zəhmət olmasa məlumatlarınızı yoxlayıb yenidən daxil edin:\n\n" +
                                      "💡 <b>Nümunə:</b> <code>Murad 123456</code>\n\n" +
                                      "<i>Hesabınız yoxdursa, Admin (<a href=\"https://t.me/Ali_Mahammadov\">@Ali_Mahammadov</a>) ilə əlaqə saxlayın.</i>";

                        await SendMessageAsync(failMsg, chatId, new { remove_keyboard = true });
                        return;
                    }
                }
                else
                {
                    _ = DeleteMessageAsync(chatId, messageId);
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
            }

            // =========================================================================
            // 2. USER IS FULLY AUTHENTICATED
            // =========================================================================
            var currentUser = (!string.IsNullOrEmpty(currentUsername) ? await uow.Users.GetByUsernameAsync(currentUsername) : null) ?? 
                              await userManager.GetUserByChatIdOrTelegramIdAsync(chatId, userId) ??
                              (isAdminUser ? await uow.Users.GetByUsernameAsync("Ali") : null);

            if (currentUser == null)
            {
                _authenticatedSessions.TryRemove(chatId, out _);
                await SendMessageAsync("⚠️ Sessiya bitmişdir. Zəhmət olmasa yenidən daxil olun.", chatId, new { remove_keyboard = true });
                return;
            }

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
            // 2.1 ADMIN FSM INPUT HANDLING (STRICT GATING - PREVENT FALLTHROUGH)
            // =========================================================================
            if (!isMenuButtonClick && isAdmin && _userStates.TryGetValue(chatId, out var admState))
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
                            await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                        }
                        else
                        {
                            await SendMessageAsync($"⚠️ <b>Xəta:</b> <code>{newUsername}</code> adlı istifadəçi artıq mövcuddur!", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                        }
                    }
                    else
                    {
                        await SendMessageAsync("⚠️ <b>Xəta:</b> Düzgün format daxil edin!\nFormat: <code>istifadəçi_adı parol</code> (məsələn: <code>murad 123456</code>)", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    return;
                }
                else if (admState == "ADMIN_WAITING_DELETE_USER")
                {
                    _userStates.TryRemove(chatId, out _);
                    var userToDelete = text.Trim();
                    if (string.IsNullOrWhiteSpace(userToDelete))
                    {
                        await SendMessageAsync("⚠️ <b>Xəta:</b> Silinəcək istifadəçi adını qeyd edin!", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                        return;
                    }
                    var deleted = await userManager.DeleteUserAsync(userToDelete);
                    if (deleted)
                    {
                        await RevokeUserSessionAsync(userToDelete);
                        await SendMessageAsync($"✅ <b>İstifadəçi '{userToDelete}' sistemdən silindi və bütün prosesləri dayandırıldı!</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    else
                    {
                        await SendMessageAsync($"⚠️ <b>'{userToDelete}' tapılmadı və ya silinə bilməz.</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    return;
                }
                else if (admState == "ADMIN_WAITING_RESET_PWD")
                {
                    _userStates.TryRemove(chatId, out _);
                    var parts = text.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        var uName = parts[0];
                        var newPwd = string.Join(" ", parts.Skip(1));
                        var res = await userManager.ResetPasswordAsync(uName, newPwd);
                        if (res)
                        {
                            await SendMessageAsync($"✅ <b>'{uName}' üçün yeni parol təyin edildi:</b> <code>{newPwd}</code>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                        }
                        else
                        {
                            await SendMessageAsync($"⚠️ <b>'{uName}' adlı istifadəçi tapılmadı!</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                        }
                    }
                    else
                    {
                        await SendMessageAsync("⚠️ <b>Xəta:</b> Düzgün format daxil edin!\nFormat: <code>istifadəçi_adı yeni_parol</code> (məsələn: <code>murad yeniParol123</code>)", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    return;
                }
            }

            // =========================================================================
            // 3. ADMIN SWITCH & CRUD FLOW
            // =========================================================================
            bool isAdminToggleClick = isAdmin && (text == "👑 Admin Paneli" || 
                                                 text.Contains("Admin Paneli", StringComparison.OrdinalIgnoreCase) || 
                                                 text.Equals("/admin", StringComparison.OrdinalIgnoreCase));

            if (isAdminToggleClick)
            {
                _userStates.TryRemove(chatId, out _);
                _ = DeleteMessageAsync(chatId, messageId);

                // Toggle logic: If user clicked "👑 Admin Paneli" and it is already open, close it cleanly
                if (userSettings.IsAdminOpen && userSettings.LastAdminMessageId.HasValue)
                {
                    var oldAdminMsgId = userSettings.LastAdminMessageId.Value;
                    userSettings.IsAdminOpen = false;
                    userSettings.LastAdminMessageId = null;
                    SaveSettings();
                    await DeleteMessageAsync(chatId, oldAdminMsgId);
                    return;
                }

                // If Terminal was open, close it so they don't duplicate
                if (userSettings.IsTerminalOpen && userSettings.LastTerminalMessageId.HasValue)
                {
                    _ = DeleteMessageAsync(chatId, userSettings.LastTerminalMessageId.Value);
                    userSettings.IsTerminalOpen = false;
                    userSettings.LastTerminalMessageId = null;
                }

                if (userSettings.LastAdminMessageId.HasValue)
                {
                    _ = DeleteMessageAsync(chatId, userSettings.LastAdminMessageId.Value);
                    userSettings.LastAdminMessageId = null;
                }

                var allUsers = await userManager.GetAllUsersAsync();
                var totalCount = allUsers.Count;
                var activeCount = allUsers.Count(u => u.IsActive);
                var adminDash = TelegramMessageFormatter.FormatAdminDashboard(totalCount, activeCount);
                var newAdminMsgId = await SendMessageReturnIdAsync(adminDash, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                userSettings.LastAdminMessageId = newAdminMsgId;
                userSettings.IsAdminOpen = true;
                SaveSettings();
                return;
            }

            // =========================================================================
            // 3.1 DEDICATED TERMINAL TOGGLE (INSTANT OPEN / CLOSE WITH NO RESIDUAL BUBBLES)
            // =========================================================================
            bool isExplicitTerminal = text == "🎛 Əsas Terminal" || 
                                      text.Contains("Əsas Terminal") || 
                                      text.Contains("Esas Terminal") || 
                                      text == "Terminal";

            if (isExplicitTerminal)
            {
                _userStates.TryRemove(chatId, out _);
                _ = DeleteMessageAsync(chatId, messageId);

                // Toggle logic: If user clicked 🎛 Əsas Terminal and it is already open, cleanly collapse it into place!
                if (userSettings.IsTerminalOpen && userSettings.LastTerminalMessageId.HasValue)
                {
                    var oldMsgId = userSettings.LastTerminalMessageId.Value;
                    userSettings.IsTerminalOpen = false;
                    userSettings.LastTerminalMessageId = null;
                    SaveSettings();
                    await DeleteMessageAsync(chatId, oldMsgId);
                    return;
                }

                // If Admin was open, close it
                if (userSettings.IsAdminOpen && userSettings.LastAdminMessageId.HasValue)
                {
                    _ = DeleteMessageAsync(chatId, userSettings.LastAdminMessageId.Value);
                    userSettings.IsAdminOpen = false;
                    userSettings.LastAdminMessageId = null;
                }

                // If opening a new terminal, remove previous one if any
                if (userSettings.LastTerminalMessageId.HasValue)
                {
                    _ = DeleteMessageAsync(chatId, userSettings.LastTerminalMessageId.Value);
                    userSettings.LastTerminalMessageId = null;
                }

                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var openCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                var lastTime = lastDeliveredUtc.HasValue 
                    ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) 
                    : "";

                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, openCount, lastTime);
                var inlineKb = TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isAdmin);

                var newMsgId = await SendMessageReturnIdAsync(dashText, chatId, inlineKb);
                userSettings.LastTerminalMessageId = newMsgId;
                userSettings.IsTerminalOpen = true;
                SaveSettings();
                _ = BuildAndSendPortfolioSummaryAsync(chatId, userSettings, forceRefresh: false);
                return;
            }

            // =========================================================================
            // 3.2 TEST REJİMİ ƏMRLƏRİ (YALNIZ ADMİN VƏ YALNIZ "start test" YAZILDIQDA)
            // =========================================================================
            bool isStartTestCommand = isAdmin && (
                text.Equals("start test", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("/start test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("start_test", StringComparison.OrdinalIgnoreCase));

            bool isStopTestCommand = isAdmin && (
                text.Equals("stop test", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("/stop test", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("/teststop", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("testi dayandır", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("testi dayandir", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("stop_test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("test bitdi", StringComparison.OrdinalIgnoreCase));

            if (isStartTestCommand)
            {
                _testModeChats[chatId] = true;
                var prompt = "🧪 <b>İnteraktiv Test Rejimi AKTİVDİR! 🟢</b>\n\n" +
                             "Sistem test mühitinə keçdi. İndi seçilən portfellər və zaman aralıqları üzrə simulyasiya siqnalları göndəriləcək:\n\n" +
                             "1️⃣ <b>🪙 Standart 40 Coin:</b> Terminalda 1h və ya 4h seçdikdə dərhal nümunəvi test siqnalı və 6 saniyə sonra TP1 nəticə bildirişi gələcək.\n" +
                             "2️⃣ <b>⭐ Mənim Coinlərim:</b> '➕ Coin Əlavə Et' (məs: SOL) və ya '🗑 Coin Sil' edərək fərdi portfelinizi canlı yoxlaya bilərsiniz.\n" +
                             "3️⃣ <b>🔥 40 + Fərdi Coinlər (Kombinə):</b> Həm 40 coin, həm də əlavə etdiyiniz fərdi coinlər üzrə test edə bilərsiniz.\n" +
                             "4️⃣ <b>ℹ️ Sistem Statusu & 🧭 Bitcoin Trend:</b> Bütün göstəricilər anında çatınıza təqdim olunacaq.\n\n" +
                             "<i>Testi bitirmək üçün: <b>stop test</b> (və ya <code>/teststop</code>) yazın.</i>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                _ = SendMockTestSignalAsync(chatId, userSettings, userSettings.Timeframe);
                return;
            }

            if (isStopTestCommand)
            {
                _testModeChats.TryRemove(chatId, out _);
                var stopMsg = "🏁 <b>Test Rejimi Dayandırıldı! 🔴</b>\n\n" +
                              "✅ Sistem 100% rəsmi 24/7 canlı real bazar skanerinə qayıtdı 🟢.\n" +
                              "Artıq yalnız real bazar qaydalarına (Confluence >= 75%, R:R >= 1.30) cavab verən real siqnallar göndəriləcək.";
                await SendMessageAsync(stopMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }

            if (text == "📊 Əsas Menyu (Siqnallar)" || text == "⬅️ Əsas Menyu" || text == "/menu")
            {
                _userStates.TryRemove(chatId, out _);
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var openCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                var lastTime = lastDeliveredUtc.HasValue ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) : "";
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, openCount, lastTime);
                await SendMessageAsync(dashText, chatId, TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isAdmin));
                _ = BuildAndSendPortfolioSummaryAsync(chatId, userSettings, forceRefresh: false);
                return;
            }

            if (isAdmin && (text.StartsWith("/adduser", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/createuser", StringComparison.OrdinalIgnoreCase) || text == "➕ İstifadəçi Yarat"))
            {
                var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 3 && !text.Equals("➕ İstifadəçi Yarat", StringComparison.OrdinalIgnoreCase))
                {
                    _userStates.TryRemove(chatId, out _);
                    var newUsername = parts[1];
                    var newPassword = string.Join(" ", parts.Skip(2));
                    var created = await userManager.CreateUserAsync(newUsername, newPassword);
                    if (created)
                    {
                        var msg = $"✅ <b>İstifadəçi uğurla yaradıldı və bazaya yazıldı!</b>\n\n" +
                                  $"👤 <b>İstifadəçi Adı:</b> <code>{newUsername}</code>\n" +
                                  $"🔑 <b>Parol:</b> <code>{newPassword}</code>\n\n" +
                                  $"<i>İstifadəçiyə bildirin ki, bota daxil olaraq <code>{newUsername} {newPassword}</code> yazsın.</i>";
                        await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    else
                    {
                        await SendMessageAsync($"⚠️ <b>Xəta:</b> <code>{newUsername}</code> adlı istifadəçi artıq mövcuddur!", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    return;
                }

                _userStates[chatId] = "ADMIN_WAITING_CREATE_USER";
                var prompt = "➕ <b>Yeni İstifadəçi Yaratmaq</b>\n\n" +
                             "Yaratmaq istədiyiniz <b>İstifadəçi Adını</b> və <b>Parolu</b> aralarında boşluq qoyaraq yazın:\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>Murad 123456</code>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                return;
            }
            if (isAdmin && (text == "👥 İstifadəçilərin Siyahısı" || text == "/users"))
            {
                _userStates.TryRemove(chatId, out _);
                var users = await userManager.GetAllUsersAsync();
                var msg = TelegramMessageFormatter.FormatUserList(users);
                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                return;
            }
            if (isAdmin && (text.StartsWith("/deleteuser", StringComparison.OrdinalIgnoreCase) || text == "🗑 İstifadəçi Sil"))
            {
                var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && !text.Equals("🗑 İstifadəçi Sil", StringComparison.OrdinalIgnoreCase))
                {
                    _userStates.TryRemove(chatId, out _);
                    var userToDelete = parts[1];
                    var deleted = await userManager.DeleteUserAsync(userToDelete);
                    if (deleted)
                    {
                        await RevokeUserSessionAsync(userToDelete);
                        await SendMessageAsync($"✅ <b>İstifadəçi '{userToDelete}' sistemdən silindi və bütün prosesləri dayandırıldı!</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    else
                    {
                        await SendMessageAsync($"⚠️ <b>'{userToDelete}' tapılmadı və ya silinə bilməz.</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    return;
                }

                _userStates[chatId] = "ADMIN_WAITING_DELETE_USER";
                var prompt = "🗑 <b>İstifadəçi Silmək</b>\n\n" +
                             "Sistemdən silmək istədiyiniz istifadəçinin <b>Adını</b> yazın:\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>Murad</code>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                return;
            }
            if (isAdmin && (text.StartsWith("/resetpwd", StringComparison.OrdinalIgnoreCase) || text == "🔑 Parolu Dəyiş"))
            {
                var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 3 && !text.Equals("🔑 Parolu Dəyiş", StringComparison.OrdinalIgnoreCase))
                {
                    _userStates.TryRemove(chatId, out _);
                    var uName = parts[1];
                    var newPwd = string.Join(" ", parts.Skip(2));
                    var changed = await userManager.ResetPasswordAsync(uName, newPwd);
                    if (changed)
                    {
                        await SendMessageAsync($"✅ <b>'{uName}' üçün yeni parol təyin edildi:</b> <code>{newPwd}</code>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    else
                    {
                        await SendMessageAsync($"⚠️ <b>'{uName}' adlı istifadəçi tapılmadı!</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    return;
                }

                _userStates[chatId] = "ADMIN_WAITING_RESET_PWD";
                var prompt = "🔑 <b>İstifadəçi Parolunu Dəyişmək</b>\n\n" +
                             "İstifadəçi adını və yeni parolu aralarında boşluqla yazın:\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>Murad yeni123</code>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildBackToAdminKeyboard());
                return;
            }

            if (isAdmin && (text == "/db" || text == "/getdb" || text == "/backup_db" || text == "📥 Bazanı Yüklə" || text.Contains("Bazanı Yüklə") || text.Contains("Bazani Yukle")))
            {
                _userStates.TryRemove(chatId, out _);
                var currentDbPath = CryptoSense.Domain.Common.AppPaths.DatabasePath;

                if (File.Exists(currentDbPath))
                {
                    var activeUsers = await userManager.GetAllUsersAsync();
                    var cap = $"💾 <b>CryptoSense SQLite Verilənlər Bazası</b>\n\n" +
                              $"📁 <b>Fayl:</b> <code>{currentDbPath}</code>\n" +
                              $"👥 <b>Aktiv İstifadəçi Sayı:</b> <b>{activeUsers.Count} nəfər</b>\n" +
                              $"📅 <b>Tarix:</b> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n\n" +
                              $"<i>Faylı yükləyərək birbaşa DB Browser for SQLite ilə bütün cədvəllərə və istifadəçilərə baxa bilərsiniz.</i>";
                    await SendMessageAsync("⏳ Baza faylı hazırlanır və çatınıza göndərilir...", chatId);
                    await SendDocumentAsync(currentDbPath, chatId, cap);
                }
                else
                {
                    await SendMessageAsync($"⚠️ Baza faylı tapılmadı: <code>{currentDbPath}</code>", chatId);
                }
                return;
            }

            if (isAdmin && (text.StartsWith("/sql ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/query ", StringComparison.OrdinalIgnoreCase)))
            {
                var query = text.Substring(text.IndexOf(' ') + 1).Trim();
                try
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<CryptoSense.Infrastructure.Persistence.AppDbContext>();
                    var conn = dbContext.Database.GetDbConnection();
                    if (conn.State != System.Data.ConnectionState.Open)
                    {
                        await conn.OpenAsync();
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = query;

                    if (query.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || query.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
                    {
                        using var reader = await cmd.ExecuteReaderAsync();
                        var sb = new StringBuilder();
                        int colCount = reader.FieldCount;
                        var colNames = new List<string>();
                        for (int i = 0; i < colCount; i++) colNames.Add(reader.GetName(i));

                        sb.AppendLine(string.Join(" | ", colNames));
                        sb.AppendLine(new string('-', Math.Min(50, Math.Max(20, sb.Length))));

                        int rowCount = 0;
                        while (await reader.ReadAsync() && rowCount < 50)
                        {
                            var rowVals = new List<string>();
                            for (int i = 0; i < colCount; i++)
                            {
                                var val = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "";
                                rowVals.Add(val);
                            }
                            sb.AppendLine(string.Join(" | ", rowVals));
                            rowCount++;
                        }

                        if (rowCount == 0) sb.AppendLine("(0 sətir tapıldı)");
                        var resultMsg = $"📊 <b>SQL Nəticəsi ({rowCount} sətir):</b>\n\n<pre><code>{System.Net.WebUtility.HtmlEncode(sb.ToString())}</code></pre>";
                        await SendMessageAsync(resultMsg, chatId);
                    }
                    else
                    {
                        int affected = await dbContext.Database.ExecuteSqlRawAsync(query);
                        await SendMessageAsync($"✅ <b>Əməliyyat icra olundu. Təsirlənən sətir sayı: {affected}</b>", chatId);
                    }
                }
                catch (Exception ex)
                {
                    await SendMessageAsync($"⚠️ <b>SQL Xətası:</b> <code>{System.Net.WebUtility.HtmlEncode(ex.Message)}</code>", chatId);
                }
                return;
            }

            if (isAdmin && (text == "🌐 Bütün Coinlərin Siyahısı" || text == "/all_coins"))
            {
                _userStates.TryRemove(chatId, out _);
                var monitored = Default40Coins;

                var cleanCoins = monitored.Select(c => c.Replace("USDT", "")).Distinct().ToList();
                var msg = "🌐 <b>Sistemin Canlı İzlədiyi Bütün Coinlər və Zamanlar</b>\n\n" +
                          $"📊 <b>Ümumi Coin Sayı:</b> <b>{cleanCoins.Count} ədəd (Standart İnstitusional 40)</b>\n" +
                          $"🪙 <b>İzlənən Coinlər:</b>\n<code>{string.Join(", ", cleanCoins)}</code>\n\n" +
                          "⏱ <b>Dövri Olaraq Analiz Olunan Şamlar:</b>\n" +
                          "• <b>1 Saat (1h)</b> — Orta müddətli güclü dalğa\n" +
                          "• <b>4 Saat (4h)</b> — Əsas makro trend və güclü səviyyələr\n\n" +
                          "🔍 <b>Skan Mexanizmi:</b>\n" +
                          $"Sistem arxa fonda hər 10 saniyədən bir bu {cleanCoins.Count} coinin hər birini aktiv zaman kəsiyində (EMA, MACD, RSI, ATR, Confluence və BTC Kompası) analiz edir və Confluence >= 75% olanda şam kilidi ilə istifadəçilərə çatdırır.";

                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                return;
            }

            // =========================================================================
            // SUPERADMIN TEST PIPELINE COMMANDS (/test_signal, /test_outcome, /test_pipeline)
            // =========================================================================
            if (isAdmin && (text == "/test_signal" || text.StartsWith("/test_signal ", StringComparison.OrdinalIgnoreCase)))
            {
                if (!await CanReceivePushAsync(chatId))
                {
                    Console.WriteLine($"[TestSignal] Blocked: {chatId} cannot receive push (CanReceivePush=false).");
                    if (_loggedOutChats.ContainsKey(chatId) || !_authenticatedSessions.ContainsKey(chatId))
                    {
                        return;
                    }
                    await SendMessageAsync(
                        "⚠️ <b>Test siqnalı bloklandı.</b>\n\n" +
                        "Siqnal almaq üçün:\n" +
                        "1. Terminalı açın → ⚙️ Coin Seçimi\n" +
                        "2. Zaman kəsiyini seçin\n" +
                        "3. ▶️ Başlat düyməsinə basın\n\n" +
                        "<i>Bildiriş Statusu 🟢 Aktiv olduqdan sonra yenidən cəhd edin.</i>",
                        chatId);
                    return;
                }

                var arg = text.Length > 12 ? text.Substring(12).Trim().ToLowerInvariant() : "long";
                await ExecuteTestSignalCommandAsync(chatId, userSettings, scope, arg);
                return;
            }

            if (isAdmin && (text == "/test_outcome" || text.StartsWith("/test_outcome ", StringComparison.OrdinalIgnoreCase)))
            {
                if (!await CanReceivePushAsync(chatId))
                {
                    Console.WriteLine($"[TestOutcome] Blocked: {chatId} cannot receive push (CanReceivePush=false).");
                    if (_loggedOutChats.ContainsKey(chatId) || !_authenticatedSessions.ContainsKey(chatId))
                    {
                        return;
                    }
                    await SendMessageAsync(
                        "⚠️ <b>Test nəticəsi bloklandı.</b>\n\n" +
                        "Bildiriş Statusu 🟢 Aktiv olmadıqda nəticə kartı göndərilə bilməz.\n" +
                        "Zəhmət olmasa terminaldan ▶️ Başlat düyməsinə basaraq xidməti aktivləşdirin.",
                        chatId);
                    return;
                }

                var arg = text.Length > 13 ? text.Substring(13).Trim().ToLowerInvariant() : "tp1";
                await ExecuteTestOutcomeCommandAsync(chatId, userSettings, scope, arg);
                return;
            }

            if (isAdmin && (text == "/test_pipeline" || text.StartsWith("/test_pipeline", StringComparison.OrdinalIgnoreCase)))
            {
                if (!await CanReceivePushAsync(chatId))
                {
                    Console.WriteLine($"[TestPipeline] Blocked: {chatId} cannot receive push (CanReceivePush=false).");
                    if (_loggedOutChats.ContainsKey(chatId) || !_authenticatedSessions.ContainsKey(chatId))
                    {
                        return;
                    }
                    await SendMessageAsync(
                        "⚠️ <b>Test borusu bloklandı.</b>\n\n" +
                        "Bildiriş Statusu 🟢 Aktiv olmadıqda test borusu işə düşə bilməz.\n" +
                        "Zəhmət olmasa terminaldan ▶️ Başlat düyməsinə basaraq xidməti aktivləşdirin.",
                        chatId);
                    return;
                }

                var sig = await ExecuteTestSignalCommandAsync(chatId, userSettings, scope, "long");
                if (sig != null)
                {
                    await SendMessageAsync("⏱ <i>Test borusu aktivdir: ~60 saniyə sonra nəticə kartı (TP1) avtomatik göndəriləcək...</i>", chatId);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(60000);

                            // Strict single-gate check: If user logged out or deactivated notifications in the meantime -> 0 messages!
                            if (!await CanReceivePushAsync(chatId))
                            {
                                Console.WriteLine($"[TestPipeline] Aborted 60s outcome push: {chatId} is logged out or inactive.");
                                return;
                            }

                            using var delayScope = _serviceProvider.CreateScope();
                            var delaySettings = GetSettings(chatId);
                            await ExecuteTestOutcomeCommandAsync(chatId, delaySettings, delayScope, "tp1");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[TestPipeline] 60s outcome error: {ex.Message}");
                        }
                    });
                }
                return;
            }


            // =========================================================================
            // 6. AUTHENTICATED REGULAR & ADMIN USER ACTIONS
            // =========================================================================

            // If user clicked any menu button, cancel waiting states immediately!
            if (isMenuButtonClick)
            {
                _userStates.TryRemove(chatId, out _);
            }

            // STATE: DELETING A SPECIFIC COIN
            if (!isMenuButtonClick && _userStates.TryGetValue(chatId, out var coinDelState) && coinDelState == "USER_WAITING_DELETE_COIN")
            {
                _userStates.TryRemove(chatId, out _);
                var parts = text.Split(new[] { ',', ' ', ';', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var removedCoins = new List<string>();
                var notFoundCoins = new List<string>();

                foreach (var p in parts)
                {
                    var clean = p.Trim().ToUpperInvariant();
                    if (clean.EndsWith("USDT") && clean.Length > 4) clean = clean.Substring(0, clean.Length - 4);
                    if (clean.Length < 2 || clean.Length > 12) continue;
                    if (!System.Text.RegularExpressions.Regex.IsMatch(clean, @"^[A-Z0-9]+$")) continue;
                    var coinToDel = clean + "USDT";

                    if (userSettings.CustomCoins.Contains(coinToDel) || userSettings.Coins.Contains(coinToDel))
                    {
                        userSettings.CustomCoins.Remove(coinToDel);
                        userSettings.Coins.Remove(coinToDel);
                        removedCoins.Add(clean);
                    }
                    else
                    {
                        notFoundCoins.Add(clean);
                    }
                }

                if (userSettings.PortfolioMode == "Combined")
                {
                    var combSet = new HashSet<string>(Default40Coins);
                    foreach (var c in userSettings.CustomCoins) combSet.Add(c);
                    userSettings.Coins = combSet.ToList();
                }
                else if (userSettings.PortfolioMode == "Custom")
                {
                    userSettings.Coins = new List<string>(userSettings.CustomCoins);
                }

                if (removedCoins.Count > 0)
                {
                    userSettings.LastResumeTime = DateTime.UtcNow;
                    SaveSettings();
                }

                var cleanCust = userSettings.CustomCoins.Count > 0 
                    ? string.Join(", ", userSettings.CustomCoins.Select(c => c.Replace("USDT", ""))) 
                    : "Boşdur";
                var sb = new StringBuilder();
                if (removedCoins.Count > 0)
                {
                    sb.AppendLine($"✅ <b>Silinən coinlər ({removedCoins.Count} ədəd):</b> <code>{string.Join(", ", removedCoins)}</code>\n");
                }
                if (notFoundCoins.Count > 0)
                {
                    sb.AppendLine($"⚠️ <b>Siyahıda tapılmayanlar:</b> <code>{string.Join(", ", notFoundCoins)}</code>\n");
                }
                sb.AppendLine($"🪙 <b>Qalan Fərdi Coinləriniz ({userSettings.CustomCoins.Count} ədəd):</b>\n<code>{cleanCust}</code>\n");
                sb.AppendLine($"📈 <b>Cari İzlənən Ümumi Portfel:</b> <b>{userSettings.Coins.Count} coin</b>");

                await SendMessageAsync(sb.ToString(), chatId, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
                return;
            }

            // STATE: WAITING_ADD_CUSTOM_COIN (➕ Öz coini əlavə et)
            if (!isMenuButtonClick && _userStates.TryGetValue(chatId, out var addCustomState) && addCustomState == "WAITING_ADD_CUSTOM_COIN")
            {
                _userStates.TryRemove(chatId, out _);
                var rawInput = text.Trim();
                var normalized = rawInput.ToUpperInvariant();
                if (normalized.EndsWith("USDT") && normalized.Length > 4)
                {
                    normalized = normalized.Substring(0, normalized.Length - 4);
                }

                if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 2 || normalized.Length > 12 || !System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^[A-Z0-9]+$"))
                {
                    await SendMessageAsync(
                        $"⛔ Bu cütlük tapılmadı.\n“{rawInput}” Binance Futures USDT siyahısında yoxdur.\nDüzgün ticker yazın (məs: SOL, LINK, AVAX).",
                        chatId,
                        TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
                    return;
                }

                if (normalized == "PEPE") normalized = "1000PEPE";
                var targetSymbol = normalized + "USDT";

                if (userSettings.CustomCoins.Contains(targetSymbol))
                {
                    await SendMessageAsync(
                        $"ℹ️ <b>{normalized} artıq fərdi portfelinizdə mövcuddur.</b>\nFərdi coinlər: {userSettings.CustomCoins.Count} ədəd.",
                        chatId,
                        TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
                    return;
                }

                bool existsOnBinance = false;
                try
                {
                    var marketProvider = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
                    var ticker = await marketProvider.Get24hTickerAsync(targetSymbol);
                    if (ticker != null && ticker.Price > 0)
                    {
                        existsOnBinance = true;
                    }
                }
                catch { }

                if (!existsOnBinance && Supported50Coins.Contains(targetSymbol))
                {
                    existsOnBinance = true;
                }

                if (!existsOnBinance)
                {
                    await SendMessageAsync(
                        $"⛔ Bu cütlük tapılmadı.\n“{rawInput}” Binance Futures USDT siyahısında yoxdur.\nDüzgün ticker yazın (məs: SOL, LINK, AVAX).",
                        chatId,
                        TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
                    return;
                }

                userSettings.CustomCoins.Add(targetSymbol);
                if (userSettings.PortfolioMode == "Combined")
                {
                    var combSet = new HashSet<string>(Default40Coins);
                    foreach (var c in userSettings.CustomCoins) combSet.Add(c);
                    userSettings.Coins = combSet.ToList();
                }
                else
                {
                    userSettings.PortfolioMode = "Custom";
                    userSettings.Coins = new List<string>(userSettings.CustomCoins);
                }
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var cleanCust = string.Join(", ", userSettings.CustomCoins.Select(c => c.Replace("USDT", "")));
                var successMsg = $"✅ <b>{normalized} fərdi portfelinizə əlavə olundu!</b>\n\n" +
                                 $"🪙 <b>Fərdi Coinləriniz ({userSettings.CustomCoins.Count} ədəd):</b>\n<code>{cleanCust}</code>\n" +
                                 $"📊 <b>Aktiv Rejim:</b> <b>{(userSettings.PortfolioMode == "Combined" ? "🔥 40 + Fərdi Coin (Kombinə)" : "⭐ Yalnız Fərdi Coinlər")}</b>\n" +
                                 $"📈 <b>Ümumi İzlənən:</b> {userSettings.Coins.Count} ədəd coin.\n\n" +
                                 "<i>Aşağıdan zaman kəsiyini seçərək canlı skaneri aktivləşdirə bilərsiniz:</i>";
                await SendMessageAsync(successMsg, chatId, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
                if (_testModeChats.ContainsKey(chatId))
                {
                    _ = SendMockTestSignalAsync(chatId, userSettings, userSettings.Timeframe);
                }
                return;
            }

            // STATE: ADDING COINS VIA COMMA LIST
            if (!isMenuButtonClick && _userStates.TryGetValue(chatId, out var coinAddState) && coinAddState == "WAITING_COIN_INPUT")
            {
                _userStates.TryRemove(chatId, out _);
                var parts = text.Split(new[] { ',', ' ', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var added = new List<string>();
                var notFound = new List<string>();
                var alreadyInList = new List<string>();

                var marketProvider = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

                foreach (var p in parts)
                {
                    var raw = p.Trim().ToUpperInvariant();
                    if (raw.EndsWith("USDT") && raw.Length > 4) raw = raw.Substring(0, raw.Length - 4);
                    if (raw.Length < 2 || raw.Length > 12 || !System.Text.RegularExpressions.Regex.IsMatch(raw, @"^[A-Z0-9]+$")) continue;

                    if (raw == "PEPE") raw = "1000PEPE";
                    var target = raw + "USDT";
                    if (userSettings.Coins.Contains(target))
                    {
                        alreadyInList.Add(raw);
                        continue;
                    }

                    bool exists = false;
                    try
                    {
                        var ticker = await marketProvider.Get24hTickerAsync(target);
                        if (ticker != null && ticker.Price > 0) exists = true;
                    }
                    catch { }

                    if (!exists && Supported50Coins.Contains(target)) exists = true;

                    if (!exists)
                    {
                        notFound.Add(raw);
                    }
                    else
                    {
                        userSettings.Coins.Add(target);
                        added.Add(raw);
                    }
                }

                if (added.Count > 0)
                {
                    userSettings.LastResumeTime = DateTime.UtcNow;
                    SaveSettings();
                    var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                    
                    var sb = new StringBuilder();
                    sb.AppendLine($"✅ <b>Uğurla əlavə edildi ({added.Count} ədəd):</b> <code>{string.Join(", ", added)}</code>\n");
                    if (notFound.Count > 0)
                    {
                        sb.AppendLine($"⛔ <b>Bu cütlüklər Binance Futures-də tapılmadı:</b> <code>{string.Join(", ", notFound)}</code>\n");
                    }
                    if (alreadyInList.Count > 0)
                    {
                        sb.AppendLine($"ℹ️ <b>Artıq siyahınızda mövcuddur:</b> <code>{string.Join(", ", alreadyInList)}</code>\n");
                    }
                    sb.AppendLine($"📋 <b>Cari Ticarət Siyahınız ({userSettings.Coins.Count} coin):</b>\n<code>{cleanList}</code>");

                    await SendMessageAsync(sb.ToString(), chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }
                else if (notFound.Count > 0)
                {
                    var notFoundStr = string.Join(", ", notFound);
                    var msg = $"⛔ Bu cütlük tapılmadı.\n“{notFoundStr}” Binance Futures USDT siyahısında yoxdur.\nDüzgün ticker yazın (məs: SOL, LINK, AVAX).";
                    await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }
                else if (alreadyInList.Count > 0)
                {
                    var alreadyStr = string.Join(", ", alreadyInList);
                    await SendMessageAsync($"ℹ️ <b>Bu coinlər artıq siyahınızda mövcuddur:</b> <code>{alreadyStr}</code>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }
                else
                {
                    await SendMessageAsync("⚠️ <b>Düzgün coin adı daxil edilmədi.</b>\n📌 <b>Məsələn:</b> <code>SOL, BTC, ETH, DOGE</code>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }
            }

            if (text == "⏱ 15 Dəqiqə (15m) Siqnalları" ||
                text == "⏱ 1 Saat (1h) Siqnalları" ||
                text == "⏱ 4 Saat (4h) Siqnalları" ||
                text == "🌟 Bütün Əsas Zamanlar (15m, 1h, 4h)" ||
                text == "🌟 Bütün Zamanlar (Hamısı) Siqnalları")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "⛔ Ticarət başlamadı.\nSəbəb: heç bir coin seçilməyib.\nƏvvəl ⚙️ Coin Seçimi ilə ən azı 1 coin seçin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }

                // CRITICAL FIX: Evaluate Bütün / Hamısı FIRST before checking 15m/1h/4h
                string targetTf;
                if (text.Contains("Bütün", StringComparison.OrdinalIgnoreCase) || 
                    text.Contains("Butun", StringComparison.OrdinalIgnoreCase) || 
                    text.Contains("Hamısı", StringComparison.OrdinalIgnoreCase) || 
                    text.Contains("Hamisi", StringComparison.OrdinalIgnoreCase) || 
                    text.Contains("15m, 1h, 4h", StringComparison.OrdinalIgnoreCase))
                {
                    targetTf = "Hamısı";
                }
                else if (text.Contains("15m", StringComparison.OrdinalIgnoreCase) || 
                         text.Contains("15 Dəqiqə", StringComparison.OrdinalIgnoreCase) || 
                         text.Contains("15 deqiqe", StringComparison.OrdinalIgnoreCase))
                {
                    targetTf = "15m";
                }
                else if (text.Contains("1h", StringComparison.OrdinalIgnoreCase) || 
                         text.Contains("1 Saat", StringComparison.OrdinalIgnoreCase))
                {
                    targetTf = "1h";
                }
                else if (text.Contains("4h", StringComparison.OrdinalIgnoreCase) || 
                         text.Contains("4 Saat", StringComparison.OrdinalIgnoreCase))
                {
                    targetTf = "4h";
                }
                else
                {
                    targetTf = "Hamısı";
                }

                userSettings.Timeframe = targetTf;
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var tfDisplay = (targetTf == "Hamısı" || targetTf == "Hamisi") ? "15m, 1h, 4h" : targetTf;
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));

                var sb = new StringBuilder();
                sb.AppendLine("✅ <b>Ticarət başladı</b>");
                sb.AppendLine($"⏱ <b>Rejim:</b> <code>{tfDisplay}</code>");
                sb.AppendLine($"🪙 <b>İzlənən:</b> {userSettings.Coins.Count} coin");
                sb.AppendLine($"<code>{cleanList}</code>");

                await SendMessageAsync(sb.ToString(), chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }

            // DIRECT TIMEFRAME PREFERENCE SELECTION
            if (text == "🌟 Bütün Əsas Zamanlar (1h, 4h)" ||
                text == "🌟 Bütün Əsas Zamanlar (15m, 1h, 4h)" || 
                text == "🌟 Bütün Zamanlar (Hamısı)" || 
                text.Equals("Hamisi", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("Hamısı", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("Bütün", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("Butun", StringComparison.OrdinalIgnoreCase))
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "⛔ Ticarət başlamadı.\nSəbəb: heç bir coin seçilməyib.\nƏvvəl ⚙️ Coin Seçimi ilə ən azı 1 coin seçin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                userSettings.Timeframe = "Hamısı";
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var startMsg = $"✅ <b>Ticarət başladı</b>\n" +
                               $"⏱ <b>Rejim:</b> <code>1h, 4h</code>\n" +
                               $"🪙 <b>İzlənən:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "⏱ 15 Dəqiqə (15m)" || text == "15m")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "⛔ Ticarət başlamadı.\nSəbəb: heç bir coin seçilməyib.\nƏvvəl ⚙️ Coin Seçimi ilə ən azı 1 coin seçin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                userSettings.Timeframe = "1h";
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var startMsg = $"✅ <b>Ticarət başladı</b>\n" +
                               $"⏱ <b>Rejim:</b> <code>1h</code> (15m deaktiv edilib)\n" +
                               $"🪙 <b>İzlənən:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "⏱ 1 Saat (1h) Siqnalları" || text == "⏱ 1 Saat (1h)" || text == "1h")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "⛔ Ticarət başlamadı.\nSəbəb: heç bir coin seçilməyib.\nƏvvəl ⚙️ Coin Seçimi ilə ən azı 1 coin seçin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                userSettings.Timeframe = "1h";
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var startMsg = $"✅ <b>Ticarət başladı</b>\n" +
                               $"⏱ <b>Rejim:</b> <code>1h</code>\n" +
                               $"🪙 <b>İzlənən:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "⏱ 4 Saat (4h)" || text == "4h")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "⛔ Ticarət başlamadı.\nSəbəb: heç bir coin seçilməyib.\nƏvvəl ⚙️ Coin Seçimi ilə ən azı 1 coin seçin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                userSettings.Timeframe = "4h";
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var startMsg = $"✅ <b>Ticarət başladı</b>\n" +
                               $"⏱ <b>Rejim:</b> <code>4h</code>\n" +
                               $"🪙 <b>İzlənən:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "📋 Standart 40 Coini Seç" || text == "📋 Standart 16 Coini Seç")
            {
                userSettings.Coins = new List<string>(Default40Coins);
                SaveSettings();
                var cleanList = string.Join(", ", Default40Coins.Select(c => c.Replace("USDT", "")));
                var msg = $"✅ <b>Standart 40 institusional coin seçildi (40/40).</b>\n\n" +
                          $"🪙 <b>İzlənən Coinlər:</b>\n<code>{cleanList}</code>\n\n" +
                          $"📌 İndi menyudan <b>⭐ Mənim Coinlərim</b> ilə ticarətə başlaya bilərsiniz.";
                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "➕ Öz coini əlavə et")
            {
                _userStates[chatId] = "WAITING_ADD_CUSTOM_COIN";
                var prompt = "➕ <b>Öz Coini Əlavə Et</b>\n\n" +
                             "Binance Futures USDT cütlüyü üçün ticker yazın (məsələn: <code>RENDER</code>, <code>SOL</code>, <code>AVAX</code>):";
                await SendMessageAsync(prompt, chatId, new { remove_keyboard = true });
                return;
            }
            else if (text.Contains("Status") || text.Contains("Statusu") || text == "/status" || text == "/version")
            {
                int openCount = 0;
                try
                {
                    openCount = await uow.Signals.GetUserOpenSignalsCountAsync(chatId);
                }
                catch { }

                var lastDeliveredUtc = await uow.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                string? lastTime = lastDeliveredUtc.HasValue 
                    ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) 
                    : null;
                bool canPush = await CanReceivePushAsync(chatId);
                var statusMsg = TelegramMessageFormatter.FormatBotStatus(userSettings, openCount, lastTime, canPush);
                await SendMessageAsync(statusMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text.Contains("Geri") || text.Contains("Əsas Menyu") || text == "/menu" || text == "/help" || text.Contains("Menyu"))
            {
                _userStates.TryRemove(chatId, out _);

                // If Admin Panel was open, close it cleanly
                if (userSettings.IsAdminOpen && userSettings.LastAdminMessageId.HasValue)
                {
                    _ = DeleteMessageAsync(chatId, userSettings.LastAdminMessageId.Value);
                    userSettings.IsAdminOpen = false;
                    userSettings.LastAdminMessageId = null;
                }

                // If opening a new terminal, delete the previous terminal message if it exists to avoid chat spam
                if (userSettings.LastTerminalMessageId.HasValue)
                {
                    _ = DeleteMessageAsync(chatId, userSettings.LastTerminalMessageId.Value);
                    userSettings.LastTerminalMessageId = null;
                }

                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var openCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                var lastTime = lastDeliveredUtc.HasValue 
                    ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) 
                    : "";

                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, openCount, lastTime);
                var inlineKb = TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isAdmin);

                var newMsgId = await SendMessageReturnIdAsync(dashText, chatId, inlineKb);
                userSettings.LastTerminalMessageId = newMsgId;
                userSettings.IsTerminalOpen = true;
                SaveSettings();
                return;
            }
            else if (text.Contains("Dayandır") || text.Contains("Dayandir") || text == "/stop")
            {
                var confirmMsg = TelegramMessageFormatter.FormatStopConfirmPrompt();
                await SendMessageAsync(confirmMsg, chatId, TelegramKeyboards.BuildStopConfirmationKeyboard());
                return;
            }
            else if (text.Contains("Başlat") || text.Contains("Baslat") || text == "/resume" || text == "/start_signals")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "⛔ Ticarət başlamadı.\nSəbəb: heç bir coin seçilməyib.\nƏvvəl ⚙️ Coin Seçimi ilə ən azı 1 coin seçin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                if (string.IsNullOrWhiteSpace(userSettings.Timeframe))
                {
                    await SendMessageAsync(
                        "⛔ Ticarət başlamadı.\nSəbəb: timeframe seçilməyib.\nZəhmət olmasa ticarət üçün zaman kəsiyi seçin.",
                        chatId,
                        TelegramKeyboards.BuildTimeframeKeyboard());
                    return;
                }

                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var tfDisplay = (userSettings.Timeframe == "Hamısı" || userSettings.Timeframe == "Hamisi") ? "1h, 4h" : userSettings.Timeframe;
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));

                var startMsg = $"✅ <b>Ticarət başladı</b>\n" +
                               $"⏱ <b>Rejim:</b> <code>{tfDisplay}</code>\n" +
                               $"🪙 <b>İzlənən:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Sıfırla") || text.Contains("Sifirla") || text == "/clear" || text == "/reset")
            {
                if (!isAdmin)
                {
                    await SendMessageAsync("⛔ <b>Səlahiyyətiniz çatmır!</b>\n\nBu funksiya yalnız Sistem Admininə məxsusdur.", chatId);
                    return;
                }

                var confirmMsg = TelegramMessageFormatter.FormatResetConfirmationPrompt();
                await SendMessageAsync(confirmMsg, chatId, TelegramKeyboards.BuildResetConfirmationKeyboard());
                return;
            }
            else if (text.Contains("Dərin") || text.Contains("Derin") || text == "📈 Dərin Statistika" || text == "📈 Coinlər Üzrə Dərin Statistika" || text == "/coin_stats")
            {
                _userStates.TryRemove(chatId, out _);
                if (!isAdmin)
                {
                    await SendMessageAsync("⛔ Bu bölmə yalnız SuperAdmin üçündür", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, false));
                    return;
                }

                await SendMessageAsync("⏳ <b>Bütün coinlər və zaman çərçivələri üzrə dərin nəticələr hesablanır...</b>", chatId);

                var monitored = Default16Coins;

                var breakdown = await signalEngine.GetCoinPerformanceBreakdownAsync(monitored);
                var report = TelegramMessageFormatter.FormatCoinPerformanceBreakdown(breakdown, monitored);
                await SendMessageAsync(report, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text.Contains("Statistika") || text == "/stats")
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var stats = (isAdmin || chatId == SuperAdminChatId)
                    ? await unitOfWork.Signals.GetPerformanceStatsAsync(userSettings.Timeframe)
                    : await signalEngine.GetUserPerformanceStatsAsync(chatId, userSettings.Timeframe, userSettings.Coins);
                var tfLabel = (string.IsNullOrWhiteSpace(userSettings.Timeframe) || userSettings.Timeframe == "Təyin olunmayıb" || userSettings.Timeframe == "Hamısı" || userSettings.Timeframe == "Hamisi") ? "1h, 4h" : userSettings.Timeframe;
                var msg = TelegramMessageFormatter.FormatPerformanceStats(stats, tfLabel);
                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Coin Seçimi") || text.Contains("Coin Secimi") || text == "/setcoins" || text.Contains("Coinlərim") || text.Contains("Coinlerim") || text == "/my" || text == "⚡ Bütün Siqnallar" || text == "/scan")
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var openCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                var lastTime = lastDeliveredUtc.HasValue ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) : "";
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, openCount, lastTime);
                await SendMessageAsync(dashText, chatId, TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isAdmin));
                return;
            }
            else if (text.Contains("Bitcoin", StringComparison.OrdinalIgnoreCase) || text.Contains("Kompas", StringComparison.OrdinalIgnoreCase) || text.Contains("🧭") || text == "/btc" || text == "/compass")
            {
                var compass = await signalEngine.GetBtcCompassAsync();
                var btcMsg = TelegramMessageFormatter.FormatBtcCompass(compass);
                await SendMessageAsync(btcMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "📰 Xəbərlər" || text.Contains("Xəbər") || text.Contains("Xeber") || text.Equals("News", StringComparison.OrdinalIgnoreCase) || text == "/news" || text.Contains("📰"))
            {
                var newsSummary = await newsService.GetNewsAndSentimentAsync();
                var msg = TelegramMessageFormatter.FormatNewsSentiment(newsSummary);
                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else
            {
                var potentialSym = text.ToUpper();
                if (!potentialSym.EndsWith("USDT")) potentialSym += "USDT";
                var tf = (userSettings.Timeframe == "Hamısı" || userSettings.Timeframe == "Hamisi") ? "1h" : userSettings.Timeframe;
                var sig = await signalEngine.AnalyzeCoinAsync(potentialSym, tf);
                if (sig.SignalType.Contains("LONG") || sig.SignalType.Contains("SHORT"))
                {
                    await SendSignalAlertAsync(sig, chatId);
                }
                else if (sig.SignalType != "MƏLUMAT AZDIR")
                {
                    var cleanSym = sig.Symbol.Replace("USDT", "");
                    var reasonList = sig.AnalysisReasons.Count > 0 
                        ? string.Join("\n• ", sig.AnalysisReasons) 
                        : "Bazar təsdiqlənmiş trend istiqaməti göstərmir.";
                    var analysisMsg = $"🔍 <b>{cleanSym} ({tf}) Canlı Texniki Analiz:</b>\n\n" +
                                      $"⚪ <b>Vəziyyət:</b> <b>NEYTRAL (GÖZLƏMƏ) ⚪</b>\n" +
                                      $"💵 <b>Cari Qiymət:</b> ${sig.CurrentPrice}\n" +
                                      $"🎯 <b>Confluence Balı:</b> {sig.ConfluenceScore}%\n\n" +
                                      $"📊 <b>İndiqator Göstəriciləri:</b>\n" +
                                      $"• {reasonList}\n\n" +
                                      $"ℹ️ <i>Hal-hazırda bu coin üzrə təsdiqlənmiş giriş siqnalı yoxdur. Tələblərə cavab verən (>=75%) giriş yarandıqda canlı siqnal göndəriləcək.</i>";
                    await SendMessageAsync(analysisMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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

        private async Task<FuturesSignal?> ExecuteTestSignalCommandAsync(string chatId, UserSettings userSettings, IServiceScope scope, string directionArg)
        {
            try
            {
                bool isShort = directionArg.Contains("short") || directionArg.Contains("sell") || directionArg.Contains("qisa");
                var dir = isShort ? SignalDirection.Sell : SignalDirection.Buy;
                var sigType = isShort ? "GÜCLÜ SHORT 🔴" : "GÜCLÜ LONG 🟢";

                // Live BTC price or fallback
                decimal currentPrice = 64500.00m;
                try
                {
                    var liveCache = scope.ServiceProvider.GetService<CryptoSense.Application.Services.LivePriceCache>();
                    if (liveCache != null)
                    {
                        var snap = liveCache.GetSnapshot("BTCUSDT");
                        if (snap != null && snap.Last > 0) currentPrice = Math.Round(snap.Last, 2);
                    }
                }
                catch { }

                decimal entry = currentPrice;
                decimal entryLow = Math.Round(entry * 0.9985m, 2);
                decimal entryHigh = Math.Round(entry * 1.0015m, 2);
                decimal sl = isShort ? Math.Round(entry * 1.0150m, 2) : Math.Round(entry * 0.9850m, 2);
                decimal tp1 = isShort ? Math.Round(entry * 0.9850m, 2) : Math.Round(entry * 1.0150m, 2);
                decimal tp2 = isShort ? Math.Round(entry * 0.9700m, 2) : Math.Round(entry * 1.0300m, 2);
                string tf = (userSettings.Timeframe != "Hamısı" && !string.IsNullOrWhiteSpace(userSettings.Timeframe) && userSettings.Timeframe != "Təyin olunmayıb") 
                    ? userSettings.Timeframe 
                    : "1h";

                var testSignal = new FuturesSignal
                {
                    Symbol = "BTCUSDT",
                    SignalType = sigType,
                    Direction = dir,
                    EntryPrice = entry,
                    EntryLow = entryLow,
                    EntryHigh = entryHigh,
                    TakeProfit1 = tp1,
                    TakeProfit2 = tp2,
                    TakeProfit3 = tp2,
                    StopLoss = sl,
                    ConfluenceScore = isShort ? 87.8m : 88.5m,
                    Confidence = isShort ? 88 : 89,
                    Timeframe = tf,
                    GeneratedAt = DateTime.UtcNow,
                    SourceCandleOpenTimeUtc = DateTime.UtcNow,
                    TimestampFormatted = CryptoSense.Domain.Common.TimeHelper.NowFormatted,
                    CandleCloseTimeUtc = DateTime.UtcNow,
                    PriceSource = "ws_last",
                    DataAgeMs = 115,
                    NewsSentimentImpact = isShort ? "BEARISH 🔴" : "BULLISH 🟢",
                    Status = SignalStatus.Open,
                    IsTest = true,
                    SignalAlertSent = false,
                    SignalNumber = 0
                };

                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await uow.Signals.AddAsync(testSignal);
                await uow.SaveChangesAsync();

                // Nömrə: Telegram true-dan SONRA, bir lock, bir sıra (#1,#2,#3…)
                int nextNum = await uow.Signals.GetNextSequentialSignalNumberAsync();
                testSignal.SignalNumber = nextNum;
                _signalUserNumberMap[$"{testSignal.Id}_{chatId}"] = nextNum;

                var cardMsg = TelegramMessageFormatter.FormatSignalAlert(testSignal, nextNum);
                bool sent = await SendMessageAsync(cardMsg, chatId);
                if (sent)
                {
                    try
                    {
                        var committedNum = await uow.Signals.CommitSignalNumberOnSendSuccessAsync(testSignal.Id);
                        if (committedNum > 0)
                        {
                            testSignal.SignalNumber = committedNum;
                        }
                        testSignal.SignalAlertSent = true;
                        await uow.SaveChangesAsync();

                        await uow.Signals.RecordDeliveryAsync(testSignal.Id, chatId, testSignal.SignalNumber);
                        _lastTestSignals[chatId] = testSignal;
                        _signalUserNumberMap[$"{testSignal.Id}_{chatId}"] = testSignal.SignalNumber;

                        userSettings.AlertCounter = Math.Max(userSettings.AlertCounter, testSignal.SignalNumber);
                        userSettings.LastSignalSentUtc = DateTime.UtcNow;
                        userSettings.LastHeartbeatSentUtc = DateTime.UtcNow;
                        SaveSettings();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TestSignal] RecordDelivery error: {ex.Message}");
                    }
                }
                else
                {
                    testSignal.SignalNumber = 0;
                }

                return testSignal;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExecuteTestSignalCommandAsync] Error: {ex.Message}");
                await SendMessageAsync($"⚠️ Test siqnalı göndərilərkən xəta baş verdi: <code>{ex.Message}</code>", chatId);
                return null;
            }
        }

        private async Task<bool> ExecuteTestOutcomeCommandAsync(string chatId, UserSettings userSettings, IServiceScope scope, string outcomeArg)
        {
            try
            {
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                if (!_lastTestSignals.TryGetValue(chatId, out var testSig) || testSig == null)
                {
                    testSig = await uow.Signals.GetLastTestSignalAsync(chatId);
                }

                if (testSig == null)
                {
                    await SendMessageAsync("⚠️ <b>Aktiv test siqnalı tapılmadı!</b>\nƏvvəlcə <code>/test_signal long</code> və ya <code>/test_signal short</code> göndərin.", chatId);
                    return false;
                }

                if (testSig.IsClosed)
                {
                    int closedNum = testSig.SignalNumber > 0 ? testSig.SignalNumber : testSig.Id;
                    await SendMessageAsync($"⚠️ <b>Bu test siqnalı (#{closedNum}) artıq bağlanıb!</b>\nYeni nəticə testi üçün əvvəlcə <code>/test_signal long</code> və ya <code>/test_signal short</code> göndərin.", chatId);
                    return false;
                }

                bool isSl = outcomeArg.Contains("sl") || outcomeArg.Contains("loss") || outcomeArg.Contains("zerer") || outcomeArg.Contains("zərər");

                string outcomeType;
                decimal hitPrice;
                decimal profitPct;

                if (isSl)
                {
                    outcomeType = "Stop Loss (SL)";
                    hitPrice = testSig.StopLoss;
                    decimal lossDist = Math.Abs(testSig.StopLoss - testSig.EntryPrice);
                    profitPct = testSig.EntryPrice > 0 ? -Math.Round((lossDist / testSig.EntryPrice) * 100m, 2) : -1.50m;
                    testSig.Status = SignalStatus.Failed;
                    testSig.CloseReason = "SL";
                    testSig.ClosePrice = hitPrice;
                    testSig.ResultPercent = profitPct;
                    testSig.GrossResultPercent = profitPct;
                    testSig.NetResultPercent = profitPct;
                    testSig.IsClosed = true;
                    testSig.ClosedAt = DateTime.UtcNow;
                    testSig.OutcomeAlertSent = true;
                    testSig.OutcomeStatus = "STOP LOSS 🔴";
                }
                else
                {
                    outcomeType = "Hədəf 1 (TP1)";
                    hitPrice = testSig.TakeProfit1;
                    decimal winDist = Math.Abs(testSig.TakeProfit1 - testSig.EntryPrice);
                    profitPct = testSig.EntryPrice > 0 ? Math.Round((winDist / testSig.EntryPrice) * 100m, 2) : 1.50m;
                    testSig.Status = SignalStatus.Success;
                    testSig.CloseReason = "TP1";
                    testSig.ClosePrice = hitPrice;
                    testSig.ResultPercent = profitPct;
                    testSig.GrossResultPercent = profitPct + 0.10m;
                    testSig.NetResultPercent = profitPct;
                    testSig.IsClosed = true;
                    testSig.ClosedAt = DateTime.UtcNow;
                    testSig.OutcomeAlertSent = true;
                    testSig.OutcomeStatus = "UĞURLU 🟢 (TP1)";
                }

                await uow.Signals.UpdateAsync(testSig);
                await uow.SaveChangesAsync();
                _lastTestSignals.TryRemove(chatId, out _);

                int sigNum = testSig.SignalNumber > 0 ? testSig.SignalNumber : await uow.Signals.GetUserSignalNumberAsync(testSig.Id, chatId);
                if (sigNum == 0) sigNum = 1;

                userSettings.LastHeartbeatSentUtc = DateTime.UtcNow;
                SaveSettings();

                var outcomeMsg = TelegramMessageFormatter.FormatOutcomeAlert(testSig, sigNum, outcomeType, hitPrice, profitPct);
                await SendMessageAsync(outcomeMsg, chatId);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExecuteTestOutcomeCommandAsync] Error: {ex.Message}");
                await SendMessageAsync($"⚠️ Test nəticəsi göndərilərkən xəta baş verdi: <code>{ex.Message}</code>", chatId);
                return false;
            }
        }
    }
}