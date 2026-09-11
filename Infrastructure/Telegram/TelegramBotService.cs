using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Application.Services;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace CryptoSense.Infrastructure.Telegram
{
    public partial class TelegramBotService : BackgroundService, ITelegramBotService
    {
        private readonly HttpClient _httpClient;
        private readonly AppConfig _config;
        private readonly IServiceProvider _serviceProvider;
        private long _lastUpdateId = 0;

        public static string? SuperAdminChatId = null;
        public static ConcurrentDictionary<string, UserSettings> UserPreferences { get; } = new();
        private static readonly ConcurrentDictionary<string, string> _userStates = new();
        private static readonly ConcurrentDictionary<string, int> _signalUserNumberMap = new();
        private static readonly ConcurrentDictionary<long, DateTime> _processedMessageIds = new();
        private static readonly ConcurrentDictionary<string, (string Text, DateTime Time)> _lastUserAction = new();
        private static readonly ConcurrentDictionary<string, bool> _loggedOutChats = new();
        private static readonly ConcurrentDictionary<string, string> _authenticatedSessions = new();
        private static readonly ConcurrentDictionary<string, bool> _testModeChats = new();
        private static readonly ConcurrentDictionary<string, FuturesSignal> _lastTestSignals = new();
        private static readonly SemaphoreSlim _sendNumberLock = new(1, 1);
        private static readonly ConcurrentDictionary<string, DateTime> _lastPortfolioSummarySent = new();
        private static readonly ConcurrentDictionary<string, (string Content, DateTime CachedAt)> _portfolioSummaryCache = new();
        private static readonly string DataDirectory = CryptoSense.Domain.Common.AppPaths.DataDirectory;
        private static readonly string SettingsFilePath = CryptoSense.Domain.Common.AppPaths.SettingsFilePath;
        private static readonly string SignalMapFilePath = CryptoSense.Domain.Common.AppPaths.SignalMapFilePath;

        public static readonly List<string> Default40Coins = new()
        {
            "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", 
            "LINKUSDT", "AVAXUSDT", "NEARUSDT", "DOTUSDT", "ADAUSDT", "ATOMUSDT", 
            "ARBUSDT", "OPUSDT", "SUIUSDT", "LTCUSDT",
            "UNIUSDT", "AAVEUSDT", "FILUSDT", "APTUSDT", "BCHUSDT", "TRXUSDT", 
            "TONUSDT", "INJUSDT", "SEIUSDT", "TIAUSDT", "WLDUSDT", "ENAUSDT", 
            "HYPEUSDT", "POLUSDT", "HBARUSDT", "XLMUSDT", "ETCUSDT", "LDOUSDT", 
            "RENDERUSDT", "FETUSDT", "TAOUSDT", "ONDOUSDT", "PENDLEUSDT", "1000PEPEUSDT"
        };

        public static readonly List<string> Default16Coins = Default40Coins;

        public static readonly List<string> OptionalCoins = new()
        {
            "UNIUSDT", "APTUSDT"
        };

        public static readonly HashSet<string> Supported50Coins = new(StringComparer.OrdinalIgnoreCase)
        {
            "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "1000PEPEUSDT", "AVAXUSDT", "NOTUSDT",
            "TONUSDT", "ADAUSDT", "LINKUSDT", "NEARUSDT", "APTUSDT", "TIAUSDT", "INJUSDT", "OPUSDT", "ARBUSDT", "RENDERUSDT",
            "FETUSDT", "TAOUSDT", "WIFUSDT", "FTMUSDT", "DOTUSDT", "LTCUSDT", "BCHUSDT", "UNIUSDT", "SEIUSDT", "JUPUSDT",
            "WLDUSDT", "SHIBUSDT", "BONKUSDT", "FLOKIUSDT", "ATOMUSDT", "XLMUSDT", "FILUSDT", "ETCUSDT", "ALGOUSDT", "ICPUSDT",
            "STXUSDT", "PYTHUSDT", "GALAUSDT", "SANDUSDT", "MANAUSDT", "AAVEUSDT", "CRVUSDT", "DYDXUSDT", "MKRUSDT", "PENDLEUSDT",
            "TRXUSDT", "ENAUSDT", "HYPEUSDT", "POLUSDT", "HBARUSDT", "LDOUSDT", "ONDOUSDT"
        };

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
                            var s = kvp.Value;
                            if (s.Coins == null)
                            {
                                s.Coins = new List<string>();
                            }
                            UserPreferences[kvp.Key] = s;
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

        public static void ResetAllAlertCounters()
        {
            try
            {
                foreach (var kvp in UserPreferences)
                {
                    kvp.Value.AlertCounter = 0;
                }
                _signalUserNumberMap.Clear();
                if (File.Exists(SignalMapFilePath))
                {
                    try { File.Delete(SignalMapFilePath); } catch { }
                }
                SaveSettings();
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

            // Hydrate active users from database into UserPreferences and _authenticatedSessions so preferences and scanning never stall
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
                var activeUsers = userManager.GetAllLoggedInActiveUsersAsync().GetAwaiter().GetResult();
                foreach (var u in activeUsers)
                {
                    if (!string.IsNullOrEmpty(u.TelegramChatId) && u.IsActive && u.IsLoggedIn)
                    {
                        _authenticatedSessions[u.TelegramChatId] = u.Username;
                        _loggedOutChats.TryRemove(u.TelegramChatId, out _);
                        if (u.Role == Domain.Enums.UserRole.Admin)
                        {
                            SuperAdminChatId = u.TelegramChatId;
                        }

                        var s = UserPreferences.GetOrAdd(u.TelegramChatId, _ => new UserSettings
                        {
                            Username = u.Username,
                            TelegramUserId = u.TelegramUserId,
                            IsActive = true,
                            Timeframe = "1h",
                            PortfolioMode = "Standard40",
                            Coins = new List<string>(Default40Coins)
                        });
                        s.Username = u.Username;
                        s.TelegramUserId = u.TelegramUserId;
                        s.IsActive = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBotService] Startup hydration error: {ex.Message}");
            }
        }

        public bool IsSuperAdmin(string chatId, long? userId, string telegramUsername)
        {
            if (userId.HasValue && (userId.Value == 1219998176 || userId.Value == _config.SuperAdminUserId))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(telegramUsername) && 
                (telegramUsername.Equals("Ali_Mahammadov", StringComparison.OrdinalIgnoreCase) || 
                 telegramUsername.Equals(_config.SuperAdminTelegram?.TrimStart('@'), StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(chatId) && 
                (chatId == "1219998176" || chatId == _config.SuperAdminChatId || chatId == SuperAdminChatId))
            {
                return true;
            }
            if (_authenticatedSessions.TryGetValue(chatId, out var user))
            {
                if (user.Equals("Ali", StringComparison.OrdinalIgnoreCase) || user.Equals("Ali Mahammadov", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static UserSettings GetSettings(string chatId)
        {
            var settings = UserPreferences.GetOrAdd(chatId, _ => new UserSettings
            {
                Coins = new List<string>(),
                Timeframe = "Təyin olunmayıb",
                PortfolioMode = "Təyin olunmayıb",
                IsActive = false
            });
            if (settings.Coins == null)
            {
                settings.Coins = new List<string>();
            }
            settings.Coins.RemoveAll(c => !System.Text.RegularExpressions.Regex.IsMatch(c, @"^[A-Z0-9]+USDT$") || c.Contains("⚡") || c.Contains("SIQNALLAR") || c.Contains("BÜTÜN") || c.Contains("BUTUN"));
            return settings;
        }

        public async Task<bool> CanReceivePushAsync(string chatId)
        {
            if (string.IsNullOrWhiteSpace(chatId)) return false;
            if (_loggedOutChats.ContainsKey(chatId)) return false;

            // 1. Session check: user must have active session in memory
            if (!_authenticatedSessions.ContainsKey(chatId))
            {
                return false;
            }

            // 2. UserPreferences check: must exist and be active
            if (!UserPreferences.TryGetValue(chatId, out var pref) || !pref.IsActive)
            {
                return false;
            }

            // 3. Database check: IsLoggedIn==true AND TelegramChatId dolu & matches AND IsActive==true
            // TƏK QAPI: İstisna YOX (SuperAdmin də yalnız öz chatId və aktiv sessiyası ilə keçir)
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var user = await uow.Users.GetByChatIdOrTelegramUserIdAsync(chatId, null);

                if (user == null)
                {
                    return false;
                }

                if (!user.IsLoggedIn || string.IsNullOrWhiteSpace(user.TelegramChatId) || user.TelegramChatId != chatId || !user.IsActive)
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CanReceivePushAsync] DB check error for {chatId}: {ex.Message}");
                return false;
            }
        }

        private async Task ClearSessionOnForbiddenAsync(string chatId)
        {
            try
            {
                _loggedOutChats[chatId] = true;
                _authenticatedSessions.TryRemove(chatId, out _);
                UserPreferences.TryRemove(chatId, out _);
                _userStates.TryRemove(chatId, out _);
                if (SuperAdminChatId == chatId) SuperAdminChatId = null;
                SaveSettings();

                using var scope = _serviceProvider.CreateScope();
                var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
                await userManager.LogoutAsync(chatId);
                await userManager.ClearChatBindingAsync(chatId, null);
            }
            catch { }
        }

        public async Task<bool> SendMessageAsync(string message, string targetChatId, object? replyMarkup = null)
        {
            if (string.IsNullOrWhiteSpace(_config.TelegramBotToken) || string.IsNullOrWhiteSpace(targetChatId))
            {
                return false;
            }

            // Telegram API maximum limit is 4096 chars. If message exceeds 3900 chars, split safely into chunks:
            if (message.Length > 3900)
            {
                var chunks = SplitMessage(message, 3800);
                for (int i = 0; i < chunks.Count; i++)
                {
                    var isLast = i == chunks.Count - 1;
                    var success = await SendSingleMessageAsync(chunks[i], targetChatId, isLast ? replyMarkup : null);
                    if (!success) return false;
                    if (!isLast) await Task.Delay(200);
                }
                return true;
            }

            return await SendSingleMessageAsync(message, targetChatId, replyMarkup);
        }

        private async Task<bool> SendSingleMessageAsync(string message, string targetChatId, object? replyMarkup = null)
        {
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
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        _ = ClearSessionOnForbiddenAsync(targetChatId);
                        return false;
                    }

                    var err = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[TelegramBotService] Send error: {response.StatusCode} - {err}");

                    // Fallback: If Telegram rejected due to HTML parsing error, strip HTML tags and retry as plain text
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && (err.Contains("can't parse entities") || err.Contains("Bad Request")))
                    {
                        try
                        {
                            var plainText = System.Text.RegularExpressions.Regex.Replace(message, "<.*?>", string.Empty);
                            payload["text"] = plainText;
                            payload.Remove("parse_mode");
                            var retryContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                            var retryResp = await _httpClient.PostAsync(url, retryContent);
                            return retryResp.IsSuccessStatusCode;
                        }
                        catch { }
                    }
                }
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBotService] Send error: {ex.Message}");
                return false;
            }
        }

        public async Task<long?> SendMessageReturnIdAsync(string message, string targetChatId, object? replyMarkup = null)
        {
            if (string.IsNullOrWhiteSpace(_config.TelegramBotToken) || string.IsNullOrWhiteSpace(targetChatId))
            {
                return null;
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
                if (response.IsSuccessStatusCode)
                {
                    var jsonStr = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonStr);
                    if (doc.RootElement.TryGetProperty("result", out var resElem) &&
                        resElem.TryGetProperty("message_id", out var msgIdElem))
                    {
                        return msgIdElem.GetInt64();
                    }
                }
                else
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        _ = ClearSessionOnForbiddenAsync(targetChatId);
                        return null;
                    }

                    var err = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[TelegramBotService] SendReturnId error: {response.StatusCode} - {err}");

                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && (err.Contains("can't parse entities") || err.Contains("Bad Request")))
                    {
                        var plainText = System.Text.RegularExpressions.Regex.Replace(message, "<.*?>", string.Empty);
                        payload["text"] = plainText;
                        payload.Remove("parse_mode");
                        var retryContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                        var retryResp = await _httpClient.PostAsync(url, retryContent);
                        if (retryResp.IsSuccessStatusCode)
                        {
                            var retryJson = await retryResp.Content.ReadAsStringAsync();
                            using var doc2 = JsonDocument.Parse(retryJson);
                            if (doc2.RootElement.TryGetProperty("result", out var resElem2) &&
                                resElem2.TryGetProperty("message_id", out var msgIdElem2))
                            {
                                return msgIdElem2.GetInt64();
                            }
                        }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBotService] SendReturnId error: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> EditMessageTextAsync(string chatId, long messageId, string text, object? replyMarkup = null)
        {
            if (string.IsNullOrWhiteSpace(_config.TelegramBotToken) || string.IsNullOrWhiteSpace(chatId))
            {
                return false;
            }

            if (text.Length > 3900)
            {
                return false; // Telegram edit limit: caller should send new message
            }

            try
            {
                var url = $"https://api.telegram.org/bot{_config.TelegramBotToken}/editMessageText";
                var payload = new Dictionary<string, object>
                {
                    { "chat_id", chatId },
                    { "message_id", messageId },
                    { "text", text },
                    { "parse_mode", "HTML" },
                    { "disable_web_page_preview", false }
                };

                if (replyMarkup != null)
                {
                    payload["reply_markup"] = replyMarkup;
                }

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        _ = ClearSessionOnForbiddenAsync(chatId);
                        return false;
                    }

                    var err = await response.Content.ReadAsStringAsync();
                    if (err.Contains("message is not modified")) return true;

                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && (err.Contains("can't parse entities") || err.Contains("Bad Request")))
                    {
                        try
                        {
                            var plainText = System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", string.Empty);
                            payload["text"] = plainText;
                            payload.Remove("parse_mode");
                            var retryContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                            var retryResp = await _httpClient.PostAsync(url, retryContent);
                            return retryResp.IsSuccessStatusCode;
                        }
                        catch { }
                    }
                }
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBotService] EditMessage error: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> AnswerCallbackQueryAsync(string callbackQueryId, string? text = null)
        {
            if (string.IsNullOrWhiteSpace(_config.TelegramBotToken) || string.IsNullOrWhiteSpace(callbackQueryId))
            {
                return false;
            }

            try
            {
                var url = $"https://api.telegram.org/bot{_config.TelegramBotToken}/answerCallbackQuery";
                var payload = new Dictionary<string, object>
                {
                    { "callback_query_id", callbackQueryId }
                };
                if (!string.IsNullOrEmpty(text))
                {
                    payload["text"] = text;
                }
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> SendDocumentAsync(string filePath, string targetChatId, string caption = "")
        {
            if (string.IsNullOrWhiteSpace(_config.TelegramBotToken) || string.IsNullOrWhiteSpace(targetChatId) || !File.Exists(filePath))
            {
                return false;
            }

            try
            {
                var url = $"https://api.telegram.org/bot{_config.TelegramBotToken}/sendDocument";
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(targetChatId), "chat_id");
                if (!string.IsNullOrEmpty(caption))
                {
                    form.Add(new StringContent(caption), "caption");
                    form.Add(new StringContent("HTML"), "parse_mode");
                }

                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var streamContent = new StreamContent(fileStream);
                streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                form.Add(streamContent, "document", Path.GetFileName(filePath));

                var response = await _httpClient.PostAsync(url, form);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[TelegramBotService] SendDocument error: {response.StatusCode} - {err}");
                }
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBotService] SendDocument error: {ex.Message}");
                return false;
            }
        }

        private static List<string> SplitMessage(string text, int maxChunkSize)
        {
            var result = new List<string>();
            var lines = text.Split('\n');
            var current = new StringBuilder();

            foreach (var line in lines)
            {
                if (current.Length + line.Length + 1 > maxChunkSize)
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                }

                if (line.Length > maxChunkSize)
                {
                    for (int i = 0; i < line.Length; i += maxChunkSize)
                    {
                        result.Add(line.Substring(i, Math.Min(maxChunkSize, line.Length - i)));
                    }
                }
                else
                {
                    current.AppendLine(line);
                }
            }

            if (current.Length > 0)
            {
                result.Add(current.ToString());
            }

            return result;
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
                _authenticatedSessions.TryRemove(chatId, out _);
                UserPreferences.TryRemove(chatId, out _);
                _userStates.TryRemove(chatId, out _);

                var kickMsg = "⛔ <b>HESABINIZ SİLİNDİ VƏ SİSTEMDƏN ÇIXARILDINIZ!</b>\n\n" +
                              "Hörmətli istifadəçi, hesabınız sistemdən silinmişdir və bütün aktiv prosesləriniz dayandırılmışdır.\n\n" +
                              "Yenidən giriş icazəsi üçün <b>Super Admin</b> ilə əlaqə saxlayın:\n" +
                              "👉 <a href=\"https://t.me/Ali_Mahammadov\">@Ali_Mahammadov</a>";

                await SendMessageAsync(kickMsg, chatId, new { remove_keyboard = true });
            }
        }

        public async Task<bool> SendSignalAlertAsync(FuturesSignal signal, string? specificChatId = null)
        {
            // Strict Timeframe check: Only 1h, 4h
            if (signal.Timeframe == "15m") return false;

            // Strict DataAge check: <= 1000ms
            if (signal.DataAgeMs > 1000)
            {
                Console.WriteLine($"[TelegramBotService] DataAge gate blocked: {signal.Symbol} DataAge={signal.DataAgeMs}ms > 1000ms");
                return false;
            }

            decimal tp1DistCheck = Math.Abs(signal.TakeProfit1 - signal.EntryPrice);
            decimal tp2DistCheck = signal.TakeProfit2 > 0 ? Math.Abs(signal.TakeProfit2 - signal.EntryPrice) : tp1DistCheck;
            decimal weightedTpDistCheck = (0.50m * tp1DistCheck) + (0.50m * tp2DistCheck);
            decimal slDistCheck = Math.Abs(signal.StopLoss - signal.EntryPrice);
            decimal rrCheck = slDistCheck > 0 ? (weightedTpDistCheck / slDistCheck) : 0m;
            if (rrCheck < 1.30m)
            {
                Console.WriteLine($"[TelegramBotService] R:R filter blocked (Weighted R:R {rrCheck:F2} < 1.30)");
                return false;
            }

            await _sendNumberLock.WaitAsync();
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                // Nömrə: Telegram true-dan SONRA, bir lock, bir sıra (#1,#2,#3…). #6-dan #68-ə tullanma = fail.
                int nextSequentialNum = await uow.Signals.GetNextSequentialSignalNumberAsync();

                if (!string.IsNullOrEmpty(specificChatId))
                {
                    if (!await CanReceivePushAsync(specificChatId)) return false;
                    var deliveredChats = await uow.Signals.GetDeliveredChatIdsAsync(signal.Id);
                    if (deliveredChats.Contains(specificChatId))
                    {
                        return false;
                    }

                    var settings = GetSettings(specificChatId);
                    var userSigNum = nextSequentialNum;
                    signal.SignalNumber = userSigNum;
                    _signalUserNumberMap[$"{signal.Id}_{specificChatId}"] = userSigNum;
                    var msg = TelegramMessageFormatter.FormatSignalAlert(signal, userSigNum);
                    bool sent = await SendMessageAsync(msg, specificChatId);
                    if (sent)
                    {
                        try
                        {
                            var committedNum = await uow.Signals.CommitSignalNumberOnSendSuccessAsync(signal.Id);
                            settings.AlertCounter = Math.Max(settings.AlertCounter, committedNum);
                            settings.LastSignalSentUtc = DateTime.UtcNow;
                            settings.LastHeartbeatSentUtc = DateTime.UtcNow;
                            SaveSettings();
                            await uow.Signals.RecordDeliveryAsync(signal.Id, specificChatId, committedNum);
                            signal.SignalAlertSent = true;
                            signal.SignalNumber = committedNum;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[TelegramBotService] RecordDelivery specific error: {ex.Message}");
                        }
                    }
                    else
                    {
                        signal.SignalNumber = 0;
                    }
                    return sent;
                }

                var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
                var activeUsers = await userManager.GetAllLoggedInActiveUsersAsync();

                var targetChatIds = new HashSet<string>();
                foreach (var user in activeUsers)
                {
                    if (!string.IsNullOrEmpty(user.TelegramChatId) && user.IsActive && user.IsLoggedIn)
                    {
                        targetChatIds.Add(user.TelegramChatId);
                    }
                }

                bool anyDelivered = false;

                foreach (var chatId in targetChatIds)
                {
                    if (!await CanReceivePushAsync(chatId)) continue;
                    var settings = GetSettings(chatId);
                    if (!settings.IsActive) continue;

                    // Strict Timeframe check: Only 1h, 4h
                    if (settings.Timeframe != "Hamısı" && settings.Timeframe != "Hamisi" && settings.Timeframe != signal.Timeframe)
                    {
                        continue;
                    }

                    // Strict Chronological check: never send a signal generated before the user selected timeframe / resumed
                    if (signal.GeneratedAt < settings.LastResumeTime.AddSeconds(-15))
                    {
                        continue;
                    }

                    // Strict Candle Freshness check: never deliver a signal whose closed candle is older than tolerance
                    var candleDuration = signal.Timeframe switch
                    {
                        "4h" => TimeSpan.FromHours(4),
                        _ => TimeSpan.FromHours(1)
                    };
                    var maxTolerance = signal.Timeframe switch
                    {
                        "4h" => TimeSpan.FromHours(3),
                        _ => TimeSpan.FromMinutes(50)
                    };
                    var candleCloseUtc = signal.SourceCandleOpenTimeUtc + candleDuration;
                    if (DateTime.UtcNow - candleCloseUtc > maxTolerance)
                    {
                        continue;
                    }

                    // Strict R:R Gate: Weighted R:R = TP1 (1.0R - 50%) + TP2 (min(2.0R, struct) - 50%). R:R < 1.30 isə send=NO
                    decimal tp1Dist = Math.Abs(signal.TakeProfit1 - signal.EntryPrice);
                    decimal tp2Dist = signal.TakeProfit2 > 0 ? Math.Abs(signal.TakeProfit2 - signal.EntryPrice) : tp1Dist;
                    decimal weightedTpDist = (0.50m * tp1Dist) + (0.50m * tp2Dist);
                    decimal slDist = Math.Abs(signal.StopLoss - signal.EntryPrice);
                    decimal rr = slDist > 0 ? (weightedTpDist / slDist) : 0m;
                    if (rr < 1.30m)
                    {
                        continue;
                    }

                    // Strict User Coin Filter: User only receives signals if they have explicitly selected coins.
                    if (settings.Coins.Count == 0) continue;
                    bool coinMatched = settings.Coins.Contains(signal.Symbol)
                        || (!string.IsNullOrEmpty(signal.CleanSymbol) && settings.Coins.Contains(signal.CleanSymbol))
                        || settings.Coins.Contains(signal.Symbol.Replace("USDT", ""))
                        || (!string.IsNullOrEmpty(signal.CleanSymbol) && settings.Coins.Contains(signal.CleanSymbol + "USDT"));
                    if (!coinMatched) continue;

                    // Fresh Entry Filter: If price drifted > 0.35% away from entry towards TP1 or StopLoss, don't send stale setup
                    if (signal.CurrentPrice > 0 && signal.EntryPrice > 0)
                    {
                        bool isLong = signal.Direction == SignalDirection.Buy || signal.SignalType.Contains("LONG");
                        if (isLong && signal.TakeProfit1 > signal.EntryPrice)
                        {
                            decimal maxAllowed = signal.EntryHigh > 0 ? signal.EntryHigh * 1.0035m : signal.EntryPrice * 1.0035m;
                            if (signal.CurrentPrice > maxAllowed) continue;
                        }
                        else if (!isLong && signal.TakeProfit1 < signal.EntryPrice)
                        {
                            decimal minAllowed = signal.EntryLow > 0 ? signal.EntryLow * 0.9965m : signal.EntryPrice * 0.9965m;
                            if (signal.CurrentPrice < minAllowed) continue;
                        }
                    }

                    // Strict Delivery Dedup: Never deliver the same signal twice to the same user
                    var deliveredChats = await uow.Signals.GetDeliveredChatIdsAsync(signal.Id);
                    if (deliveredChats.Contains(chatId))
                    {
                        continue;
                    }

                    // Limit checks: Max 10 signals per day, Max 5 open positions
                    var todayCount = await uow.Signals.GetUserTodaySignalsCountAsync(chatId);
                    if (todayCount >= 10) continue;

                    var openCount = await uow.Signals.GetUserOpenSignalsCountAsync(chatId);
                    if (openCount >= 5) continue;

                    var userSigNum = nextSequentialNum;
                    signal.SignalNumber = userSigNum;
                    _signalUserNumberMap[$"{signal.Id}_{chatId}"] = userSigNum;
                    var msg = TelegramMessageFormatter.FormatSignalAlert(signal, userSigNum);
                    bool sent = await SendMessageAsync(msg, chatId);
                    if (sent)
                    {
                        anyDelivered = true;
                        try
                        {
                            settings.AlertCounter = Math.Max(settings.AlertCounter, userSigNum);
                            settings.LastSignalSentUtc = DateTime.UtcNow;
                            settings.LastHeartbeatSentUtc = DateTime.UtcNow;
                            SaveSettings();
                            await uow.Signals.RecordDeliveryAsync(signal.Id, chatId, userSigNum);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[TelegramBotService] RecordDelivery error: {ex.Message}");
                        }
                    }
                }

                if (anyDelivered)
                {
                    try
                    {
                        var committedNum = await uow.Signals.CommitSignalNumberOnSendSuccessAsync(signal.Id);
                        signal.SignalAlertSent = true;
                        signal.SignalNumber = committedNum;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TelegramBotService] Error committing signal number: {ex.Message}");
                    }
                }
                else
                {
                    signal.SignalNumber = 0;
                }

                return anyDelivered;
            }
            finally
            {
                _sendNumberLock.Release();
            }
        }

        public async Task SendOutcomeAlertAsync(FuturesSignal signal, string outcomeType, decimal hitPrice, decimal profitPct)
        {
            using var scope = _serviceProvider.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // Query ONLY chatIds that ACTUALLY received this entry signal
            var deliveredChatIds = await uow.Signals.GetDeliveredChatIdsAsync(signal.Id);
            var targetChatIds = new HashSet<string>(deliveredChatIds);

            // In-memory fallback
            foreach (var kvp in _signalUserNumberMap)
            {
                if (kvp.Key.StartsWith($"{signal.Id}_") && kvp.Value > 0)
                {
                    var parts = kvp.Key.Split('_');
                    if (parts.Length == 2) targetChatIds.Add(parts[1]);
                }
            }
            foreach (var kvp in signal.UserSignalNumbers)
            {
                if (kvp.Value > 0) targetChatIds.Add(kvp.Key);
            }

            if (targetChatIds.Count == 0)
            {
                // NO USER EVER RECEIVED THIS SIGNAL ENTRY! NEVER BROADCAST OUTCOME!
                return;
            }

            foreach (var chatId in targetChatIds)
            {
                if (!await CanReceivePushAsync(chatId)) continue;
                var settings = GetSettings(chatId);
                if (!settings.IsActive) continue;

                int userSigNum = await uow.Signals.GetUserSignalNumberAsync(signal.Id, chatId);
                if (userSigNum == 0)
                {
                    if (!_signalUserNumberMap.TryGetValue($"{signal.Id}_{chatId}", out userSigNum) || userSigNum == 0)
                    {
                        if (!signal.UserSignalNumbers.TryGetValue(chatId, out userSigNum) || userSigNum == 0)
                        {
                            userSigNum = signal.SignalNumber;
                        }
                    }
                }

                if (userSigNum == 0) continue;

                var msg = TelegramMessageFormatter.FormatOutcomeAlert(signal, userSigNum, outcomeType, hitPrice, profitPct);
                await SendMessageAsync(msg, chatId);
                settings.LastHeartbeatSentUtc = DateTime.UtcNow;
                SaveSettings();
            }
        }

        public async Task SendVolatilityRiskAlertAsync(string symbol, decimal currentPrice, decimal priceChange24h, decimal volatilityRatio, string reason)
        {
            var msg = TelegramMessageFormatter.FormatVolatilityRiskAlert(symbol, currentPrice, priceChange24h, volatilityRatio, reason);

            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var activeUsers = await userManager.GetAllLoggedInActiveUsersAsync();

            var targetChatIds = new HashSet<string>();
            foreach (var user in activeUsers)
            {
                if (!string.IsNullOrEmpty(user.TelegramChatId) && user.IsActive && user.IsLoggedIn)
                {
                    targetChatIds.Add(user.TelegramChatId);
                }
            }

            foreach (var chatId in targetChatIds)
            {
                if (!await CanReceivePushAsync(chatId)) continue;
                var settings = GetSettings(chatId);
                if (!settings.IsActive) continue;
                // Only send volatility alert if user follows this coin or has chosen Hamısı
                if (settings.Coins.Count > 0 && !settings.Coins.Contains(symbol)) continue;
                await SendMessageAsync(msg, chatId);
            }
        }

        public async Task SendUrgentNewsAlertAsync(CryptoNewsItem newsItem, bool isListing = false)
        {
            var msg = TelegramMessageFormatter.FormatUrgentNewsAlert(newsItem, isListing);

            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var activeUsers = await userManager.GetAllLoggedInActiveUsersAsync();

            var targetChatIds = new HashSet<string>();
            foreach (var user in activeUsers)
            {
                if (!string.IsNullOrEmpty(user.TelegramChatId) && user.IsActive && user.IsLoggedIn)
                {
                    targetChatIds.Add(user.TelegramChatId);
                }
            }

            foreach (var chatId in targetChatIds)
            {
                if (!await CanReceivePushAsync(chatId)) continue;
                var settings = GetSettings(chatId);
                if (!settings.IsActive) continue;
                await SendMessageAsync(msg, chatId);
            }
        }

        public async Task BroadcastSystemAlertAsync(string message)
        {
            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var activeUsers = await userManager.GetAllLoggedInActiveUsersAsync();

            var targetChatIds = new HashSet<string>();
            foreach (var user in activeUsers)
            {
                if (!string.IsNullOrEmpty(user.TelegramChatId) && user.IsActive && user.IsLoggedIn)
                {
                    targetChatIds.Add(user.TelegramChatId);
                }
            }

            foreach (var chatId in targetChatIds)
            {
                if (!await CanReceivePushAsync(chatId)) continue;
                var settings = GetSettings(chatId);
                if (!settings.IsActive) continue;
                await SendMessageAsync(message, chatId);
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

        public async Task SendDailyReportAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // QIZIL QAYDA: Gündəlik hesabat Bakı təqvimi (UTC+4) ilə hər gün yalnız 1 DƏFƏ göndərilə bilər.
            // Baza (AuditLogs) üzərindən yoxlanılır — heç bir restart və ya deploy təkrar hesabat çıxara BİLMƏZ.
            var bakuDateStr = DateTime.UtcNow.AddHours(4).ToString("yyyy-MM-dd");
            if (await uow.AuditLogs.HasDailyReportBeenSentAsync(bakuDateStr))
            {
                Console.WriteLine($"[DailyReport] Bakı tarixi {bakuDateStr} üçün gün sonu hesabatı artıq bazada mövcuddur. Təkrar ləğv edildi.");
                return;
            }

            // Dərhal bazaya qeyd edilir ki, paralel və ya ardıcıl deploy çağırışları təkrar göndərə bilməsin
            await uow.AuditLogs.RecordDailyReportSentAsync(bakuDateStr);

            var sinceUtc = DateTime.UtcNow.AddHours(-24);
            var todaySignals = await uow.Signals.GetSignalsSinceAsync(sinceUtc);
            var globalCoinsEntered = todaySignals.Select(s => s.Symbol.Replace("USDT", "")).Distinct().ToList();
            var defaultReasons = new List<string>
            {
                "Bazar konsolidasiyası və ADX < 16 olan cütlüklər kənarlaşdırıldı",
                "R:R < 1.30 olan qeyri-sabit setup-lar bloklandı",
                "Spayk və qeyri-təbii dalğalanma olan riskli zonalar filtrləndi"
            };

            // 1. Send global report to SuperAdmin
            if (!string.IsNullOrEmpty(SuperAdminChatId))
            {
                if (await CanReceivePushAsync(SuperAdminChatId))
                {
                    var globalStats = await uow.Signals.GetPerformanceStatsAsync();
                    var adminMsg = TelegramMessageFormatter.FormatDailyReport(globalStats, globalCoinsEntered, defaultReasons, isSuperAdmin: true);
                    await SendMessageAsync(adminMsg, SuperAdminChatId);
                }
            }

            // 2. Send personal daily report to each active user
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var activeUsers = await userManager.GetAllLoggedInActiveUsersAsync();
            foreach (var user in activeUsers)
            {
                var chatId = user.TelegramChatId;
                if (string.IsNullOrEmpty(chatId)) continue;
                if (!await CanReceivePushAsync(chatId)) continue;
                if (chatId == SuperAdminChatId) continue;

                var userStats = await uow.Signals.GetUserPerformanceStatsAsync(chatId);
                var userOpenSignals = await uow.Signals.GetUserOpenSignalsAsync(chatId);
                var userCoinsEntered = userOpenSignals.Select(s => s.Symbol.Replace("USDT", "")).Distinct().ToList();

                var userMsg = TelegramMessageFormatter.FormatDailyReport(userStats, userCoinsEntered, defaultReasons, isSuperAdmin: false);
                await SendMessageAsync(userMsg, chatId);
            }
        }

        public async Task<string> BuildPortfolioSummaryAsync(string chatId, UserSettings userSettings, bool forceRefresh = false)
        {
            if (userSettings.Coins.Count == 0)
            {
                // BƏND 7: 0 coin → ticarət blok, 40 avtomatik Açma
                return "⚠️ <b>Portfel boşdur (0 coin seçilib).</b>\n\n⛔ Ticarət siqnalları bloklanıb. Siqnal almaq üçün menyudan coinləri seçin.";
            }

            // 45-second cache to prevent hammering Binance API upon rapid clicks
            if (!forceRefresh && _portfolioSummaryCache.TryGetValue(chatId, out var cached))
            {
                if ((DateTime.UtcNow - cached.CachedAt).TotalSeconds < 45)
                {
                    return cached.Content;
                }
            }

            var timeframe = userSettings.Timeframe;
            if (string.IsNullOrWhiteSpace(timeframe) || timeframe == "Təyin olunmayıb")
            {
                timeframe = "1h"; // display fallback without overwriting settings
            }

            using var scope = _serviceProvider.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
            var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();

            var tfDisplay = (timeframe == "Hamısı" || timeframe == "Hamisi") ? "1h, 4h" : timeframe;
            var userOpenSignals = await uow.Signals.GetUserOpenSignalsAsync(chatId);
            if (timeframe != "Hamısı" && timeframe != "Hamisi")
            {
                userOpenSignals = userOpenSignals.Where(s => s.Timeframe == timeframe).ToList();
            }

            // 1. Fetch BTC Compass
            BtcMarketCompass? btcCompass = null;
            try
            {
                btcCompass = await signalEngine.GetBtcCompassAsync();
            }
            catch { }

            // 2. Fetch live tickers for the selected coins
            List<CoinTicker> matchedTickers = new();
            try
            {
                var allTickers = await marketData.GetTopFuturesTickersAsync(250);
                var coinSet = new HashSet<string>(userSettings.Coins, StringComparer.OrdinalIgnoreCase);
                matchedTickers = allTickers.Where(t => coinSet.Contains(t.Symbol) || coinSet.Contains(t.Symbol.Replace("1000", "")) || coinSet.Contains("1000" + t.Symbol)).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BuildPortfolioSummary] Top futures tickers error: {ex.Message}");
            }

            // Ensure all tracked coins are present (parallel fetch via Task.WhenAll for fast sub-second load)
            var missingCoins = userSettings.Coins
                .Where(coin => !matchedTickers.Any(t => t.Symbol.Equals(coin, StringComparison.OrdinalIgnoreCase)
                                                     || t.Symbol.Equals(coin.Replace("1000", ""), StringComparison.OrdinalIgnoreCase)
                                                     || ("1000" + t.Symbol).Equals(coin, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (missingCoins.Count > 0)
            {
                var fetchTasks = missingCoins.Select(async coin =>
                {
                    try
                    {
                        var singleTicker = await marketData.Get24hTickerAsync(coin);
                        if (singleTicker != null && singleTicker.Price > 0) return singleTicker;

                        var altSym = coin.Replace("1000", "");
                        singleTicker = await marketData.Get24hTickerAsync(altSym);
                        if (singleTicker != null && singleTicker.Price > 0) return singleTicker;
                    }
                    catch { }
                    return null;
                });

                var results = await Task.WhenAll(fetchTasks);
                foreach (var r in results)
                {
                    if (r != null) matchedTickers.Add(r);
                }
            }

            // Fallback from LivePriceCache if any coin is still missing, and update real-time tick prices
            var liveCache = scope.ServiceProvider.GetService<LivePriceCache>();
            if (liveCache != null)
            {
                foreach (var t in matchedTickers)
                {
                    var snap = liveCache.GetSnapshot(t.Symbol) ?? liveCache.GetSnapshot(t.Symbol.Replace("1000", "")) ?? liveCache.GetSnapshot("1000" + t.Symbol);
                    if (snap != null && snap.Last > 0)
                    {
                        t.Price = snap.Last;
                    }
                }

                foreach (var coin in userSettings.Coins)
                {
                    if (!matchedTickers.Any(t => t.Symbol.Equals(coin, StringComparison.OrdinalIgnoreCase)
                                              || t.Symbol.Equals(coin.Replace("1000", ""), StringComparison.OrdinalIgnoreCase)
                                              || ("1000" + t.Symbol).Equals(coin, StringComparison.OrdinalIgnoreCase)))
                    {
                        var snap = liveCache.GetSnapshot(coin) ?? liveCache.GetSnapshot(coin.Replace("1000", ""));
                        matchedTickers.Add(new CoinTicker
                        {
                            Symbol = coin,
                            Price = (snap != null && snap.Last > 0) ? snap.Last : 0m,
                            PriceChangePercent = 0m
                        });
                    }
                }
            }

            // 3. Unify BTC price: ensure BTC in matchedTickers matches btcCompass exactly
            if (btcCompass != null && btcCompass.Price > 0)
            {
                var btcTicker = matchedTickers.FirstOrDefault(t => t.Symbol.Equals("BTCUSDT", StringComparison.OrdinalIgnoreCase));
                if (btcTicker != null)
                {
                    btcTicker.Price = btcCompass.Price;
                    btcTicker.PriceChangePercent = btcCompass.Change24h;
                }
            }

            var modeLabel = userSettings.PortfolioMode switch
            {
                "Standard40" => "🪙 Standart 40 Coin",
                "Custom" => "⭐ Fərdi Coinlər",
                "Combined" => "🔥 40 + Fərdi Coin (Kombinə)",
                _ => "🪙 Standart 40 Coin"
            };

            var sb = new StringBuilder();
            sb.AppendLine($"📊 <b>{modeLabel} — Canlı Bazar Xülasəsi 🟢</b>\n");
            sb.AppendLine($"⏱ <b>Aktiv Zaman Kəsiyi:</b> <code>{tfDisplay}</code>");
            sb.AppendLine($"🪙 <b>İzlənən Portfel:</b> <b>{userSettings.Coins.Count} ədəd coin</b>");
            sb.AppendLine($"🕒 <b>Məlumat Vaxtı:</b> <code>{Domain.Common.TimeHelper.FormatAz(DateTime.UtcNow)}</code> (Bakı)\n");

            // BTC Market Regime & Benchmark
            if (btcCompass != null && btcCompass.Price > 0)
            {
                var btcSign = btcCompass.Change24h >= 0 ? "+" : "";
                var btcIcon = btcCompass.Change24h >= 0 ? "🟢" : "🔴";
                sb.AppendLine($"🧭 <b>Bitcoin Kompası (BTC/USDT):</b>");
                sb.AppendLine($"• <b>Qiymət:</b> ${btcCompass.Price.ToString("N2", CultureInfo.InvariantCulture)} ({btcSign}{btcCompass.Change24h.ToString("F2", CultureInfo.InvariantCulture)}% {btcIcon})");
                sb.AppendLine($"• <b>Bazar Rejimi:</b> {btcCompass.Trend}");
                sb.AppendLine();
            }

            // Live status of tracked portfolio
            if (matchedTickers.Count > 0)
            {
                var gainers = matchedTickers.Where(t => t.PriceChangePercent > 0).OrderByDescending(t => t.PriceChangePercent).ToList();
                var losers = matchedTickers.Where(t => t.PriceChangePercent < 0).OrderBy(t => t.PriceChangePercent).ToList();
                var neutral = matchedTickers.Where(t => t.PriceChangePercent == 0).ToList();

                sb.AppendLine($"📈 <b>Portfeldəki Coinlərin Canlı Vəziyyəti:</b>");
                sb.AppendLine($"• <b>Bazar Balansı:</b> 🟢 {gainers.Count} Yüksələn | 🔴 {losers.Count} Düşən{(neutral.Count > 0 ? $" | ⚪ {neutral.Count} Sabit" : "")}\n");

                if (gainers.Count > 0)
                {
                    var topGainers = string.Join(" | ", gainers.Take(3).Select(t => $"{t.Symbol.Replace("USDT", "")}: +{t.PriceChangePercent.ToString("F2", CultureInfo.InvariantCulture)}%"));
                    sb.AppendLine($"🔥 <b>Ən Çox Artanlar:</b> <code>{topGainers}</code>");
                }
                if (losers.Count > 0)
                {
                    var topLosers = string.Join(" | ", losers.Take(3).Select(t => $"{t.Symbol.Replace("USDT", "")}: {t.PriceChangePercent.ToString("F2", CultureInfo.InvariantCulture)}%"));
                    sb.AppendLine($"❄️ <b>Ən Çox Düşənlər:</b> <code>{topLosers}</code>");
                }
                sb.AppendLine();

                sb.AppendLine($"📋 <b>Portfeldəki Bütün Coinlərin Canlı Qiyməti və 24s Dəyişimi ({matchedTickers.Count}/{userSettings.Coins.Count}):</b>");
                var sorted = matchedTickers.OrderByDescending(t => t.PriceChangePercent).ToList();
                var chunkList = new List<string>();
                foreach (var t in sorted)
                {
                    var cName = t.Symbol.Replace("USDT", "");
                    var pSign = t.PriceChangePercent >= 0 ? "+" : "";
                    var pIcon = t.PriceChangePercent >= 0 ? "🟢" : "🔴";
                    var priceFormatted = t.Price >= 1000 
                        ? t.Price.ToString("N2", CultureInfo.InvariantCulture) 
                        : (t.Price >= 1 
                            ? t.Price.ToString("F2", CultureInfo.InvariantCulture) 
                            : (t.Price >= 0.001m 
                                ? t.Price.ToString("F4", CultureInfo.InvariantCulture) 
                                : t.Price.ToString("F6", CultureInfo.InvariantCulture)));
                    chunkList.Add($"{cName}: ${priceFormatted} ({pSign}{t.PriceChangePercent.ToString("F2", CultureInfo.InvariantCulture)}% {pIcon})");
                }
                for (int i = 0; i < chunkList.Count; i += 2)
                {
                    var row = string.Join(" | ", chunkList.Skip(i).Take(2));
                    sb.AppendLine($"• <code>{row}</code>");
                }
                sb.AppendLine();
            }

            // Open trades or scanner status
            if (userOpenSignals.Count > 0)
            {
                sb.AppendLine($"📌 <b>Hazırda Açıq İzlənən Əməliyyatlar ({userOpenSignals.Count} ədəd):</b>");
                foreach (var sig in userOpenSignals)
                {
                    var num = sig.UserSignalNumbers.TryGetValue(chatId, out var n) ? n : sig.SignalNumber;
                    var icon = sig.SignalType.Contains("LONG") ? "🟢" : "🔴";
                    var dir = (sig.Direction == SignalDirection.Buy || sig.SignalType.Contains("LONG")) ? "LONG" : "SHORT";
                    sb.AppendLine($"• <b>#{num} {sig.CleanSymbol}</b> ({sig.Timeframe}) - {dir} {icon} (${sig.EntryPrice.ToString(CultureInfo.InvariantCulture)}) | Confluence: <b>{sig.ConfluenceScore.ToString("F1", CultureInfo.InvariantCulture)}%</b>");
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"🎯 <b>Skaner Analizi:</b> <i>Bütün {userSettings.Coins.Count} coin üzrə RSI, ADX, SuperTrend, EMA20/50 və Volume Wave davamlı analiz edilir.</i>");
                sb.AppendLine($"⚡ <i>Növbəti şam bağlanışında Confluence ≥ 75% qeydə alınan kimi dərhal əməliyyat kartı göndəriləcək.</i>\n");
            }

            bool isTestMode = _testModeChats.ContainsKey(chatId);
            if (isTestMode)
            {
                sb.AppendLine($"🧪 <b>Test Rejimi:</b> Aktivdir 🟢\n<i>Simulyasiya test siqnalı hazırlanır...</i>");
            }

            var result = sb.ToString();
            _portfolioSummaryCache[chatId] = (result, DateTime.UtcNow);
            return result;
        }

        public async Task BuildAndSendPortfolioSummaryAsync(string chatId, UserSettings userSettings, bool forceRefresh = false)
        {
            try
            {
                var summary = await BuildPortfolioSummaryAsync(chatId, userSettings, forceRefresh);
                if (string.IsNullOrWhiteSpace(summary)) return;

                var kb = TelegramKeyboards.BuildPortfolioSummaryKeyboard();
                if (userSettings.LastPortfolioSummaryMessageId.HasValue)
                {
                    var edited = await EditMessageTextAsync(chatId, userSettings.LastPortfolioSummaryMessageId.Value, summary, kb);
                    if (edited) return;
                }

                var newMsgId = await SendMessageReturnIdAsync(summary, chatId, kb);
                if (newMsgId.HasValue)
                {
                    userSettings.LastPortfolioSummaryMessageId = newMsgId;
                    SaveSettings();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBotService] Portfolio summary error: {ex.Message}");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (string.IsNullOrWhiteSpace(_config.TelegramBotToken)) return;

            // Ensure no lingering webhook exists to prevent conflicts or duplicate updates
            try
            {
                var delWebhookUrl = $"https://api.telegram.org/bot{_config.TelegramBotToken}/deleteWebhook?drop_pending_updates=true";
                var delResp = await _httpClient.GetAsync(delWebhookUrl, stoppingToken);
                Console.WriteLine($"[TelegramBotService] Startup deleteWebhook executed: {delResp.StatusCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelegramBotService] Startup deleteWebhook notice: {ex.Message}");
            }

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

                                // Duplicate prevention: ignore already processed Telegram messageId
                                if (!_processedMessageIds.TryAdd(messageId, DateTime.UtcNow))
                                {
                                    continue;
                                }

                                // Prune old messageIds periodically (keep for 30 minutes)
                                if (_processedMessageIds.Count > 2000)
                                {
                                    var cutoff = DateTime.UtcNow.AddMinutes(-30);
                                    foreach (var kvp in _processedMessageIds)
                                    {
                                        if (kvp.Value < cutoff) _processedMessageIds.TryRemove(kvp.Key, out _);
                                    }
                                }

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

                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await HandleIncomingMessageAsync(chatId, fromUser, fromUserId, messageId, text);
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[TelegramBotService] Handler error: {ex.Message}");
                                    }
                                });
                            }
                            else if (item.TryGetProperty("callback_query", out var cb))
                            {
                                var cbId = cb.GetProperty("id").GetString() ?? "";
                                var cbData = (cb.TryGetProperty("data", out var dEl) ? dEl.GetString() : "") ?? "";
                                string cbChatId = "";
                                long cbMessageId = 0;
                                string fromUser = "";
                                long? fromUserId = null;

                                if (cb.TryGetProperty("from", out var fromEl))
                                {
                                    if (fromEl.TryGetProperty("username", out var uNameEl))
                                        fromUser = (uNameEl.GetString() ?? "").TrimStart('@');
                                    if (fromEl.TryGetProperty("id", out var idEl))
                                        fromUserId = idEl.GetInt64();
                                }

                                if (cb.TryGetProperty("message", out var cbMsg))
                                {
                                    cbChatId = cbMsg.GetProperty("chat").GetProperty("id").GetInt64().ToString();
                                    cbMessageId = cbMsg.GetProperty("message_id").GetInt64();
                                }

                                if (!string.IsNullOrEmpty(cbChatId) && !string.IsNullOrEmpty(cbData))
                                {
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            await AnswerCallbackQueryAsync(cbId);
                                            await HandleCallbackQueryAsync(cbChatId, cbMessageId, cbData, fromUser, fromUserId);
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[TelegramBotService] Callback error: {ex.Message}");
                                        }
                                    });
                                }
                            }
                        }
                    }
                }
                catch
                {
                }

                await Task.Delay(100, stoppingToken);
            }
        }
    }
}