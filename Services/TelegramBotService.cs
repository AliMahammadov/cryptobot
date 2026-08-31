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

        // DISPATCH REAL-TIME SIGNALS STRICTLY MATCHING USER'S TIMEFRAME
        public async Task SendSignalAlertAsync(FuturesSignal signal, string? specificChatId = null)
        {
            var isLong = signal.SignalType.Contains("LONG");
            var cleanSymbol = signal.Symbol.Replace("USDT", "");
            var statusIcon = isLong ? "\U0001F7E2" : "\U0001F534";
            var sentimentText = signal.NewsSentimentImpact.Contains("BULLISH") || signal.NewsSentimentImpact.Contains("MUSBET") ? "M\u00FCsb\u0259t \U0001F7E2" : (signal.NewsSentimentImpact.Contains("BEARISH") || signal.NewsSentimentImpact.Contains("MENFI") ? "M\u0259nfi \U0001F534" : "Neytral \u26AA");

            var sb = new StringBuilder();
            sb.AppendLine($"#{signal.SignalNumber} {statusIcon} <b>YEN\u0130 AVTOMAT\u0130K S\u0130QNAL:</b>");
            sb.AppendLine();
            sb.AppendLine($"\U0001FA99 <b>C\u00FCtl\u00FCk:</b> {cleanSymbol} Futures ({signal.Timeframe})");
            sb.AppendLine($"\u23F1 <b>Zaman \u00C7\u0259r\u00E7iv\u0259si:</b> {signal.Timeframe}");
            sb.AppendLine($"\U0001F552 <b>Verilm\u0259 Tarixi:</b> {signal.TimestampFormatted}");
            sb.AppendLine($"\U0001F3AF <b>G\u00FCv\u0259nlik S\u0259viyy\u0259si:</b> {signal.Confidence}%");
            sb.AppendLine($"\U0001F4B2 <b>Cari Qiym\u0259t:</b> ${signal.CurrentPrice}");
            sb.AppendLine($"\U0001F4F0 <b>X\u0259b\u0259r Sentimenti:</b> {sentimentText}");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"\U0001F4CD <b>Giri\u015F Zonas\u0131:</b> ${signal.EntryLow} - ${signal.EntryHigh}");
            sb.AppendLine($"\U0001F3AF <b>H\u0259d\u0259f 1 (TP1):</b> ${signal.TakeProfit1}");
            sb.AppendLine($"\U0001F3AF <b>H\u0259d\u0259f 2 (TP2):</b> ${signal.TakeProfit2}");
            sb.AppendLine($"\U0001F31F <b>H\u0259d\u0259f 3 (TP3):</b> ${signal.TakeProfit3}");
            sb.AppendLine($"\u26D4 <b>Stop Loss (SL):</b> ${signal.StopLoss}");
            sb.AppendLine("-----------------------------------");
            sb.AppendLine("\U0001F4CA <b>20-??ndikator Texniki ??sasland??rma:</b>");
            foreach (var r in signal.AnalysisReasons)
            {
                sb.AppendLine($"- {r}");
            }

            var messageText = sb.ToString();

            if (!string.IsNullOrEmpty(specificChatId))
            {
                await SendMessageAsync(messageText, specificChatId);
                return;
            }

            foreach (var chatId in AuthenticatedChats)
            {
                if (chatId == SuperAdminChatId) continue; // NEVER SPAM SUPER ADMIN

                var settings = GetSettings(chatId);
                if (!settings.IsActive) continue;

                // STRICT TIMEFRAME CHECK: ONLY SEND IF MATCHES USER'S CHOSEN TIMEFRAME!
                if (settings.Timeframe != "Ham\u0131s\u0131" && settings.Timeframe != "Hamisi" && settings.Timeframe != signal.Timeframe)
                {
                    continue; // Skip if user chose 3m and signal is 15m or 5m!
                }

                // Coin filter check
                if (settings.Coins.Count > 0 && !settings.Coins.Contains(signal.Symbol)) continue;

                await SendMessageAsync(messageText, chatId);
            }
        }

        // OUTCOME REPORT DISPATCHER
        public async Task SendOutcomeAlertAsync(FuturesSignal signal, string outcomeType, decimal hitPrice, decimal profitPct)
        {
            var isWin = profitPct >= 0;
            var icon = isWin ? "\U0001F3AF" : "\u26D4";
            var statusText = isWin ? $"{outcomeType} (U\u011EURLU) \u2705" : $"{outcomeType} (U\u011EURSUZ) \u274C";
            var cleanSymbol = signal.Symbol.Replace("USDT", "");

            var sb = new StringBuilder();
            sb.AppendLine($"{icon} <b>#{signal.SignalNumber} N\u018FT\u0130C\u018F HESABATI:</b>");
            sb.AppendLine($"<b>{statusText}</b>");
            sb.AppendLine();
            sb.AppendLine($"\U0001FA99 <b>C\u00FCtl\u00FCk:</b> {cleanSymbol} Futures ({signal.Timeframe})");
            sb.AppendLine($"\U0001F4CD <b>Giri\u015F S\u0259viyy\u0259si:</b> ${signal.EntryLow} - ${signal.EntryHigh}");
            sb.AppendLine($"\U0001F4B2 <b>Ba\u011Flan\u0131\u015F Qiym\u0259ti:</b> ${hitPrice}");
            sb.AppendLine($"\U0001F4C8 <b>N\u0259tic\u0259 (G\u0259lir/Z\u0259r\u0259r):</b> <b>{(profitPct >= 0 ? "+" : "")}{profitPct:F2}%</b>");
            sb.AppendLine($"\U0001F552 <b>Siqnal Vaxt\u0131:</b> {signal.TimestampFormatted}");
            sb.AppendLine($"\U0001F552 <b>Ba\u011Flanma Vaxt\u0131:</b> {DateTime.Now:dd.MM.yyyy | HH:mm:ss}");

            var messageText = sb.ToString();

            foreach (var chatId in AuthenticatedChats)
            {
                if (chatId == SuperAdminChatId) continue; // NEVER SPAM SUPER ADMIN
                var settings = GetSettings(chatId);
                
                // ONLY SEND OUTCOME IF MATCHES USER'S TIMEFRAME
                if (settings.Timeframe != "Ham\u0131s\u0131" && settings.Timeframe != "Hamisi" && settings.Timeframe != signal.Timeframe)
                {
                    continue;
                }

                await SendMessageAsync(messageText, chatId);
            }
        }

        public async Task NotifySuperAdminUserLoginAsync(string username, string platform)
        {
            if (string.IsNullOrEmpty(SuperAdminChatId)) return;

            var msg = $"\U0001F514 <b>YEN\u0130 G\u0130R\u0130\u015E B\u0130LD\u0130R\u0130\u015E\u0130:</b>\n\n" +
                      $"\U0001F464 <b>\u0130stifad\u0259\u00E7i:</b> <code>{username}</code>\n" +
                      $"\U0001F552 <b>Tarix:</b> <code>{DateTime.Now:dd.MM.yyyy | HH:mm:ss}</code>\n" +
                      $"\U0001F310 <b>Platforma:</b> {platform}";
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
                                var text = (msg.TryGetProperty("text", out var txtEl) ? txtEl.GetString() : "")?.Trim() ?? "";

                                var fromUser = "";
                                if (msg.TryGetProperty("from", out var fromEl) && fromEl.TryGetProperty("username", out var uNameEl))
                                {
                                    fromUser = (uNameEl.GetString() ?? "").TrimStart('@');
                                }

                                await HandleIncomingMessageAsync(chatId, fromUser, text);
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

        private async Task HandleIncomingMessageAsync(string chatId, string username, string text)
        {
            using var scope = _serviceProvider.CreateScope();
            var userManager = (UserManagerService)scope.ServiceProvider.GetService(typeof(UserManagerService))!;
            var signalEngine = (SignalEngine)scope.ServiceProvider.GetService(typeof(SignalEngine))!;
            var newsService = (NewsService)scope.ServiceProvider.GetService(typeof(NewsService))!;

            bool isSuperAdmin = username.Equals(SuperAdminUsername, StringComparison.OrdinalIgnoreCase);

            if (isSuperAdmin)
            {
                SuperAdminChatId = chatId;
                AuthenticatedChats.Remove(chatId); // NEVER SPAM SUPER ADMIN
                await HandleSuperAdminFlowAsync(chatId, text, userManager);
                return;
            }

            await HandleRegularUserFlowAsync(chatId, username, text, userManager, signalEngine, newsService);
        }

        private async Task HandleSuperAdminFlowAsync(string chatId, string text, UserManagerService userManager)
        {
            var adminKeyboard = new
            {
                keyboard = new[]
                {
                    new[] { new { text = "\u2795 \u0130stifad\u0259\u00E7i Yarat" } },
                    new[] { new { text = "\U0001F465 \u0130stifad\u0259\u00E7il\u0259rin Siyah\u0131s\u0131" }, new { text = "\U0001F5D1 \u0130stifad\u0259\u00E7i Sil" } }
                },
                resize_keyboard = true,
                one_time_keyboard = false
            };

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
                        var msg = $"\u2705 <b>\u0130stifad\u0259\u00E7i u\u011Furla yarad\u0131ld\u0131!</b>\n\n" +
                                  $"\U0001F464 <b>Ad:</b> <code>{newUsername}</code>\n" +
                                  $"\U0001F511 <b>Parol:</b> <code>{newPassword}</code>\n\n" +
                                  $"<i>\u0130stifad\u0259\u00E7iy\u0259 g\u00F6nd\u0259rin ki, bota daxil olub <code>{newUsername} {newPassword}</code> yazs\u0131n.</i>";
                        await SendMessageAsync(msg, chatId, adminKeyboard);
                    }
                    else
                    {
                        await SendMessageAsync($"\u26A0\uFE0F <b>X\u0259ta:</b> <code>{newUsername}</code> adl\u0131 istifad\u0259\u00E7i art\u0131q m\u00F6vcuddur!", chatId, adminKeyboard);
                    }
                    return;
                }
                else
                {
                    await SendMessageAsync("\u26A0\uFE0F <b>Format\u0131 d\u00FCzg\u00FCn daxil edin!</b>\nM\u0259s\u0259l\u0259n: <code>Murad 123456</code>", chatId, adminKeyboard);
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
                    await SendMessageAsync($"\u2705 <b>\u0130stifad\u0259\u00E7i '{userToDelete}' sistemd\u0259n silindi, b\u00FCt\u00FCn prosesl\u0259ri dayand\u0131r\u0131ld\u0131 v\u0259 x\u0259b\u0259rdarl\u0131q g\u00F6nd\u0259rildi!</b>", chatId, adminKeyboard);
                }
                else
                {
                    await SendMessageAsync($"\u26A0\uFE0F <b>'{userToDelete}' tap\u0131lmad\u0131 v\u0259 ya silin\u0259 bilm\u0259z.</b>", chatId, adminKeyboard);
                }
                return;
            }

            if (text.Contains("Yarat") || text == "/adduser")
            {
                _userStates[chatId] = "ADMIN_WAITING_CREATE_USER";
                var prompt = "\u2795 <b>Yeni \u0130stifad\u0259\u00E7i Yaratmaq</b>\n\n" +
                             "Yaratmaq ist\u0259diyiniz <b>\u0130stifad\u0259\u00E7i Ad\u0131n\u0131</b> v\u0259 <b>Parolu</b> aralar\u0131nda bo\u015Fluq qoyaraq yaz\u0131n:\n\n" +
                             "\U0001F4CC <b>M\u0259s\u0259l\u0259n:</b>\n" +
                             "<code>Murad 123456</code>";
                await SendMessageAsync(prompt, chatId, adminKeyboard);
            }
            else if (text.Contains("Siyah\u0131s\u0131") || text.Contains("Siyahisi") || text.Contains("istifad\u0259\u00E7il\u0259r") || text == "/users")
            {
                var users = userManager.GetAllUsers();
                var sb = new StringBuilder();
                sb.AppendLine("\U0001F465 <b>Sistemd\u0259ki Qeydiyyatl\u0131 \u0130stifad\u0259\u00E7il\u0259r:</b>");
                sb.AppendLine($"\u00DCmumi say: <b>{users.Count} n\u0259f\u0259r</b>");
                sb.AppendLine("-----------------------------------");
                int index = 1;
                foreach (var u in users)
                {
                    sb.AppendLine($"{index}. <b>{u.Username}</b> | Parol: <code>{u.Password}</code> ({u.Role})");
                    index++;
                }
                sb.AppendLine("-----------------------------------");
                await SendMessageAsync(sb.ToString(), chatId, adminKeyboard);
            }
            else if (text.Contains("Sil") || text == "/deleteuser")
            {
                _userStates[chatId] = "ADMIN_WAITING_DELETE_USER";
                var prompt = "\U0001F5D1 <b>\u0130stifad\u0259\u00E7i Silm\u0259k</b>\n\n" +
                             "Sistemd\u0259n silm\u0259k ist\u0259diyiniz istifad\u0259\u00E7inin <b>Ad\u0131n\u0131</b> yaz\u0131n:\n\n" +
                             "\U0001F4CC <b>M\u0259s\u0259l\u0259n:</b>\n" +
                             "<code>Murad</code>";
                await SendMessageAsync(prompt, chatId, adminKeyboard);
            }
            else
            {
                var welcomeAdmin = "\U0001F451 <b>Salam, Ali Muhammadov (@alimahammadov)!</b>\n\n" +
                                   "Bu sizin <b>Super Admin \u0130dar\u0259etm\u0259 Panelinizdir.</b>\n" +
                                   "A\u015Fa\u011F\u0131dak\u0131 d\u00FCym\u0259l\u0259rd\u0259n istifad\u0259 ed\u0259r\u0259k istifad\u0259\u00E7il\u0259ri idar\u0259 ed\u0259 bil\u0259rsiniz:";
                await SendMessageAsync(welcomeAdmin, chatId, adminKeyboard);
            }
        }

        private async Task HandleRegularUserFlowAsync(string chatId, string username, string text, UserManagerService userManager, SignalEngine signalEngine, NewsService newsService)
        {
            // 1. STRICT CHECK: IS THIS USER AUTHENTICATED?
            if (!AuthenticatedChats.Contains(chatId))
            {
                var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    var inputUser = parts[0];
                    var inputPass = string.Join(" ", parts.Skip(1));

                    var (isValid, user) = userManager.ValidateLogin(inputUser, inputPass);
                    if (isValid && user != null)
                    {
                        AuthenticatedChats.Add(chatId);
                        var settings = GetSettings(chatId);
                        settings.Username = user.Username;
                        settings.IsActive = true;
                        settings.Timeframe = "3m"; // STRICT 3M DEFAULT

                        var onboardingMsg = $"\u2705 <b>Giri\u015F T\u0259sdiql\u0259ndi! Xo\u015F G\u0259ldiniz, {user.Username}!</b>\n\n" +
                                            $"\U0001F680 <b>KriptoBot 20-??ndikatorlu Ticar\u0259t Sistemi AKT\u0130VD\u0130R \U0001F7E2</b>\n\n" +
                                            $"Aktiv Zaman \u00C7\u0259r\u00E7iv\u0259si: <b>{settings.Timeframe}</b>\n\n" +
                                            $"Yaln\u0131z se\u00E7diyiniz <b>{settings.Timeframe}</b> zaman\u0131 \u00FCzr\u0259 24/7 siqnallar v\u0259 n\u0259tic\u0259l\u0259r g\u00F6nd\u0259ril\u0259c\u0259k.";
                        
                        await SendMessageAsync(onboardingMsg, chatId, BuildUserKeyboard(settings));
                        await NotifySuperAdminUserLoginAsync(user.Username, $"Telegram Bot (Chat ID: {chatId}, @{username})");
                        return;
                    }
                }

                var isWrong = parts.Length >= 2;
                var welcomeAndAuth = (isWrong ? "\u274C <b>\u0130stifad\u0259\u00E7i ad\u0131 v\u0259 ya parol yaln\u0131\u015Fd\u0131r!</b>\n\n" : "\U0001F44B <b>Salam! KriptoBot Xidm\u0259tin\u0259 xo\u015F g\u0259ldiniz.</b>\n\n") +
                                     "\u26A0\uFE0F <b>Sistemd\u0259n istifad\u0259 etm\u0259k \u00FC\u00E7\u00FCn QEYD\u0130YYATDAN KE\u00C7M\u018FL\u0130 v\u0259 daxil olmal\u0131s\u0131n\u0131z!</b>\n\n" +
                                     "Sistem\u0259 daxil olmaq \u00FC\u00E7\u00FCn <b>\u0130stifad\u0259\u00E7i Ad\u0131n\u0131z\u0131</b> v\u0259 <b>Parolunuzu</b> bir s\u0259tird\u0259, aralar\u0131nda bo\u015Fluq qoyaraq yaz\u0131n:\n\n" +
                                     "\U0001F4CC <b>D\u00FCzg\u00FCn Format:</b>\n" +
                                     "<code>[\u0130stifad\u0259\u00E7iAd\u0131] [Parol]</code>\n\n" +
                                     "\U0001F4A1 <b>N\u00FCmun\u0259:</b>\n" +
                                     "<code>Murad 123456</code>\n\n" +
                                     "-----------------------------------\n" +
                                     "Hesab\u0131n\u0131z yoxdur? Qeydiyyat v\u0259 giri\u015F icaz\u0259si \u00FC\u00E7\u00FCn <b>Super Admin</b> il\u0259 \u0259laq\u0259 saxlay\u0131n:\n" +
                                     "\U0001F449 <a href=\"https://t.me/alimahammadov\">@alimahammadov</a> (Ali Muhammadov)";
                
                await SendMessageAsync(welcomeAndAuth, chatId, new { remove_keyboard = true });
                return;
            }

            // 2. CHECK IF USER WAS DELETED IN THE MEANTIME
            var userSettings = GetSettings(chatId);
            var isStillRegistered = userManager.GetAllUsers().Any(u => u.Username.Equals(userSettings.Username, StringComparison.OrdinalIgnoreCase));
            if (!isStillRegistered)
            {
                AuthenticatedChats.Remove(chatId);
                UserPreferences.TryRemove(chatId, out _);

                var kickMsg = "\u26D4 <b>HESABINIZ S\u0130L\u0130ND\u0130 V\u018F S\u0130STEMD\u018FN \u00C7IXARILDINIZ!</b>\n\n" +
                              "H\u00F6rm\u0259tli istifad\u0259\u00E7i, hesab\u0131n\u0131z sistemd\u0259n silinmi\u015Fdir v\u0259 <b>b\u00FCt\u00FCn prosesl\u0259riniz dayand\u0131r\u0131lm\u0131\u015Fd\u0131r.</b>\n\n" +
                              "Yenid\u0259n giri\u015F v\u0259 qeydiyyat \u00FC\u00E7\u00FCn <b>Super Admin</b> il\u0259 \u0259laq\u0259 saxlay\u0131n:\n" +
                              "\U0001F449 <a href=\"https://t.me/alimahammadov\">@alimahammadov</a> (Ali Muhammadov)";

                await SendMessageAsync(kickMsg, chatId, new { remove_keyboard = true });
                return;
            }

            // 3. AUTHENTICATED USER INTERACTIONS

            // STATE: DELETING A SPECIFIC COIN
            if (_userStates.TryGetValue(chatId, out var stateVal) && stateVal == "USER_WAITING_DELETE_COIN")
            {
                _userStates.TryRemove(chatId, out _);
                var coinToDel = text.Trim().ToUpper();
                if (!coinToDel.EndsWith("USDT")) coinToDel += "USDT";

                if (userSettings.Coins.Contains(coinToDel))
                {
                    userSettings.Coins.Remove(coinToDel);
                    var cleanDel = coinToDel.Replace("USDT", "");
                    var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                    var succMsg = $"\u2705 <b>'{cleanDel}' coini siyah\u0131n\u0131zdan silindi!</b>\n\n" +
                                  $"Cari siyah\u0131n\u0131z ({userSettings.Coins.Count}/10 \u0259d\u0259d):\n" +
                                  $"<code>{(string.IsNullOrEmpty(cleanList) ? "Siyah\u0131 bo\u015Fdur" : cleanList)}</code>";
                    await SendMessageAsync(succMsg, chatId, BuildUserKeyboard(userSettings));
                }
                else
                {
                    var cleanDel = coinToDel.Replace("USDT", "");
                    await SendMessageAsync($"\u26A0\uFE0F <b>'{cleanDel}' coini siyah\u0131n\u0131zda tap\u0131lmad\u0131!</b>", chatId, BuildUserKeyboard(userSettings));
                }
                return;
            }

            // STATE: ADDING COINS (MAX 10)
            if (_userStates.TryGetValue(chatId, out var coinState) && coinState == "WAITING_COIN_INPUT")
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

                    var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                    var conf = $"\u2705 <b>Se\u00E7ilmi\u015F coinl\u0259riniz yenil\u0259ndi ({userSettings.Coins.Count}/10 \u0259d\u0259d):</b>\n\n" +
                               $"<code>{cleanList}</code>";
                    await SendMessageAsync(conf, chatId, BuildUserKeyboard(userSettings));
                    return;
                }
            }

            // ==========================================
            // SUBMENU ACTION: USER CLICKED A SPECIFIC TIMEFRAME FOR ALL SIGNALS SCAN
            // ==========================================
            if (text == "\u23F1 3 D\u0259qiq\u0259 (3m) Siqnallar\u0131" ||
                text == "\u23F1 5 D\u0259qiq\u0259 (5m) Siqnallar\u0131" ||
                text == "\u23F1 15 D\u0259qiq\u0259 (15m) Siqnallar\u0131" ||
                text == "\u23F1 1 Saat (1h) Siqnallar\u0131" ||
                text == "\u23F1 4 Saat (4h) Siqnallar\u0131" ||
                text == "\U0001F31F B\u00FCt\u00FCn Zamanlar (Ham\u0131s\u0131) Siqnallar\u0131")
            {
                var sampleCoins = new[] { "SOLUSDT", "BTCUSDT", "ETHUSDT", "DOGEUSDT", "XRPUSDT", "BNBUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT" };

                if (text.Contains("B\u00FCt\u00FCn Zamanlar") || text.Contains("Ham\u0131s\u0131"))
                {
                    userSettings.Timeframe = "Ham\u0131s\u0131"; // SET PROFILE TIMEFRAME
                    await SendMessageAsync("\u23F3 <b>B\u00FCt\u00FCn zaman \u00E7\u0259r\u00E7iv\u0259l\u0259ri (3m, 5m, 15m, 1h, 4h) \u00FCzr\u0259 bazar skan edilir...</b>", chatId, BuildUserKeyboard(userSettings));
                    var tfs = new[] { "3m", "5m", "15m", "1h", "4h" };
                    int totalFound = 0;
                    foreach (var tf in tfs)
                    {
                        foreach (var sym in sampleCoins.Take(3))
                        {
                            var sig = await signalEngine.AnalyzeCoinAsync(sym, tf);
                            if (sig.Confidence >= 88 && (sig.SignalType.Contains("LONG") || sig.SignalType.Contains("SHORT")))
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

                    userSettings.Timeframe = targetTf; // STRICTLY SET USER'S PROFILE TIMEFRAME!

                    await SendMessageAsync($"\u23F3 <b>Yaln\u0131z ({targetTf}) \u00FCzr\u0259 90%+ g\u00FCcl\u00FC siqnallar axtar\u0131l\u0131r... (Profiliniz '{targetTf}' olaraq t\u0259yin edildi)</b>", chatId, BuildUserKeyboard(userSettings));

                    int found = 0;
                    foreach (var sym in sampleCoins)
                    {
                        var sig = await signalEngine.AnalyzeCoinAsync(sym, targetTf);
                        if (sig.Confidence >= 88 && (sig.SignalType.Contains("LONG") || sig.SignalType.Contains("SHORT")))
                        {
                            await SendSignalAlertAsync(sig, chatId);
                            found++;
                            await Task.Delay(250);
                        }
                    }
                    if (found == 0)
                    {
                        await SendMessageAsync($"\u2139\uFE0F Hal-haz\u0131rda {targetTf} zaman\u0131nda 90%+ t\u0259l\u0259bl\u0259r\u0259 cavab ver\u0259n aktiv siqnal yoxdur.", chatId, BuildUserKeyboard(userSettings));
                    }
                    return;
                }
            }

            // USER TIMEFRAME PREFERENCE SELECTION DIRECT CHOICES
            if (text == "\u23F1 3 D\u0259qiq\u0259 (3m)" || text == "3m")
            {
                userSettings.Timeframe = "3m";
                await SendMessageAsync($"\u2705 <b>Zaman \u00C7\u0259r\u00E7iv\u0259si T\u0259yin Edildi:</b> <code>3m</code>\n<i>Art\u0131q yaln\u0131z 3m siqnallar\u0131 alacaqs\u0131n\u0131z.</i>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text == "\u23F1 5 D\u0259qiq\u0259 (5m)" || text == "5m")
            {
                userSettings.Timeframe = "5m";
                await SendMessageAsync($"\u2705 <b>Zaman \u00C7\u0259r\u00E7iv\u0259si T\u0259yin Edildi:</b> <code>5m</code>\n<i>Art\u0131q yaln\u0131z 5m siqnallar\u0131 alacaqs\u0131n\u0131z.</i>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text == "\u23F1 15 D\u0259qiq\u0259 (15m)" || text == "15m")
            {
                userSettings.Timeframe = "15m";
                await SendMessageAsync($"\u2705 <b>Zaman \u00C7\u0259r\u00E7iv\u0259si T\u0259yin Edildi:</b> <code>15m</code>\n<i>Art\u0131q yaln\u0131z 15m siqnallar\u0131 alacaqs\u0131n\u0131z.</i>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text == "\u23F1 1 Saat (1h)" || text == "1h")
            {
                userSettings.Timeframe = "1h";
                await SendMessageAsync($"\u2705 <b>Zaman \u00C7\u0259r\u00E7iv\u0259si T\u0259yin Edildi:</b> <code>1h</code>\n<i>Art\u0131q yaln\u0131z 1h siqnallar\u0131 alacaqs\u0131n\u0131z.</i>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text == "\u23F1 4 Saat (4h)" || text == "4h")
            {
                userSettings.Timeframe = "4h";
                await SendMessageAsync($"\u2705 <b>Zaman \u00C7\u0259r\u00E7iv\u0259si T\u0259yin Edildi:</b> <code>4h</code>\n<i>Art\u0131q yaln\u0131z 4h siqnallar\u0131 alacaqs\u0131n\u0131z.</i>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text == "\U0001F31F B\u00FCt\u00FCn Zamanlar (Ham\u0131s\u0131)" || text == "Hamisi" || text == "Ham\u0131s\u0131")
            {
                userSettings.Timeframe = "Ham\u0131s\u0131";
                await SendMessageAsync($"\u2705 <b>Zaman \u00C7\u0259r\u00E7iv\u0259si T\u0259yin Edildi:</b> <code>Ham\u0131s\u0131</code>", chatId, BuildUserKeyboard(userSettings));
                return;
            }
            else if (text.Contains("Geri") || text.Contains("\u018Fsas Menyu"))
            {
                await SendMessageAsync("\U0001F4CA <b>\u018Fsas Menyu:</b>", chatId, BuildUserKeyboard(userSettings));
                return;
            }

            if (text == "/start" || text == "/help" || text.Contains("Menyu"))
            {
                var welcome = "\U0001F4CA <b>KriptoBot Xidm\u0259ti - Canl\u0131 Bazar Paneli</b>\n\n" +
                              "Bildiri\u015F Statusu: " + (userSettings.IsActive ? "<b>AKT\u0130V \U0001F7E2</b>" : "<b>DAYANDIRILIB \U0001F534</b>") + "\n" +
                              "Aktiv Zaman \u00C7\u0259r\u00E7iv\u0259si: <b>" + userSettings.Timeframe + "</b>\n" +
                              "Se\u00E7ilmi\u015F Coinl\u0259r: <b>" + userSettings.Coins.Count + "/10 \u0259d\u0259d</b>\n\n" +
                              "\u018Fm\u0259liyyatlar \u00FC\u00E7\u00FCn a\u015Fa\u011F\u0131dak\u0131 menyudan istifad\u0259 edin:";
                await SendMessageAsync(welcome, chatId, BuildUserKeyboard(userSettings));
            }
            else if (text.Contains("Dayand\u0131r") || text.Contains("Dayandir") || text == "/stop")
            {
                userSettings.IsActive = false;
                await SendMessageAsync("\U0001F6D1 <b>Bildiri\u015Fl\u0259r dayand\u0131r\u0131ld\u0131.</b>\n\nYenid\u0259n ba\u015Flamaq \u00FC\u00E7\u00FCn 'Bildiri\u015Fl\u0259ri Ba\u015Flat' d\u00FCym\u0259sin\u0259 vurun.", chatId, BuildUserKeyboard(userSettings));
            }
            else if (text.Contains("Ba\u015Flat") || text.Contains("Baslat") || text == "/resume")
            {
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                await SendMessageAsync("\u25B6\uFE0F <b>Bildiri\u015Fl\u0259r aktivl\u0259\u015Fdirildi!</b>\n\nSe\u00E7diyiniz zaman \u00E7\u0259r\u00E7iv\u0259si (" + userSettings.Timeframe + ") \u00FCzr\u0259 arxa fonda 24/7 siqnallar g\u00F6nd\u0259ril\u0259c\u0259k.", chatId, BuildUserKeyboard(userSettings));
            }
            // MAIN BUTTON: USER CLICKS 'B??T??N S??QNALLAR' -> SHOW TIMEFRAME SELECTION SUBMENU
            else if (text == "\u26A1 B\u00FCt\u00FCn Siqnallar" || text == "B\u00FCt\u00FCn Siqnallar" || text == "/scan")
            {
                var tfAskMsg = "\u26A1 <b>B\u00FCt\u00FCn Bazar \u00DCzr\u0259 Siqnal Axtar\u0131\u015F\u0131</b>\n\n" +
                               "Hans\u0131 zaman aral\u0131\u011F\u0131 \u00FCzr\u0259 90%+ g\u00FCcl\u00FC siqnallar axtarmaq ist\u0259yirsiniz?\n\n" +
                               "A\u015Fa\u011F\u0131dak\u0131 se\u00E7iml\u0259rd\u0259n birin\u0259 vurun:";
                await SendMessageAsync(tfAskMsg, chatId, BuildAllSignalsTimeframeKeyboard());
            }
            // MY COINS
            else if (text.Contains("Coinl\u0259rim") || text.Contains("Coinlerim") || text == "/my")
            {
                if (userSettings.Coins.Count == 0)
                {
                    var emptyMsg = "\u2B50 <b>Sizin Se\u00E7ilmi\u015F Coinl\u0259riniz</b>\n\n" +
                                   "Hal-haz\u0131rda siyah\u0131n\u0131z bo\u015Fdur.\n" +
                                   "Coin \u0259lav\u0259 etm\u0259k \u00FC\u00E7\u00FCn <b>\u2699\uFE0F Coin Se\u00E7imi</b> d\u00FCym\u0259sin\u0259 vurun (Maks: 10 \u0259d\u0259d).";
                    await SendMessageAsync(emptyMsg, chatId, BuildUserKeyboard(userSettings));
                    return;
                }

                var tf = (userSettings.Timeframe == "Ham\u0131s\u0131" || userSettings.Timeframe == "Hamisi") ? "3m" : userSettings.Timeframe;
                await SendMessageAsync($"\u23F3 Se\u00E7diyiniz {userSettings.Coins.Count} coin \u00FCzr\u0259 ({tf}) 20-indikator analizi apar\u0131l\u0131r...", chatId);

                var sb = new StringBuilder();
                sb.AppendLine("\u2B50 <b>M\u0259nim Coinl\u0259rim - 20-??ndikatorlu Canl\u0131 Analiz</b>");
                sb.AppendLine($"C\u0259mi: <b>{userSettings.Coins.Count}/10 \u0259d\u0259d</b> | Aktiv Zaman: <b>{userSettings.Timeframe}</b>");
                sb.AppendLine("-----------------------------------");

                foreach (var sym in userSettings.Coins)
                {
                    var sig = await signalEngine.AnalyzeCoinAsync(sym, tf);
                    var cleanSym = sym.Replace("USDT", "");
                    var trendIcon = sig.SignalType.Contains("LONG") ? "\U0001F7E2 Y\u00DCKS\u018FL\u0130\u015E" : (sig.SignalType.Contains("SHORT") ? "\U0001F534 EN\u0130\u015E" : "\u26AA NEYTRAL");
                    sb.AppendLine($"\u2022 <b>{cleanSym}</b> (${sig.CurrentPrice}) \u2014 {trendIcon} (G\u00FCc: {sig.Confidence}%)");

                    if (sig.Confidence >= 88 && (sig.SignalType.Contains("LONG") || sig.SignalType.Contains("SHORT")))
                    {
                        await SendSignalAlertAsync(sig, chatId);
                        await Task.Delay(200);
                    }
                }
                sb.AppendLine("-----------------------------------");
                sb.AppendLine("Yeni coin \u0259lav\u0259 etm\u0259k \u00FC\u00E7\u00FCn: <b>\u2699\uFE0F Coin Se\u00E7imi</b>");
                sb.AppendLine("Coini siyah\u0131dan silm\u0259k \u00FC\u00E7\u00FCn: <b>\U0001F5D1 Coin Sil</b>");

                await SendMessageAsync(sb.ToString(), chatId, BuildUserKeyboard(userSettings));
            }
            // TIME PANEL
            else if (text.Contains("Zaman \u00C7\u0259r\u00E7iv\u0259si") || text.Contains("Zaman") || text == "/tf")
            {
                var tfPanelMsg = "\u23F1 <b>Zaman \u00C7\u0259r\u00E7iv\u0259si Paneli</b>\n\n" +
                                 "Hal-haz\u0131rda t\u0259yin edilmi\u015F: <b>" + userSettings.Timeframe + "</b>\n\n" +
                                 "Se\u00E7m\u0259k ist\u0259diyiniz yeni zaman\u0131 a\u015Fa\u011F\u0131dak\u0131 paneld\u0259n se\u00E7in:";
                await SendMessageAsync(tfPanelMsg, chatId, BuildTimeframeKeyboard());
            }
            // DELETE COIN
            else if (text.Contains("Coin Sil") || text == "/delcoin")
            {
                _userStates[chatId] = "USER_WAITING_DELETE_COIN";
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var prompt = "\U0001F5D1 <b>Coin Silm\u0259k</b>\n\n" +
                             $"Cari siyah\u0131n\u0131z ({userSettings.Coins.Count}/10 \u0259d\u0259d):\n" +
                             $"<code>{(string.IsNullOrEmpty(cleanList) ? "Siyah\u0131 bo\u015Fdur" : cleanList)}</code>\n\n" +
                             "Siyah\u0131dan silm\u0259k ist\u0259diyiniz coinin <b>Ad\u0131n\u0131</b> yaz\u0131n:\n" +
                             "\U0001F4CC <b>M\u0259s\u0259l\u0259n:</b> <code>SOL</code> v\u0259 ya <code>DOGE</code>";
                await SendMessageAsync(prompt, chatId, BuildUserKeyboard(userSettings));
            }
            // ADD COINS
            else if (text.Contains("Coin Se\u00E7imi") || text.Contains("Coin Secimi") || text == "/setcoins")
            {
                _userStates[chatId] = "WAITING_COIN_INPUT";
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var prompt = "\u2699\uFE0F <b>Coin Se\u00E7imi (Maksimum 10 \u0259d\u0259d haqq\u0131n\u0131z var):</b>\n\n" +
                             $"Hal-haz\u0131rda se\u00E7ilmi\u015F: <b>{userSettings.Coins.Count}/10 \u0259d\u0259d</b>\n" +
                             $"Cari siyah\u0131: <code>{(string.IsNullOrEmpty(cleanList) ? "Bo\u015Fdur" : cleanList)}</code>\n\n" +
                             "\u018Flav\u0259 etm\u0259k ist\u0259diyiniz coinl\u0259rin ad\u0131n\u0131 a\u015Fa\u011F\u0131ya <b>verg\u00FCll\u0259</b> yaz\u0131b g\u00F6nd\u0259rin.\n\n" +
                             "\U0001F4CC <b>M\u0259s\u0259l\u0259n:</b>\n" +
                             "<code>BTC, ETH, SOL, SUI, DOGE, PEPE, AVAX</code>";
                await SendMessageAsync(prompt, chatId, BuildUserKeyboard(userSettings));
            }
            // BITCOIN
            else if (text.Contains("Bitcoin") || text == "/btc")
            {
                var compass = await signalEngine.GetBtcCompassAsync();
                var btcMsg = $"\U0001F9ED <b>Bitcoin Bazar Kompas\u0131</b>\n\n" +
                             $"\U0001F552 Tarix: <b>{compass.TimestampFormatted}</b>\n" +
                             $"\U0001F4B2 Cari Qiym\u0259t: <b>${compass.Price}</b>\n" +
                             $"\U0001F4C8 Trend \u0130stiqam\u0259ti: <b>{compass.Trend}</b>\n" +
                             $"\U0001F3AF T\u0259hlil G\u00FCc\u00FC: <b>{compass.BullishScore}%</b>\n\n" +
                             $"<i>{compass.Summary}</i>";
                await SendMessageAsync(btcMsg, chatId, BuildUserKeyboard(userSettings));
            }
            // NEWS
            else if (text.Contains("X\u0259b\u0259rl\u0259ri") || text.Contains("Xeberleri") || text == "/news")
            {
                var newsSummary = await newsService.GetNewsAndSentimentAsync();
                var sb = new StringBuilder();
                sb.AppendLine("\U0001F4F0 <b>Qlobal Kripto X\u0259b\u0259rl\u0259ri & Sentimenti (Az\u0259rbaycan Dilind\u0259)</b>");
                sb.AppendLine($"\U0001F4CA \u00DCmumi Bazar \u018Fhval\u0131: <b>{newsSummary.Status} ({newsSummary.OverallScore}%)</b>");
                sb.AppendLine();
                sb.AppendLine("Son x\u0259b\u0259rl\u0259r (ke\u00E7id \u00FC\u00E7\u00FCn x\u0259b\u0259r\u0259 klikl\u0259yin):");
                sb.AppendLine("-----------------------------------");
                foreach (var n in newsSummary.LatestNews.Take(5))
                {
                    var safeTitle = System.Net.WebUtility.HtmlEncode(n.Title);
                    sb.AppendLine($"\u2022 <b>[{n.Source}]</b> <a href=\"{n.Url}\">{safeTitle}</a> - <i>{n.Sentiment}</i>");
                }
                sb.AppendLine("-----------------------------------");
                await SendMessageAsync(sb.ToString(), chatId, BuildUserKeyboard(userSettings));
            }
            else
            {
                var potentialSym = text.ToUpper();
                if (!potentialSym.EndsWith("USDT")) potentialSym += "USDT";
                var tf = (userSettings.Timeframe == "Ham\u0131s\u0131" || userSettings.Timeframe == "Hamisi") ? "3m" : userSettings.Timeframe;
                var sig = await signalEngine.AnalyzeCoinAsync(potentialSym, tf);
                if (sig.SignalType != "MELUMAT AZDIR")
                {
                    await SendSignalAlertAsync(sig, chatId);
                }
                else
                {
                    var helpMsg = "\u2139\uFE0F <b>N\u0259 etm\u0259k laz\u0131md\u0131r?</b>\n\n" +
                                  "A\u015Fa\u011F\u0131dak\u0131 menyudan se\u00E7im edin v\u0259 ya analiz etm\u0259k ist\u0259diyiniz coinin ad\u0131n\u0131 yaz\u0131n.\n" +
                                  "\U0001F4CC M\u0259s\u0259l\u0259n: <code>SOL</code>, <code>BTC</code>, <code>ETH</code>, <code>DOGE</code>";
                    await SendMessageAsync(helpMsg, chatId, BuildUserKeyboard(userSettings));
                }
            }
        }

        private static object BuildUserKeyboard(UserSettings settings)
        {
            var toggleBtn = settings.IsActive ? "\U0001F6D1 Bildiri\u015Fl\u0259ri Dayand\u0131r" : "\u25B6\uFE0F Bildiri\u015Fl\u0259ri Ba\u015Flat";
            return new
            {
                keyboard = new[]
                {
                    new[] { new { text = "\U0001F4CA Bitcoin Kompas\u0131" }, new { text = "\u26A1 B\u00FCt\u00FCn Siqnallar" } },
                    new[] { new { text = "\u2B50 M\u0259nim Coinl\u0259rim" }, new { text = "\U0001F4F0 Bazar X\u0259b\u0259rl\u0259ri" } },
                    new[] { new { text = "\u2699\uFE0F Coin Se\u00E7imi" }, new { text = "\U0001F5D1 Coin Sil" } },
                    new[] { new { text = "\u23F1 Zaman \u00C7\u0259r\u00E7iv\u0259si" }, new { text = toggleBtn } }
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
                    new[] { new { text = "\u23F1 3 D\u0259qiq\u0259 (3m) Siqnallar\u0131" }, new { text = "\u23F1 5 D\u0259qiq\u0259 (5m) Siqnallar\u0131" } },
                    new[] { new { text = "\u23F1 15 D\u0259qiq\u0259 (15m) Siqnallar\u0131" }, new { text = "\u23F1 1 Saat (1h) Siqnallar\u0131" } },
                    new[] { new { text = "\u23F1 4 Saat (4h) Siqnallar\u0131" }, new { text = "\U0001F31F B\u00FCt\u00FCn Zamanlar (Ham\u0131s\u0131) Siqnallar\u0131" } },
                    new[] { new { text = "\u2B05\uFE0F \u018Fsas Menyu" } }
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
                    new[] { new { text = "\u23F1 3 D\u0259qiq\u0259 (3m)" }, new { text = "\u23F1 5 D\u0259qiq\u0259 (5m)" } },
                    new[] { new { text = "\u23F1 15 D\u0259qiq\u0259 (15m)" }, new { text = "\u23F1 1 Saat (1h)" } },
                    new[] { new { text = "\u23F1 4 Saat (4h)" }, new { text = "\U0001F31F B\u00FCt\u00FCn Zamanlar (Ham\u0131s\u0131)" } },
                    new[] { new { text = "\u2B05\uFE0F \u018Fsas Menyu" } }
                },
                resize_keyboard = true,
                one_time_keyboard = false
            };
        }
    }
}