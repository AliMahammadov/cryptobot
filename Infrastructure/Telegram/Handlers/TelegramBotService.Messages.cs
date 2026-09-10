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
    // Partial class: Incoming message handler and FSM (authentication, admin commands, coin management)
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
                var tf = (timeframe == "Ham─▒s─▒" || timeframe == "Hamisi" || string.IsNullOrWhiteSpace(timeframe) || timeframe == "T╔Öyin olunmay─▒b") ? "1h" : timeframe;

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
                    NewsSentimentImpact = "BULLISH ­şşó",
                    Status = SignalStatus.Open
                };

                // BãÅND 1: Test say─şac─▒ YOX. N├Âmr╔Ö YALNIZ real g├Ând╔Örilmi┼ş siqnala aiddir.
                var userSigNum = 0;

                var alertMsg = TelegramMessageFormatter.FormatSignalAlert(mockSig, userSigNum);
                var note = "­şğ¬ <b>[TEST REJ─░M─░ CANLI S─░MULYAS─░YASI]</b>\n" +
                           "<i>B├╝t├╝n parametrl╔Ör v╔Ö d├╝ym╔Öl╔Ör i┼şl╔Ökdir. G├Ând╔Öril╔Ön test siqnal─▒:</i>\n\n";
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

                        var outcomeMsg = TelegramMessageFormatter.FormatOutcomeAlert(mockSig, userSigNum, "­şÄ» Take Profit 1 (TP1)", tp1, +1.20m);
                        var outNote = "­şğ¬ <b>[TEST REJ─░M─░ NãÅT─░CãÅ S─░MULYAS─░YASI]</b>\n\n";
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
            // EXCEPT for toggle buttons (­şÄø ãÅsas Terminal and ­şææ Admin Paneli) which require immediate double-tap to collapse!
            bool isToggleCommand = text == "­şÄø ãÅsas Terminal" || 
                                   text.Contains("ãÅsas Terminal") || 
                                   text.Contains("Esas Terminal") || 
                                   text == "Terminal" ||
                                   text == "­şææ Admin Paneli" || 
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

            // ZERO-PASSWORD ADMIN DIRECT ACCESS:
            // Admin (Ali) is recognized instantly by Telegram UserId (1219998176), TelegramUsername (@Ali_Mahammadov),
            // or SuperAdmin ChatId. No password is ever asked!
            if (isAdminUser)
            {
                _authenticatedSessions[chatId] = "Ali";
                SuperAdminChatId = chatId;
                _loggedOutChats.TryRemove(chatId, out _);
                _userStates.TryRemove(chatId, out _);
                var aSettings = GetSettings(chatId);
                aSettings.TelegramUserId = userId ?? 1219998176;
                aSettings.Username = "Ali (Super Admin)";
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
                text.Equals("testi dayand─▒r", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("testi dayandir", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("test bitdi", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("ba┼şla", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("basla", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("test rejimi", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("testi ba┼şlat", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/adduser", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/createuser", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("/deluser", StringComparison.OrdinalIgnoreCase);

            if (!isAdminUser && isRestrictedAdminCommand)
            {
                _ = DeleteMessageAsync(chatId, messageId);
                await SendMessageAsync(
                    "Ôøö <b>S╔Ölahiyy╔Ötiniz ├ğatm─▒r!</b>\n\nBu ╔Ömr v╔Ö ya funksiya yaln─▒z Sistem Adminin╔Ö m╔Öxsusdur.", 
                    chatId);
                return;
            }

            bool isMenuButtonClick = text.StartsWith("­şğ¡") || text.StartsWith("ÔÜí") || text.StartsWith("Ô¡É") || 
                                     text.StartsWith("­şôè") || text.StartsWith("­şôê") || text.StartsWith("ÔÜÖ´©Å") || 
                                     text.StartsWith("­şùæ") || text.StartsWith("ÔÅ▒") || text.StartsWith("­şîş") || 
                                     text.StartsWith("­şğ╣") || text.StartsWith("­şøæ") || text.StartsWith("ÔûÂ´©Å") || 
                                     text.StartsWith("­şô░") || text.StartsWith("Ô¼à´©Å") || text.StartsWith("­şæÑ") || 
                                     text.StartsWith("­şöæ") || text.StartsWith("­şææ") || text.StartsWith("­şôï") || 
                                     text.StartsWith("Ôä╣´©Å") || text.StartsWith("­şôÑ") || text.StartsWith("­şÄø") ||
                                     text == "ÔŞò ├ûz coini ╔Ölav╔Ö et" || text == "ÔŞò ─░stifad╔Ö├ği Yarat" ||
                                     text.Contains("Siqnallar") || text.Contains("Menyu") || text.Contains("Statistika") ||
                                     (text.StartsWith("/") && !text.StartsWith("/login", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("/admin ", StringComparison.OrdinalIgnoreCase));

            // =========================================================================
            // 0. LOGOUT COMMAND (ALWAYS CLEARS DATABASE & SESSION)
            // =========================================================================
            if (text == "/logout" || text.Equals("/cixis", StringComparison.OrdinalIgnoreCase) || text.Equals("/exit", StringComparison.OrdinalIgnoreCase) || text.Equals("├ç─▒x─▒┼ş", StringComparison.OrdinalIgnoreCase) || text.Equals("Cixis", StringComparison.OrdinalIgnoreCase) || text.Equals("logout", StringComparison.OrdinalIgnoreCase))
            {
                _ = DeleteMessageAsync(chatId, messageId);
                _authenticatedSessions.TryRemove(chatId, out _);
                _loggedOutChats[chatId] = true;
                UserPreferences.TryRemove(chatId, out _);
                _userStates.TryRemove(chatId, out _);
                if (SuperAdminChatId == chatId) SuperAdminChatId = null;

                await userManager.LogoutAsync(chatId, userId);
                await userManager.ClearChatBindingAsync(chatId, userId);

                await SendMessageAsync(
                    "­şæï <b>Hesab─▒n─▒zdan ├ğ─▒x─▒┼ş edildi!</b>\n\n" +
                    "Yenid╔Ön daxil olmaq ├╝├ğ├╝n <b>─░stifad╔Ö├ği Ad─▒n─▒z─▒</b> v╔Ö <b>Parolunuzu</b> yaz─▒n:\n" +
                    "­şÆí <b>N├╝mun╔Ö:</b> <code>Murad 123456</code>", 
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

                    var welcomeAdmin = $"­şææ <b>Xo┼ş G╔Öldiniz, Ba┼ş Admin!</b>\n\n" +
                                       $"­şÜÇ <b>CryptoSense Terminal Xidm╔Öti AKT─░VD─░R ­şşó</b>\n\n" +
                                       $"Terminal─▒ a├ğmaq ├╝├ğ├╝n a┼şa─ş─▒dak─▒ <b>­şÄø ãÅsas Terminal</b> d├╝ym╔Ösin╔Ö toxunun.";
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
                    var welcomeAndAuth = "­şæï <b>Salam! Kripto Signals Bot Xidm╔Ötin╔Ö xo┼ş g╔Ölmisiniz.</b>\n\n" +
                                         "ÔÜá´©Å <b>Sistemd╔Ön istifad╔Ö etm╔Ök ├╝├ğ├╝n daxil olmal─▒s─▒n─▒z!</b>\n\n" +
                                         "Sistem╔Ö daxil olmaq ├╝├ğ├╝n <b>─░stifad╔Ö├ği Ad─▒n─▒z─▒</b> v╔Ö <b>Parolunuzu</b> bir s╔Ötird╔Ö, aralar─▒nda bo┼şluq qoyaraq yaz─▒n:\n\n" +
                                         "­şÆí <b>N├╝mun╔Ö:</b>\n" +
                                         "<code>Murad 123456</code>\n\n" +
                                         "-----------------------------------\n" +
                                         "Hesab─▒n─▒z yoxdur? Qeydiyyat v╔Ö giri┼ş icaz╔Ösi ├╝├ğ├╝n <b>Admin</b> il╔Ö ╔Ölaq╔Ö saxlay─▒n:\n" +
                                         "­şæë <a href=\"https://t.me/Ali_Mahammadov\">@Ali_Mahammadov</a>";

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

                    var greeting = $"­şæï <b>Salam, {sessionUser}! Kripto Signals Bot haz─▒rd─▒r ­şşó</b>\n\n" +
                                   "Terminal─▒ a├ğmaq ├╝├ğ├╝n a┼şa─ş─▒dak─▒ <b>­şÄø ãÅsas Terminal</b> d├╝ym╔Ösin╔Ö toxunun.";
                    await SendMessageAsync(greeting, chatId, TelegramKeyboards.BuildUserKeyboard(uSettings, isAdmin: false));
                    _ = BuildAndSendPortfolioSummaryAsync(chatId, uSettings, forceRefresh: false);
                    return;
                }
            }

            // =========================================================================
            // 1. AUTHENTICATION GATING (STRICT: NO AUTO-LOGIN BYPASS)
            // =========================================================================
            string? currentUsername = null;
            bool isAuthenticated = isAdminUser;
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
                        settings.IsActive = false;
                        settings.Timeframe = "T╔Öyin olunmay─▒b";
                        settings.PortfolioMode = "T╔Öyin olunmay─▒b";
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

                            var welcomeAdmin = $"­şææ <b>Xo┼ş G╔Öldiniz, Ba┼ş Admin ({user.Username})!</b>\n\n" +
                                               $"­şÜÇ <b>CryptoSense Terminal Xidm╔Öti AKT─░VD─░R ­şşó</b>\n\n" +
                                               $"Terminal─▒ a├ğmaq ├╝├ğ├╝n a┼şa─ş─▒dak─▒ <b>­şÄø ãÅsas Terminal</b> d├╝ym╔Ösin╔Ö toxunun.\n\n" +
                                               $"<i>├ç─▒x─▒┼ş etm╔Ök ├╝├ğ├╝n: <code>/logout</code></i>";

                            await SendMessageAsync(welcomeAdmin, chatId, TelegramKeyboards.BuildUserKeyboard(settings, isAdmin: true));
                            return;
                        }
                        else
                        {
                            settings.Username = user.Username;
                            SaveSettings();

                            var onboardingMsg = $"Ô£à <b>Giri┼ş T╔Ösdiql╔Öndi! Xo┼ş G╔Öldiniz, {user.Username}!</b>\n\n" +
                                                $"­şÜÇ <b>Kripto Signals Bot Xidm╔Öti AKT─░VD─░R ­şşó</b>\n\n" +
                                                $"Terminal─▒ a├ğmaq ├╝├ğ├╝n a┼şa─ş─▒dak─▒ <b>­şÄø ãÅsas Terminal</b> d├╝ym╔Ösin╔Ö toxunun.\n\n" +
                                                $"<i>├ç─▒x─▒┼ş etm╔Ök ├╝├ğ├╝n: <code>/logout</code></i>";
                            
                            await SendMessageAsync(onboardingMsg, chatId, TelegramKeyboards.BuildUserKeyboard(settings, isAdmin: false));
                            await NotifySuperAdminUserLoginAsync(user.Username, $"Telegram (@{telegramUsername})");
                            return;
                        }
                    }
                    else
                    {
                        _ = DeleteMessageAsync(chatId, messageId);
                        var failMsg = "ÔØî <b>Giri┼ş U─şursuz Oldu!</b>\n\n" +
                                      "─░stifad╔Ö├ği ad─▒ v╔Ö ya parol yaln─▒┼şd─▒r.\n" +
                                      "Z╔Öhm╔Öt olmasa m╔Ölumatlar─▒n─▒z─▒ yoxlay─▒b yenid╔Ön daxil edin:\n\n" +
                                      "­şÆí <b>N├╝mun╔Ö:</b> <code>Murad 123456</code>\n\n" +
                                      "<i>Hesab─▒n─▒z yoxdursa, Admin (<a href=\"https://t.me/Ali_Mahammadov\">@Ali_Mahammadov</a>) il╔Ö ╔Ölaq╔Ö saxlay─▒n.</i>";

                        await SendMessageAsync(failMsg, chatId, new { remove_keyboard = true });
                        return;
                    }
                }
                else
                {
                    _ = DeleteMessageAsync(chatId, messageId);
                    var welcomeAndAuth = "­şæï <b>Salam! Kripto Signals Bot Xidm╔Ötin╔Ö xo┼ş g╔Ölmisiniz.</b>\n\n" +
                                         "ÔÜá´©Å <b>Sistemd╔Ön istifad╔Ö etm╔Ök ├╝├ğ├╝n daxil olmal─▒s─▒n─▒z!</b>\n\n" +
                                         "Sistem╔Ö daxil olmaq ├╝├ğ├╝n <b>─░stifad╔Ö├ği Ad─▒n─▒z─▒</b> v╔Ö <b>Parolunuzu</b> bir s╔Ötird╔Ö, aralar─▒nda bo┼şluq qoyaraq yaz─▒n:\n\n" +
                                         "­şÆí <b>N├╝mun╔Ö:</b>\n" +
                                         "<code>Murad 123456</code>\n\n" +
                                         "-----------------------------------\n" +
                                         "Hesab─▒n─▒z yoxdur? Qeydiyyat v╔Ö giri┼ş icaz╔Ösi ├╝├ğ├╝n <b>Admin</b> il╔Ö ╔Ölaq╔Ö saxlay─▒n:\n" +
                                         "­şæë <a href=\"https://t.me/Ali_Mahammadov\">@Ali_Mahammadov</a>";

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
                await SendMessageAsync("ÔÜá´©Å Sessiya bitmi┼şdir. Z╔Öhm╔Öt olmasa yenid╔Ön daxil olun.", chatId, new { remove_keyboard = true });
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
                            var msg = $"Ô£à <b>─░stifad╔Ö├ği u─şurla yarad─▒ld─▒!</b>\n\n" +
                                      $"­şæñ <b>─░stifad╔Ö├ği Ad─▒:</b> <code>{newUsername}</code>\n" +
                                      $"­şöæ <b>Parol:</b> <code>{newPassword}</code>\n\n" +
                                      $"<i>─░stifad╔Ö├ğiy╔Ö bildirin ki, bota daxil olaraq <code>{newUsername} {newPassword}</code> yazs─▒n.</i>";
                            await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                        }
                        else
                        {
                            await SendMessageAsync($"ÔÜá´©Å <b>X╔Öta:</b> <code>{newUsername}</code> adl─▒ istifad╔Ö├ği art─▒q m├Âvcuddur!", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                        }
                    }
                    else
                    {
                        await SendMessageAsync("ÔÜá´©Å <b>X╔Öta:</b> D├╝zg├╝n format daxil edin!\nFormat: <code>istifad╔Ö├ği_ad─▒ parol</code> (m╔Ös╔Öl╔Ön: <code>murad 123456</code>)", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    return;
                }
                else if (admState == "ADMIN_WAITING_DELETE_USER")
                {
                    _userStates.TryRemove(chatId, out _);
                    var userToDelete = text.Trim();
                    if (string.IsNullOrWhiteSpace(userToDelete))
                    {
                        await SendMessageAsync("ÔÜá´©Å <b>X╔Öta:</b> Silin╔Öc╔Ök istifad╔Ö├ği ad─▒n─▒ qeyd edin!", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                        return;
                    }
                    var deleted = await userManager.DeleteUserAsync(userToDelete);
                    if (deleted)
                    {
                        await RevokeUserSessionAsync(userToDelete);
                        await SendMessageAsync($"Ô£à <b>─░stifad╔Ö├ği '{userToDelete}' sistemd╔Ön silindi v╔Ö b├╝t├╝n prosesl╔Öri dayand─▒r─▒ld─▒!</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    else
                    {
                        await SendMessageAsync($"ÔÜá´©Å <b>'{userToDelete}' tap─▒lmad─▒ v╔Ö ya silin╔Ö bilm╔Öz.</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
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
                            await SendMessageAsync($"Ô£à <b>'{uName}' ├╝├ğ├╝n yeni parol t╔Öyin edildi:</b> <code>{newPwd}</code>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                        }
                        else
                        {
                            await SendMessageAsync($"ÔÜá´©Å <b>'{uName}' adl─▒ istifad╔Ö├ği tap─▒lmad─▒!</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                        }
                    }
                    else
                    {
                        await SendMessageAsync("ÔÜá´©Å <b>X╔Öta:</b> D├╝zg├╝n format daxil edin!\nFormat: <code>istifad╔Ö├ği_ad─▒ yeni_parol</code> (m╔Ös╔Öl╔Ön: <code>murad yeniParol123</code>)", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    return;
                }
            }

            // =========================================================================
            // 3. ADMIN SWITCH & CRUD FLOW
            // =========================================================================
            bool isAdminToggleClick = isAdmin && (text == "­şææ Admin Paneli" || 
                                                 text.Contains("Admin Paneli", StringComparison.OrdinalIgnoreCase) || 
                                                 text.Equals("/admin", StringComparison.OrdinalIgnoreCase));

            if (isAdminToggleClick)
            {
                _userStates.TryRemove(chatId, out _);
                _ = DeleteMessageAsync(chatId, messageId);

                // Toggle logic: If user clicked "­şææ Admin Paneli" and it is already open, close it cleanly
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
            bool isExplicitTerminal = text == "­şÄø ãÅsas Terminal" || 
                                      text.Contains("ãÅsas Terminal") || 
                                      text.Contains("Esas Terminal") || 
                                      text == "Terminal";

            if (isExplicitTerminal)
            {
                _userStates.TryRemove(chatId, out _);
                _ = DeleteMessageAsync(chatId, messageId);

                // Toggle logic: If user clicked ­şÄø ãÅsas Terminal and it is already open, cleanly collapse it into place!
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
                var openCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default 
                    ? "" 
                    : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);

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
            // 3.2 TEST REJ─░M─░ ãÅMRLãÅR─░ (YALNIZ ADM─░N VãÅ YALNIZ "start test" YAZILDIQDA)
            // =========================================================================
            bool isStartTestCommand = isAdmin && (
                text.Equals("start test", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("/start test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("start_test", StringComparison.OrdinalIgnoreCase));

            bool isStopTestCommand = isAdmin && (
                text.Equals("stop test", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("/stop test", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("/teststop", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("testi dayand─▒r", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("testi dayandir", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("stop_test", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("test bitdi", StringComparison.OrdinalIgnoreCase));

            if (isStartTestCommand)
            {
                _testModeChats[chatId] = true;
                var prompt = "­şğ¬ <b>─░nteraktiv Test Rejimi AKT─░VD─░R! ­şşó</b>\n\n" +
                             "Sistem test m├╝hitin╔Ö ke├ğdi. ─░ndi se├ğil╔Ön portfell╔Ör v╔Ö zaman aral─▒qlar─▒ ├╝zr╔Ö simulyasiya siqnallar─▒ g├Ând╔Öril╔Öc╔Ök:\n\n" +
                             "1´©ÅÔâú <b>­ş¬Ö Standart 40 Coin:</b> Terminalda 1h v╔Ö ya 4h se├ğdikd╔Ö d╔Örhal n├╝mun╔Övi test siqnal─▒ v╔Ö 6 saniy╔Ö sonra TP1 n╔Ötic╔Ö bildiri┼şi g╔Öl╔Öc╔Ök.\n" +
                             "2´©ÅÔâú <b>Ô¡É M╔Önim Coinl╔Örim:</b> 'ÔŞò Coin ãÅlav╔Ö Et' (m╔Ös: SOL) v╔Ö ya '­şùæ Coin Sil' ed╔Ör╔Ök f╔Ördi portfelinizi canl─▒ yoxlaya bil╔Örsiniz.\n" +
                             "3´©ÅÔâú <b>­şöÑ 40 + F╔Ördi Coinl╔Ör (Kombin╔Ö):</b> H╔Öm 40 coin, h╔Öm d╔Ö ╔Ölav╔Ö etdiyiniz f╔Ördi coinl╔Ör ├╝zr╔Ö test ed╔Ö bil╔Örsiniz.\n" +
                             "4´©ÅÔâú <b>Ôä╣´©Å Sistem Statusu & ­şğ¡ Bitcoin Trend:</b> B├╝t├╝n g├Âst╔Öricil╔Ör an─▒nda ├ğat─▒n─▒za t╔Öqdim olunacaq.\n\n" +
                             "<i>Testi bitirm╔Ök ├╝├ğ├╝n: <b>stop test</b> (v╔Ö ya <code>/teststop</code>) yaz─▒n.</i>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                _ = SendMockTestSignalAsync(chatId, userSettings, userSettings.Timeframe);
                return;
            }

            if (isStopTestCommand)
            {
                _testModeChats.TryRemove(chatId, out _);
                var stopMsg = "­şÅü <b>Test Rejimi Dayand─▒r─▒ld─▒! ­şö┤</b>\n\n" +
                              "Ô£à Sistem 100% r╔Ösmi 24/7 canl─▒ real bazar skanerin╔Ö qay─▒td─▒ ­şşó.\n" +
                              "Art─▒q yaln─▒z real bazar qaydalar─▒na (Confluence >= 78%, R:R >= 1.30) cavab ver╔Ön real siqnallar g├Ând╔Öril╔Öc╔Ök.";
                await SendMessageAsync(stopMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }

            if (text == "­şôè ãÅsas Menyu (Siqnallar)" || text == "Ô¼à´©Å ãÅsas Menyu" || text == "/menu")
            {
                _userStates.TryRemove(chatId, out _);
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var openCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, openCount, lastTime);
                await SendMessageAsync(dashText, chatId, TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isAdmin));
                _ = BuildAndSendPortfolioSummaryAsync(chatId, userSettings, forceRefresh: false);
                return;
            }

            if (isAdmin && (text.StartsWith("/adduser", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/createuser", StringComparison.OrdinalIgnoreCase) || text == "ÔŞò ─░stifad╔Ö├ği Yarat"))
            {
                var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 3 && !text.Equals("ÔŞò ─░stifad╔Ö├ği Yarat", StringComparison.OrdinalIgnoreCase))
                {
                    _userStates.TryRemove(chatId, out _);
                    var newUsername = parts[1];
                    var newPassword = string.Join(" ", parts.Skip(2));
                    var created = await userManager.CreateUserAsync(newUsername, newPassword);
                    if (created)
                    {
                        var msg = $"Ô£à <b>─░stifad╔Ö├ği u─şurla yarad─▒ld─▒ v╔Ö bazaya yaz─▒ld─▒!</b>\n\n" +
                                  $"­şæñ <b>─░stifad╔Ö├ği Ad─▒:</b> <code>{newUsername}</code>\n" +
                                  $"­şöæ <b>Parol:</b> <code>{newPassword}</code>\n\n" +
                                  $"<i>─░stifad╔Ö├ğiy╔Ö bildirin ki, bota daxil olaraq <code>{newUsername} {newPassword}</code> yazs─▒n.</i>";
                        await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    else
                    {
                        await SendMessageAsync($"ÔÜá´©Å <b>X╔Öta:</b> <code>{newUsername}</code> adl─▒ istifad╔Ö├ği art─▒q m├Âvcuddur!", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    return;
                }

                _userStates[chatId] = "ADMIN_WAITING_CREATE_USER";
                var prompt = "ÔŞò <b>Yeni ─░stifad╔Ö├ği Yaratmaq</b>\n\n" +
                             "Yaratmaq ist╔Ödiyiniz <b>─░stifad╔Ö├ği Ad─▒n─▒</b> v╔Ö <b>Parolu</b> aralar─▒nda bo┼şluq qoyaraq yaz─▒n:\n\n" +
                             "­şôî <b>M╔Ös╔Öl╔Ön:</b>\n" +
                             "<code>Murad 123456</code>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                return;
            }
            if (isAdmin && (text == "­şæÑ ─░stifad╔Ö├ğil╔Örin Siyah─▒s─▒" || text == "/users"))
            {
                _userStates.TryRemove(chatId, out _);
                var users = await userManager.GetAllUsersAsync();
                var msg = TelegramMessageFormatter.FormatUserList(users);
                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                return;
            }
            if (isAdmin && (text.StartsWith("/deleteuser", StringComparison.OrdinalIgnoreCase) || text == "­şùæ ─░stifad╔Ö├ği Sil"))
            {
                var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && !text.Equals("­şùæ ─░stifad╔Ö├ği Sil", StringComparison.OrdinalIgnoreCase))
                {
                    _userStates.TryRemove(chatId, out _);
                    var userToDelete = parts[1];
                    var deleted = await userManager.DeleteUserAsync(userToDelete);
                    if (deleted)
                    {
                        await RevokeUserSessionAsync(userToDelete);
                        await SendMessageAsync($"Ô£à <b>─░stifad╔Ö├ği '{userToDelete}' sistemd╔Ön silindi v╔Ö b├╝t├╝n prosesl╔Öri dayand─▒r─▒ld─▒!</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    else
                    {
                        await SendMessageAsync($"ÔÜá´©Å <b>'{userToDelete}' tap─▒lmad─▒ v╔Ö ya silin╔Ö bilm╔Öz.</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    return;
                }

                _userStates[chatId] = "ADMIN_WAITING_DELETE_USER";
                var prompt = "­şùæ <b>─░stifad╔Ö├ği Silm╔Ök</b>\n\n" +
                             "Sistemd╔Ön silm╔Ök ist╔Ödiyiniz istifad╔Ö├ğinin <b>Ad─▒n─▒</b> yaz─▒n:\n\n" +
                             "­şôî <b>M╔Ös╔Öl╔Ön:</b>\n" +
                             "<code>Murad</code>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                return;
            }
            if (isAdmin && (text.StartsWith("/resetpwd", StringComparison.OrdinalIgnoreCase) || text == "­şöæ Parolu D╔Öyi┼ş"))
            {
                var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 3 && !text.Equals("­şöæ Parolu D╔Öyi┼ş", StringComparison.OrdinalIgnoreCase))
                {
                    _userStates.TryRemove(chatId, out _);
                    var uName = parts[1];
                    var newPwd = string.Join(" ", parts.Skip(2));
                    var changed = await userManager.ResetPasswordAsync(uName, newPwd);
                    if (changed)
                    {
                        await SendMessageAsync($"Ô£à <b>'{uName}' ├╝├ğ├╝n yeni parol t╔Öyin edildi:</b> <code>{newPwd}</code>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    else
                    {
                        await SendMessageAsync($"ÔÜá´©Å <b>'{uName}' adl─▒ istifad╔Ö├ği tap─▒lmad─▒!</b>", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    }
                    return;
                }

                _userStates[chatId] = "ADMIN_WAITING_RESET_PWD";
                var prompt = "­şöæ <b>─░stifad╔Ö├ği Parolunu D╔Öyi┼şm╔Ök</b>\n\n" +
                             "─░stifad╔Ö├ği ad─▒n─▒ v╔Ö yeni parolu aralar─▒nda bo┼şluqla yaz─▒n:\n\n" +
                             "­şôî <b>M╔Ös╔Öl╔Ön:</b>\n" +
                             "<code>Murad yeni123</code>";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildBackToAdminKeyboard());
                return;
            }

            if (isAdmin && (text == "/db" || text == "/getdb" || text == "/backup_db" || text == "­şôÑ Bazan─▒ Y├╝kl╔Ö" || text.Contains("Bazan─▒ Y├╝kl╔Ö") || text.Contains("Bazani Yukle")))
            {
                _userStates.TryRemove(chatId, out _);
                var volumeEnv = Environment.GetEnvironmentVariable("RAILWAY_VOLUME_MOUNT_PATH");
                var currentDataDir = !string.IsNullOrEmpty(volumeEnv) && Directory.Exists(volumeEnv)
                    ? volumeEnv
                    : (Directory.Exists("/app/data") ? "/app/data" : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
                var currentDbPath = Path.Combine(currentDataDir, "cryptosense.db");

                if (File.Exists(currentDbPath))
                {
                    var activeUsers = await userManager.GetAllUsersAsync();
                    var cap = $"­şÆ¥ <b>CryptoSense SQLite Veril╔Önl╔Ör Bazas─▒</b>\n\n" +
                              $"­şôü <b>Fayl:</b> <code>{currentDbPath}</code>\n" +
                              $"­şæÑ <b>Aktiv ─░stifad╔Ö├ği Say─▒:</b> <b>{activeUsers.Count} n╔Öf╔Ör</b>\n" +
                              $"­şôà <b>Tarix:</b> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n\n" +
                              $"<i>Fayl─▒ y├╝kl╔Öy╔Ör╔Ök birba┼şa DB Browser for SQLite il╔Ö b├╝t├╝n c╔Ödv╔Öll╔Ör╔Ö v╔Ö istifad╔Ö├ğil╔Ör╔Ö baxa bil╔Örsiniz.</i>";
                    await SendMessageAsync("ÔÅ│ Baza fayl─▒ haz─▒rlan─▒r v╔Ö ├ğat─▒n─▒za g├Ând╔Örilir...", chatId);
                    await SendDocumentAsync(currentDbPath, chatId, cap);
                }
                else
                {
                    await SendMessageAsync($"ÔÜá´©Å Baza fayl─▒ tap─▒lmad─▒: <code>{currentDbPath}</code>", chatId);
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

                        if (rowCount == 0) sb.AppendLine("(0 s╔Ötir tap─▒ld─▒)");
                        var resultMsg = $"­şôè <b>SQL N╔Ötic╔Ösi ({rowCount} s╔Ötir):</b>\n\n<pre><code>{System.Net.WebUtility.HtmlEncode(sb.ToString())}</code></pre>";
                        await SendMessageAsync(resultMsg, chatId);
                    }
                    else
                    {
                        int affected = await dbContext.Database.ExecuteSqlRawAsync(query);
                        await SendMessageAsync($"Ô£à <b>ãÅm╔Öliyyat icra olundu. T╔Ösirl╔Ön╔Ön s╔Ötir say─▒: {affected}</b>", chatId);
                    }
                }
                catch (Exception ex)
                {
                    await SendMessageAsync($"ÔÜá´©Å <b>SQL X╔Ötas─▒:</b> <code>{System.Net.WebUtility.HtmlEncode(ex.Message)}</code>", chatId);
                }
                return;
            }

            if (isAdmin && (text == "­şîÉ B├╝t├╝n Coinl╔Örin Siyah─▒s─▒" || text == "/all_coins"))
            {
                _userStates.TryRemove(chatId, out _);
                var monitored = Default40Coins;

                var cleanCoins = monitored.Select(c => c.Replace("USDT", "")).Distinct().ToList();
                var msg = "­şîÉ <b>Sistemin Canl─▒ ─░zl╔Ödiyi B├╝t├╝n Coinl╔Ör v╔Ö Zamanlar</b>\n\n" +
                          $"­şôè <b>├£mumi Coin Say─▒:</b> <b>{cleanCoins.Count} ╔Öd╔Öd (Standart ─░nstitusional 40)</b>\n" +
                          $"­ş¬Ö <b>─░zl╔Ön╔Ön Coinl╔Ör:</b>\n<code>{string.Join(", ", cleanCoins)}</code>\n\n" +
                          "ÔÅ▒ <b>D├Âvri Olaraq Analiz Olunan ┼Şamlar:</b>\n" +
                          "ÔÇó <b>1 Saat (1h)</b> ÔÇö Orta m├╝dd╔Ötli g├╝cl├╝ dal─şa\n" +
                          "ÔÇó <b>4 Saat (4h)</b> ÔÇö ãÅsas makro trend v╔Ö g├╝cl├╝ s╔Öviyy╔Öl╔Ör\n\n" +
                          "­şöı <b>Skan Mexanizmi:</b>\n" +
                          $"Sistem arxa fonda h╔Ör 10 saniy╔Öd╔Ön bir bu {cleanCoins.Count} coinin h╔Ör birini aktiv zaman k╔Ösiyind╔Ö (EMA, MACD, RSI, ATR, Confluence v╔Ö BTC Kompas─▒) analiz edir v╔Ö Confluence >= 78% olanda ┼şam kilidi il╔Ö istifad╔Ö├ğil╔Ör╔Ö ├ğatd─▒r─▒r.";

                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
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
                    : "Bo┼şdur";
                var sb = new StringBuilder();
                if (removedCoins.Count > 0)
                {
                    sb.AppendLine($"Ô£à <b>Silin╔Ön coinl╔Ör ({removedCoins.Count} ╔Öd╔Öd):</b> <code>{string.Join(", ", removedCoins)}</code>\n");
                }
                if (notFoundCoins.Count > 0)
                {
                    sb.AppendLine($"ÔÜá´©Å <b>Siyah─▒da tap─▒lmayanlar:</b> <code>{string.Join(", ", notFoundCoins)}</code>\n");
                }
                sb.AppendLine($"­ş¬Ö <b>Qalan F╔Ördi Coinl╔Öriniz ({userSettings.CustomCoins.Count} ╔Öd╔Öd):</b>\n<code>{cleanCust}</code>\n");
                sb.AppendLine($"­şôê <b>Cari ─░zl╔Ön╔Ön ├£mumi Portfel:</b> <b>{userSettings.Coins.Count} coin</b>");

                await SendMessageAsync(sb.ToString(), chatId, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
                return;
            }

            // STATE: WAITING_ADD_CUSTOM_COIN (ÔŞò ├ûz coini ╔Ölav╔Ö et)
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
                        $"Ôøö Bu c├╝tl├╝k tap─▒lmad─▒.\nÔÇ£{rawInput}ÔÇØ Binance Futures USDT siyah─▒s─▒nda yoxdur.\nD├╝zg├╝n ticker yaz─▒n (m╔Ös: SOL, LINK, AVAX).",
                        chatId,
                        TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
                    return;
                }

                if (normalized == "PEPE") normalized = "1000PEPE";
                var targetSymbol = normalized + "USDT";

                if (userSettings.CustomCoins.Contains(targetSymbol))
                {
                    await SendMessageAsync(
                        $"Ôä╣´©Å <b>{normalized} art─▒q f╔Ördi portfelinizd╔Ö m├Âvcuddur.</b>\nF╔Ördi coinl╔Ör: {userSettings.CustomCoins.Count} ╔Öd╔Öd.",
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
                        $"Ôøö Bu c├╝tl├╝k tap─▒lmad─▒.\nÔÇ£{rawInput}ÔÇØ Binance Futures USDT siyah─▒s─▒nda yoxdur.\nD├╝zg├╝n ticker yaz─▒n (m╔Ös: SOL, LINK, AVAX).",
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
                var successMsg = $"Ô£à <b>{normalized} f╔Ördi portfeliniz╔Ö ╔Ölav╔Ö olundu!</b>\n\n" +
                                 $"­ş¬Ö <b>F╔Ördi Coinl╔Öriniz ({userSettings.CustomCoins.Count} ╔Öd╔Öd):</b>\n<code>{cleanCust}</code>\n" +
                                 $"­şôè <b>Aktiv Rejim:</b> <b>{(userSettings.PortfolioMode == "Combined" ? "­şöÑ 40 + F╔Ördi Coin (Kombin╔Ö)" : "Ô¡É Yaln─▒z F╔Ördi Coinl╔Ör")}</b>\n" +
                                 $"­şôê <b>├£mumi ─░zl╔Ön╔Ön:</b> {userSettings.Coins.Count} ╔Öd╔Öd coin.\n\n" +
                                 "<i>A┼şa─ş─▒dan zaman k╔Ösiyini se├ğ╔Ör╔Ök canl─▒ skaneri aktivl╔Ö┼şdir╔Ö bil╔Örsiniz:</i>";
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
                    sb.AppendLine($"Ô£à <b>U─şurla ╔Ölav╔Ö edildi ({added.Count} ╔Öd╔Öd):</b> <code>{string.Join(", ", added)}</code>\n");
                    if (notFound.Count > 0)
                    {
                        sb.AppendLine($"Ôøö <b>Bu c├╝tl├╝kl╔Ör Binance Futures-d╔Ö tap─▒lmad─▒:</b> <code>{string.Join(", ", notFound)}</code>\n");
                    }
                    if (alreadyInList.Count > 0)
                    {
                        sb.AppendLine($"Ôä╣´©Å <b>Art─▒q siyah─▒n─▒zda m├Âvcuddur:</b> <code>{string.Join(", ", alreadyInList)}</code>\n");
                    }
                    sb.AppendLine($"­şôï <b>Cari Ticar╔Öt Siyah─▒n─▒z ({userSettings.Coins.Count} coin):</b>\n<code>{cleanList}</code>");

                    await SendMessageAsync(sb.ToString(), chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }
                else if (notFound.Count > 0)
                {
                    var notFoundStr = string.Join(", ", notFound);
                    var msg = $"Ôøö Bu c├╝tl├╝k tap─▒lmad─▒.\nÔÇ£{notFoundStr}ÔÇØ Binance Futures USDT siyah─▒s─▒nda yoxdur.\nD├╝zg├╝n ticker yaz─▒n (m╔Ös: SOL, LINK, AVAX).";
                    await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }
                else if (alreadyInList.Count > 0)
                {
                    var alreadyStr = string.Join(", ", alreadyInList);
                    await SendMessageAsync($"Ôä╣´©Å <b>Bu coinl╔Ör art─▒q siyah─▒n─▒zda m├Âvcuddur:</b> <code>{alreadyStr}</code>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }
                else
                {
                    await SendMessageAsync("ÔÜá´©Å <b>D├╝zg├╝n coin ad─▒ daxil edilm╔Ödi.</b>\n­şôî <b>M╔Ös╔Öl╔Ön:</b> <code>SOL, BTC, ETH, DOGE</code>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }
            }

            if (text == "ÔÅ▒ 15 D╔Öqiq╔Ö (15m) Siqnallar─▒" ||
                text == "ÔÅ▒ 1 Saat (1h) Siqnallar─▒" ||
                text == "ÔÅ▒ 4 Saat (4h) Siqnallar─▒" ||
                text == "­şîş B├╝t├╝n ãÅsas Zamanlar (15m, 1h, 4h)" ||
                text == "­şîş B├╝t├╝n Zamanlar (Ham─▒s─▒) Siqnallar─▒")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "Ôøö Ticar╔Öt ba┼şlamad─▒.\nS╔Öb╔Öb: he├ğ bir coin se├ğilm╔Öyib.\nãÅvv╔Öl ÔÜÖ´©Å Coin Se├ğimi il╔Ö ╔Ön az─▒ 1 coin se├ğin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }

                // CRITICAL FIX: Evaluate B├╝t├╝n / Ham─▒s─▒ FIRST before checking 15m/1h/4h
                string targetTf;
                if (text.Contains("B├╝t├╝n", StringComparison.OrdinalIgnoreCase) || 
                    text.Contains("Butun", StringComparison.OrdinalIgnoreCase) || 
                    text.Contains("Ham─▒s─▒", StringComparison.OrdinalIgnoreCase) || 
                    text.Contains("Hamisi", StringComparison.OrdinalIgnoreCase) || 
                    text.Contains("15m, 1h, 4h", StringComparison.OrdinalIgnoreCase))
                {
                    targetTf = "Ham─▒s─▒";
                }
                else if (text.Contains("15m", StringComparison.OrdinalIgnoreCase) || 
                         text.Contains("15 D╔Öqiq╔Ö", StringComparison.OrdinalIgnoreCase) || 
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
                    targetTf = "Ham─▒s─▒";
                }

                userSettings.Timeframe = targetTf;
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var tfDisplay = (targetTf == "Ham─▒s─▒" || targetTf == "Hamisi") ? "15m, 1h, 4h" : targetTf;
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));

                var sb = new StringBuilder();
                sb.AppendLine("Ô£à <b>Ticar╔Öt ba┼şlad─▒</b>");
                sb.AppendLine($"ÔÅ▒ <b>Rejim:</b> <code>{tfDisplay}</code>");
                sb.AppendLine($"­ş¬Ö <b>─░zl╔Ön╔Ön:</b> {userSettings.Coins.Count} coin");
                sb.AppendLine($"<code>{cleanList}</code>");

                await SendMessageAsync(sb.ToString(), chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }

            // DIRECT TIMEFRAME PREFERENCE SELECTION
            if (text == "­şîş B├╝t├╝n ãÅsas Zamanlar (1h, 4h)" ||
                text == "­şîş B├╝t├╝n ãÅsas Zamanlar (15m, 1h, 4h)" || 
                text == "­şîş B├╝t├╝n Zamanlar (Ham─▒s─▒)" || 
                text.Equals("Hamisi", StringComparison.OrdinalIgnoreCase) || 
                text.Equals("Ham─▒s─▒", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("B├╝t├╝n", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("Butun", StringComparison.OrdinalIgnoreCase))
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "Ôøö Ticar╔Öt ba┼şlamad─▒.\nS╔Öb╔Öb: he├ğ bir coin se├ğilm╔Öyib.\nãÅvv╔Öl ÔÜÖ´©Å Coin Se├ğimi il╔Ö ╔Ön az─▒ 1 coin se├ğin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                userSettings.Timeframe = "Ham─▒s─▒";
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var startMsg = $"Ô£à <b>Ticar╔Öt ba┼şlad─▒</b>\n" +
                               $"ÔÅ▒ <b>Rejim:</b> <code>1h, 4h</code>\n" +
                               $"­ş¬Ö <b>─░zl╔Ön╔Ön:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "ÔÅ▒ 15 D╔Öqiq╔Ö (15m)" || text == "15m")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "Ôøö Ticar╔Öt ba┼şlamad─▒.\nS╔Öb╔Öb: he├ğ bir coin se├ğilm╔Öyib.\nãÅvv╔Öl ÔÜÖ´©Å Coin Se├ğimi il╔Ö ╔Ön az─▒ 1 coin se├ğin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                userSettings.Timeframe = "1h";
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var startMsg = $"Ô£à <b>Ticar╔Öt ba┼şlad─▒</b>\n" +
                               $"ÔÅ▒ <b>Rejim:</b> <code>1h</code> (15m deaktiv edilib)\n" +
                               $"­ş¬Ö <b>─░zl╔Ön╔Ön:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "ÔÅ▒ 1 Saat (1h) Siqnallar─▒" || text == "ÔÅ▒ 1 Saat (1h)" || text == "1h")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "Ôøö Ticar╔Öt ba┼şlamad─▒.\nS╔Öb╔Öb: he├ğ bir coin se├ğilm╔Öyib.\nãÅvv╔Öl ÔÜÖ´©Å Coin Se├ğimi il╔Ö ╔Ön az─▒ 1 coin se├ğin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                userSettings.Timeframe = "1h";
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var startMsg = $"Ô£à <b>Ticar╔Öt ba┼şlad─▒</b>\n" +
                               $"ÔÅ▒ <b>Rejim:</b> <code>1h</code>\n" +
                               $"­ş¬Ö <b>─░zl╔Ön╔Ön:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "ÔÅ▒ 4 Saat (4h)" || text == "4h")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "Ôøö Ticar╔Öt ba┼şlamad─▒.\nS╔Öb╔Öb: he├ğ bir coin se├ğilm╔Öyib.\nãÅvv╔Öl ÔÜÖ´©Å Coin Se├ğimi il╔Ö ╔Ön az─▒ 1 coin se├ğin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                userSettings.Timeframe = "4h";
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var startMsg = $"Ô£à <b>Ticar╔Öt ba┼şlad─▒</b>\n" +
                               $"ÔÅ▒ <b>Rejim:</b> <code>4h</code>\n" +
                               $"­ş¬Ö <b>─░zl╔Ön╔Ön:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "­şôï Standart 40 Coini Se├ğ" || text == "­şôï Standart 16 Coini Se├ğ")
            {
                userSettings.Coins = new List<string>(Default40Coins);
                SaveSettings();
                var cleanList = string.Join(", ", Default40Coins.Select(c => c.Replace("USDT", "")));
                var msg = $"Ô£à <b>Standart 40 institusional coin se├ğildi (40/40).</b>\n\n" +
                          $"­ş¬Ö <b>─░zl╔Ön╔Ön Coinl╔Ör:</b>\n<code>{cleanList}</code>\n\n" +
                          $"­şôî ─░ndi menyudan <b>Ô¡É M╔Önim Coinl╔Örim</b> il╔Ö ticar╔Öt╔Ö ba┼şlaya bil╔Örsiniz.";
                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "ÔŞò ├ûz coini ╔Ölav╔Ö et")
            {
                _userStates[chatId] = "WAITING_ADD_CUSTOM_COIN";
                var prompt = "ÔŞò <b>├ûz Coini ãÅlav╔Ö Et</b>\n\n" +
                             "Binance Futures USDT c├╝tl├╝y├╝ ├╝├ğ├╝n ticker yaz─▒n (m╔Ös╔Öl╔Ön: <code>RENDER</code>, <code>SOL</code>, <code>AVAX</code>):";
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

                string? lastTime = userSettings.LastSignalSentUtc == default 
                    ? null 
                    : CryptoSense.Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                var statusMsg = TelegramMessageFormatter.FormatBotStatus(userSettings, openCount, lastTime);
                await SendMessageAsync(statusMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text.Contains("Geri") || text.Contains("ãÅsas Menyu") || text == "/menu" || text == "/help" || text.Contains("Menyu"))
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
                var openCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default 
                    ? "" 
                    : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);

                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, openCount, lastTime);
                var inlineKb = TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isAdmin);

                var newMsgId = await SendMessageReturnIdAsync(dashText, chatId, inlineKb);
                userSettings.LastTerminalMessageId = newMsgId;
                userSettings.IsTerminalOpen = true;
                SaveSettings();
                return;
            }
            else if (text.Contains("Dayand─▒r") || text.Contains("Dayandir") || text == "/stop")
            {
                userSettings.IsActive = false;
                SaveSettings();
                await SendMessageAsync("­şøæ <b>Canl─▒ Bildiri┼şl╔Ör Dayand─▒r─▒ld─▒! ­şö┤</b>\n\n" +
                                       "Siz╔Ö yeni siqnal v╔Ö n╔Ötic╔Ö bildiri┼şl╔Öri g╔Ölm╔Öy╔Öc╔Ök.\n" +
                                       "Yenid╔Ön ba┼şlatmaq ├╝├ğ├╝n <b>ÔûÂ´©Å Bildiri┼şl╔Öri Ba┼şlat</b> d├╝ym╔Ösin╔Ö klikl╔Öyin.", 
                                       chatId, 
                                       TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Ba┼şlat") || text.Contains("Baslat") || text == "/resume" || text == "/start_signals")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "Ôøö Ticar╔Öt ba┼şlamad─▒.\nS╔Öb╔Öb: he├ğ bir coin se├ğilm╔Öyib.\nãÅvv╔Öl ÔÜÖ´©Å Coin Se├ğimi il╔Ö ╔Ön az─▒ 1 coin se├ğin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                if (string.IsNullOrWhiteSpace(userSettings.Timeframe))
                {
                    await SendMessageAsync(
                        "Ôøö Ticar╔Öt ba┼şlamad─▒.\nS╔Öb╔Öb: timeframe se├ğilm╔Öyib.\nZ╔Öhm╔Öt olmasa ticar╔Öt ├╝├ğ├╝n zaman k╔Ösiyi se├ğin.",
                        chatId,
                        TelegramKeyboards.BuildTimeframeKeyboard());
                    return;
                }

                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var tfDisplay = (userSettings.Timeframe == "Ham─▒s─▒" || userSettings.Timeframe == "Hamisi") ? "1h, 4h" : userSettings.Timeframe;
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));

                var startMsg = $"Ô£à <b>Ticar╔Öt ba┼şlad─▒</b>\n" +
                               $"ÔÅ▒ <b>Rejim:</b> <code>{tfDisplay}</code>\n" +
                               $"­ş¬Ö <b>─░zl╔Ön╔Ön:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("S─▒f─▒rla") || text.Contains("Sifirla") || text == "/clear" || text == "/reset")
            {
                if (!isAdmin)
                {
                    await SendMessageAsync("Ôøö <b>S╔Ölahiyy╔Ötiniz ├ğatm─▒r!</b>\n\nBu funksiya yaln─▒z Sistem Adminin╔Ö m╔Öxsusdur.", chatId);
                    return;
                }

                userSettings.AlertCounter = 0;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                int coinCount = userSettings.Coins.Count;
                var resetMsg = "­şğ╣ <b>Bildiri┼ş Say─şac─▒n─▒z S─▒f─▒rland─▒! Ô£à</b>\n\n" +
                               "ÔÇó ┼Ş╔Öxsi siqnal say─şac─▒n─▒z (#1) s─▒f─▒rland─▒ v╔Ö yeni bildiri┼şl╔Ör ├╝├ğ├╝n haz─▒rland─▒.\n" +
                               "ÔÇó Baza statistikas─▒ v╔Ö ke├ğmi┼ş ticar╔Öt n╔Ötic╔Öl╔Öri qorunub saxlan─▒ld─▒.\n" +
                               $"ÔÇó Se├ğilmi┼ş coin siyah─▒n─▒z (<b>{coinCount} coin</b>) qorunub saxlan─▒ld─▒.\n" +
                               $"ÔÇó Bildiri┼ş Statusu: {(userSettings.IsActive ? "Aktiv ­şşó" : "Dayand─▒r─▒l─▒b ­şö┤")}";

                await SendMessageAsync(resetMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("D╔Örin") || text.Contains("Derin") || text == "­şôê D╔Örin Statistika" || text == "­şôê Coinl╔Ör ├£zr╔Ö D╔Örin Statistika" || text == "/coin_stats")
            {
                _userStates.TryRemove(chatId, out _);
                if (!isAdmin)
                {
                    await SendMessageAsync("Ôøö Bu b├Âlm╔Ö yaln─▒z SuperAdmin ├╝├ğ├╝nd├╝r", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, false));
                    return;
                }

                await SendMessageAsync("ÔÅ│ <b>B├╝t├╝n coinl╔Ör v╔Ö zaman ├ğ╔Ör├ğiv╔Öl╔Öri ├╝zr╔Ö d╔Örin n╔Ötic╔Öl╔Ör hesablan─▒r...</b>", chatId);

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
                var tfLabel = (string.IsNullOrWhiteSpace(userSettings.Timeframe) || userSettings.Timeframe == "T╔Öyin olunmay─▒b" || userSettings.Timeframe == "Ham─▒s─▒" || userSettings.Timeframe == "Hamisi") ? "1h, 4h" : userSettings.Timeframe;
                var msg = TelegramMessageFormatter.FormatPerformanceStats(stats, tfLabel);
                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Coin Se├ğimi") || text.Contains("Coin Secimi") || text == "/setcoins" || text.Contains("Coinl╔Örim") || text.Contains("Coinlerim") || text == "/my" || text == "ÔÜí B├╝t├╝n Siqnallar" || text == "/scan")
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var openCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, openCount, lastTime);
                await SendMessageAsync(dashText, chatId, TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isAdmin));
                return;
            }
            else if (text.Contains("Bitcoin", StringComparison.OrdinalIgnoreCase) || text.Contains("Kompas", StringComparison.OrdinalIgnoreCase) || text.Contains("­şğ¡") || text == "/btc" || text == "/compass")
            {
                var compass = await signalEngine.GetBtcCompassAsync();
                var btcMsg = TelegramMessageFormatter.FormatBtcCompass(compass);
                await SendMessageAsync(btcMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text == "­şô░ X╔Öb╔Örl╔Ör" || text.Contains("X╔Öb╔Ör") || text.Contains("Xeber") || text.Equals("News", StringComparison.OrdinalIgnoreCase) || text == "/news" || text.Contains("­şô░"))
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
                var tf = (userSettings.Timeframe == "Ham─▒s─▒" || userSettings.Timeframe == "Hamisi") ? "1h" : userSettings.Timeframe;
                var sig = await signalEngine.AnalyzeCoinAsync(potentialSym, tf);
                if (sig.SignalType.Contains("LONG") || sig.SignalType.Contains("SHORT"))
                {
                    await SendSignalAlertAsync(sig, chatId);
                }
                else if (sig.SignalType != "MãÅLUMAT AZDIR")
                {
                    var cleanSym = sig.Symbol.Replace("USDT", "");
                    var reasonList = sig.AnalysisReasons.Count > 0 
                        ? string.Join("\nÔÇó ", sig.AnalysisReasons) 
                        : "Bazar t╔Ösdiql╔Önmi┼ş trend istiqam╔Öti g├Âst╔Örmir.";
                    var analysisMsg = $"­şöı <b>{cleanSym} ({tf}) Canl─▒ Texniki Analiz:</b>\n\n" +
                                      $"ÔÜ¬ <b>V╔Öziyy╔Öt:</b> <b>NEYTRAL (G├ûZLãÅMãÅ) ÔÜ¬</b>\n" +
                                      $"­şÆÁ <b>Cari Qiym╔Öt:</b> ${sig.CurrentPrice}\n" +
                                      $"­şÄ» <b>Confluence Bal─▒:</b> {sig.ConfluenceScore}%\n\n" +
                                      $"­şôè <b>─░ndiqator G├Âst╔Öricil╔Öri:</b>\n" +
                                      $"ÔÇó {reasonList}\n\n" +
                                      $"Ôä╣´©Å <i>Hal-haz─▒rda bu coin ├╝zr╔Ö t╔Ösdiql╔Önmi┼ş giri┼ş siqnal─▒ yoxdur. T╔Öl╔Öbl╔Ör╔Ö cavab ver╔Ön (>=75%) giri┼ş yarand─▒qda canl─▒ siqnal g├Ând╔Öril╔Öc╔Ök.</i>";
                    await SendMessageAsync(analysisMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                }
                else
                {
                    var helpMsg = "Ôä╣´©Å <b>N╔Ö etm╔Ök laz─▒md─▒r?</b>\n\n" +
                                  "A┼şa─ş─▒dak─▒ menyudan se├ğim edin v╔Ö ya analiz etm╔Ök ist╔Ödiyiniz coinin ad─▒n─▒ yaz─▒n.\n" +
                                  "­şôî M╔Ös╔Öl╔Ön: <code>SOL</code>, <code>BTC</code>, <code>ETH</code>, <code>DOGE</code>";
                    await SendMessageAsync(helpMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                }
            }
        }
    }
}