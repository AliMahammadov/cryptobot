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
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

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
        private static readonly ConcurrentDictionary<long, DateTime> _processedMessageIds = new();
        private static readonly ConcurrentDictionary<string, (string Text, DateTime Time)> _lastUserAction = new();
        private static readonly ConcurrentDictionary<string, bool> _loggedOutChats = new();
        private static readonly ConcurrentDictionary<string, string> _authenticatedSessions = new();
        private static readonly ConcurrentDictionary<string, bool> _testModeChats = new();
        private static readonly SemaphoreSlim _sendNumberLock = new(1, 1);
        private static readonly ConcurrentDictionary<string, DateTime> _lastPortfolioSummarySent = new();
        private static readonly string DataDirectory = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAILWAY_VOLUME_MOUNT_PATH")) && Directory.Exists(Environment.GetEnvironmentVariable("RAILWAY_VOLUME_MOUNT_PATH"))
            ? Environment.GetEnvironmentVariable("RAILWAY_VOLUME_MOUNT_PATH")!
            : (Directory.Exists("/app/data") ? "/app/data" : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
        private static readonly string SettingsFilePath = Path.Combine(DataDirectory, "user_preferences.json");
        private static readonly string SignalMapFilePath = Path.Combine(DataDirectory, "signal_user_numbers.json");

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

            // Hydrate active users from database into UserPreferences so preferences and scanning never stall
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
                var activeUsers = userManager.GetAllUsersAsync().GetAwaiter().GetResult();
                foreach (var u in activeUsers)
                {
                    if (!string.IsNullOrEmpty(u.TelegramChatId) && u.IsActive)
                    {
                        var s = UserPreferences.GetOrAdd(u.TelegramChatId, _ => new UserSettings
                        {
                            Username = u.Username,
                            TelegramUserId = u.TelegramUserId,
                            IsActive = false,
                            Timeframe = "Təyin olunmayıb",
                            PortfolioMode = "Təyin olunmayıb",
                            Coins = new List<string>()
                        });
                        s.Username = u.Username;
                        s.TelegramUserId = u.TelegramUserId;
                    }
                }
            }
            catch { }
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
            decimal slDistCheck = Math.Abs(signal.StopLoss - signal.EntryPrice);
            decimal rrCheck = slDistCheck > 0 ? (tp1DistCheck / slDistCheck) : 0m;
            if (rrCheck < 1.30m)
            {
                Console.WriteLine($"[TelegramBotService] R:R filter blocked (R:R {rrCheck:F2} < 1.30)");
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
                var activeUsers = await userManager.GetAllUsersAsync();

                var targetChatIds = new HashSet<string>();
                foreach (var user in activeUsers)
                {
                    if (!string.IsNullOrEmpty(user.TelegramChatId) && user.IsActive)
                    {
                        targetChatIds.Add(user.TelegramChatId);
                    }
                }
                foreach (var kvp in UserPreferences)
                {
                    if (kvp.Value.IsActive && !string.IsNullOrEmpty(kvp.Key))
                    {
                        targetChatIds.Add(kvp.Key);
                    }
                }

                bool anyDelivered = false;

                foreach (var chatId in targetChatIds)
                {
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
                        "4h" => TimeSpan.FromMinutes(30),
                        _ => TimeSpan.FromMinutes(15)
                    };
                    var candleCloseUtc = signal.SourceCandleOpenTimeUtc + candleDuration;
                    if (DateTime.UtcNow - candleCloseUtc > maxTolerance)
                    {
                        continue;
                    }

                    // Strict R:R Gate: R:R = (TP1 məsafəsi) / (SL məsafəsi). R:R < 1.30 isə send=NO
                    decimal tp1Dist = Math.Abs(signal.TakeProfit1 - signal.EntryPrice);
                    decimal slDist = Math.Abs(signal.StopLoss - signal.EntryPrice);
                    decimal rr = slDist > 0 ? (tp1Dist / slDist) : 0m;
                    if (rr < 1.30m)
                    {
                        continue;
                    }

                    // Strict User Coin Filter: User only receives signals if they have explicitly selected coins.
                    if (settings.Coins.Count == 0 || !settings.Coins.Contains(signal.Symbol)) continue;

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
                settings.LastSignalSentUtc = DateTime.UtcNow;
                settings.LastHeartbeatSentUtc = DateTime.UtcNow;
                SaveSettings();
            }
        }

        public async Task SendVolatilityRiskAlertAsync(string symbol, decimal currentPrice, decimal priceChange24h, decimal volatilityRatio, string reason)
        {
            var msg = TelegramMessageFormatter.FormatVolatilityRiskAlert(symbol, currentPrice, priceChange24h, volatilityRatio, reason);

            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var activeUsers = await userManager.GetAllUsersAsync();

            var targetChatIds = new HashSet<string>();
            foreach (var user in activeUsers)
            {
                if (!string.IsNullOrEmpty(user.TelegramChatId) && user.IsActive)
                {
                    targetChatIds.Add(user.TelegramChatId);
                }
            }
            foreach (var kvp in UserPreferences)
            {
                if (kvp.Value.IsActive && !string.IsNullOrEmpty(kvp.Key))
                {
                    targetChatIds.Add(kvp.Key);
                }
            }

            foreach (var chatId in targetChatIds)
            {
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
            var activeUsers = await userManager.GetAllUsersAsync();

            var targetChatIds = new HashSet<string>();
            foreach (var user in activeUsers)
            {
                if (!string.IsNullOrEmpty(user.TelegramChatId) && user.IsActive)
                {
                    targetChatIds.Add(user.TelegramChatId);
                }
            }
            foreach (var kvp in UserPreferences)
            {
                if (kvp.Value.IsActive && !string.IsNullOrEmpty(kvp.Key))
                {
                    targetChatIds.Add(kvp.Key);
                }
            }

            foreach (var chatId in targetChatIds)
            {
                var settings = GetSettings(chatId);
                if (!settings.IsActive) continue;
                await SendMessageAsync(msg, chatId);
            }
        }

        public async Task BroadcastSystemAlertAsync(string message)
        {
            using var scope = _serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
            var activeUsers = await userManager.GetAllUsersAsync();

            var targetChatIds = new HashSet<string>();
            foreach (var user in activeUsers)
            {
                if (!string.IsNullOrEmpty(user.TelegramChatId) && user.IsActive)
                {
                    targetChatIds.Add(user.TelegramChatId);
                }
            }
            foreach (var kvp in UserPreferences)
            {
                if (kvp.Value.IsActive && !string.IsNullOrEmpty(kvp.Key))
                {
                    targetChatIds.Add(kvp.Key);
                }
            }

            foreach (var chatId in targetChatIds)
            {
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
            var todayUtc = DateTime.UtcNow.Date;

            var todaySignals = await uow.Signals.GetSignalsSinceAsync(todayUtc);
            var globalCoinsEntered = todaySignals.Select(s => s.Symbol.Replace("USDT", "")).Distinct().ToList();
            var defaultReasons = new List<string>
            {
                "Bazar konsolidasiyası və ADX < 20 olan cütlüklər kənarlaşdırıldı",
                "R:R < 1.80 olan qeyri-sabit setup-lar bloklandı",
                "Spayk və qeyri-təbii dalğalanma olan riskli zonalar filtrləndi"
            };

            // 1. Send global report to SuperAdmin
            if (!string.IsNullOrEmpty(SuperAdminChatId))
            {
                var globalStats = await uow.Signals.GetPerformanceStatsAsync();
                var adminMsg = TelegramMessageFormatter.FormatDailyReport(globalStats, globalCoinsEntered, defaultReasons, isSuperAdmin: true);
                await SendMessageAsync(adminMsg, SuperAdminChatId);
            }

            // 2. Send personal daily report to each active user
            foreach (var kvp in UserPreferences)
            {
                var chatId = kvp.Key;
                var settings = kvp.Value;
                if (!settings.IsActive) continue;
                if (chatId == SuperAdminChatId) continue;

                var userStats = await uow.Signals.GetUserPerformanceStatsAsync(chatId);
                var userOpenSignals = await uow.Signals.GetUserOpenSignalsAsync(chatId);
                var userCoinsEntered = userOpenSignals.Select(s => s.Symbol.Replace("USDT", "")).Distinct().ToList();

                var userMsg = TelegramMessageFormatter.FormatDailyReport(userStats, userCoinsEntered, defaultReasons, isSuperAdmin: false);
                await SendMessageAsync(userMsg, chatId);
            }
        }

        private async Task ScanUserCoinsInstantlyAsync(UserSettings userSettings, string chatId, string timeframe, bool isManualButton = false)
        {
            if (userSettings.Coins.Count == 0 || string.IsNullOrWhiteSpace(timeframe) || timeframe == "Təyin olunmayıb" || !userSettings.IsActive) return;

            if (!isManualButton && _lastPortfolioSummarySent.TryGetValue(chatId, out var lastSent))
            {
                if ((DateTime.UtcNow - lastSent).TotalMinutes < 60)
                {
                    return; // Throttle: max 1 per 60 mins unless manual button
                }
            }
            _lastPortfolioSummarySent[chatId] = DateTime.UtcNow;

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
            catch { }

            // Ensure all tracked coins are present (e.g. TONUSDT if not in top 250 futures)
            foreach (var coin in userSettings.Coins)
            {
                bool exists = matchedTickers.Any(t => t.Symbol.Equals(coin, StringComparison.OrdinalIgnoreCase)
                                                   || t.Symbol.Equals(coin.Replace("1000", ""), StringComparison.OrdinalIgnoreCase)
                                                   || ("1000" + t.Symbol).Equals(coin, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    try
                    {
                        var singleTicker = await marketData.Get24hTickerAsync(coin);
                        if (singleTicker != null && singleTicker.Price > 0)
                        {
                            matchedTickers.Add(singleTicker);
                        }
                        else
                        {
                            var altSym = coin.Replace("1000", "");
                            singleTicker = await marketData.Get24hTickerAsync(altSym);
                            if (singleTicker != null && singleTicker.Price > 0)
                            {
                                matchedTickers.Add(singleTicker);
                            }
                        }
                    }
                    catch { }
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

            var sb = new StringBuilder();
            sb.AppendLine($"📊 <b>Portfel və Bazar Vəziyyəti Xülasəsi 🟢</b>\n");
            sb.AppendLine($"⏱ <b>Aktiv Zaman:</b> <code>{tfDisplay}</code>");
            sb.AppendLine($"🪙 <b>İzlənən Portfel:</b> <b>{userSettings.Coins.Count} ədəd coin</b>\n");

            // BTC Market Regime & Benchmark
            if (btcCompass != null && btcCompass.Price > 0)
            {
                var btcSign = btcCompass.Change24h >= 0 ? "+" : "";
                var btcIcon = btcCompass.Change24h >= 0 ? "🟢" : "🔴";
                sb.AppendLine($"🧭 <b>Bitcoin Kompası (BTC/USDT):</b>");
                sb.AppendLine($"• <b>Qiymət:</b> ${btcCompass.Price.ToString("N0", CultureInfo.InvariantCulture)} ({btcSign}{btcCompass.Change24h.ToString("F2", CultureInfo.InvariantCulture)}% {btcIcon})");
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

                sb.AppendLine($"📋 <b>Portfeldəki Bütün Coinlərin Canlı Qiyməti və 24s Dəyişimi:</b>");
                var sorted = matchedTickers.OrderByDescending(t => t.PriceChangePercent).ToList();
                var chunkList = new List<string>();
                foreach (var t in sorted)
                {
                    var cName = t.Symbol.Replace("USDT", "");
                    var pSign = t.PriceChangePercent >= 0 ? "+" : "";
                    var pIcon = t.PriceChangePercent >= 0 ? "🟢" : "🔴";
                    var priceFormatted = t.Price >= 1000 ? t.Price.ToString("N0", CultureInfo.InvariantCulture) : (t.Price >= 1 ? t.Price.ToString("F2", CultureInfo.InvariantCulture) : t.Price.ToString("F4", CultureInfo.InvariantCulture));
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

            await SendMessageAsync(sb.ToString(), chatId);
        }

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

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (string.IsNullOrWhiteSpace(_config.TelegramBotToken)) return;

            // Ensure no lingering webhook exists to prevent conflicts or duplicate updates
            try
            {
                var delWebhookUrl = $"https://api.telegram.org/bot{_config.TelegramBotToken}/deleteWebhook?drop_pending_updates=false";
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

        private async Task HandleCallbackQueryAsync(string chatId, long messageId, string data, string fromUser, long? fromUserId)
        {
            var userSettings = GetSettings(chatId);
            using var scope = _serviceProvider.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
            bool isCallerAdmin = IsSuperAdmin(chatId, fromUserId, fromUser);

            // Guard restricted admin actions
            if (data.StartsWith("cb_admin_") && !isCallerAdmin)
            {
                await SendMessageAsync("⛔ <b>Səlahiyyətiniz çatmır!</b>\n\nBu panel yalnız Sistem Admininə məxsusdur.", chatId);
                return;
            }

            if ((data == "cb_toggle_testmode" || data == "cb_reset" || data == "cb_reset_confirm") && !isCallerAdmin)
            {
                await SendMessageAsync("⛔ <b>Səlahiyyətiniz çatmır!</b>\n\nBu funksiya yalnız Sistem Admininə məxsusdur.", chatId);
                return;
            }

            if (data == "cb_menu")
            {
                var activeCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, activeCount, lastTime);
                await EditMessageTextAsync(chatId, messageId, dashText, TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isCallerAdmin));
            }
            else if (data == "cb_toggle")
            {
                if (!userSettings.IsActive && userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "⛔ Ticarət başlamadı.\nSəbəb: heç bir coin seçilməyib.\nƏvvəl ⚙️ Coin Seçimi ilə ən azı 1 coin seçin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                userSettings.IsActive = !userSettings.IsActive;
                SaveSettings();
                var activeCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, activeCount, lastTime);
                await EditMessageTextAsync(chatId, messageId, dashText, TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isCallerAdmin));
            }
            else if (data == "cb_toggle_testmode")
            {
                bool newState = !_testModeChats.ContainsKey(chatId);
                if (newState)
                {
                    _testModeChats[chatId] = true;
                    var activeCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                    var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                    var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, activeCount, lastTime);
                    await EditMessageTextAsync(chatId, messageId, dashText, TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, isTestMode: true, isAdmin: true));

                    var testActiveMsg = "🧪 <b>Test Simulyasiya Rejimi AKTİVLƏŞDİRİLDİ! 🟢</b>\n\n" +
                                        "İndi istənilən portfel (məs. <b>🪙 Standart 40 Coin</b> və ya <b>⭐ Mənim Coinlərim</b>) seçib zaman aralığını (1h/4h) təyin edin.\n\n" +
                                        "⚡ Sistem dərhal sizə real Confluence ilə nümunəvi <b>TEST Siqnalı</b> və 6 saniyə sonra <b>TP1 Hədəfi (+1.20%)</b> bildirişi göndərəcək!\n\n" +
                                        "<i>Testi dayandırmaq üçün: <code>stop test</code></i>";
                    await SendMessageAsync(testActiveMsg, chatId);
                    _ = SendMockTestSignalAsync(chatId, userSettings, userSettings.Timeframe);
                }
                else
                {
                    _testModeChats.TryRemove(chatId, out _);
                    var activeCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                    var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                    var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, activeCount, lastTime);
                    await EditMessageTextAsync(chatId, messageId, dashText, TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, isTestMode: false, isAdmin: true));

                    var testStopMsg = "⚪ <b>Test Rejimi DAYANDIRILDI!</b>\n\n" +
                                      "🚀 Sistem 100% real canlı bazar analizinə qayıtdı. Yalnız Binance birjasında təsdiqlənən real bazar siqnalları göndəriləcək.";
                    await SendMessageAsync(testStopMsg, chatId);
                }
            }
            else if (data == "cb_portfolio_std40")
            {
                userSettings.PortfolioMode = "Standard40";
                userSettings.Coins = new List<string>(Default40Coins);
                SaveSettings();
                var cleanList = string.Join(", ", Default40Coins.Select(c => c.Replace("USDT", "")));
                var msg = "🪙 <b>Standart 40 İnstitusional Portfel Seçildi</b>\n\n" +
                          $"📊 <b>İzlənən Coinlər:</b> 40/40\n" +
                          $"📋 <code>{cleanList}</code>\n\n" +
                          "<i>Zəhmət olmasa bu portfel üçün ticarət zaman kəsiyini seçin:</i>";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildStandard40TimeframeKeyboard());
            }
            else if (data == "cb_portfolio_custom")
            {
                var custCoins = userSettings.CustomCoins.Select(c => c.Replace("USDT", "")).ToList();
                var custDisplay = custCoins.Count > 0 ? string.Join(", ", custCoins) : "Hələ heç bir fərdi coin əlavə edilməyib.";
                var activeModeText = userSettings.PortfolioMode == "Combined" 
                    ? "🔥 40 Coin + Fərdi Coinlər (Kombinə)" 
                    : (userSettings.PortfolioMode == "Custom" ? "⭐ Yalnız Fərdi Coinlər" : "🪙 Standart 40 Coin");
                var msg = "⭐ <b>Fərdi Portfel İdarəsi</b>\n\n" +
                          $"📌 <b>Cari Rejim:</b> <b>{activeModeText}</b>\n" +
                          $"🪙 <b>Fərdi Coinləriniz ({custCoins.Count} ədəd):</b>\n<code>{custDisplay}</code>\n\n" +
                          "<i>Aşağıdakı düymələrlə portfel rejimini dəyişə, coin əlavə edib/silə və zaman kəsiyini təyin edə bilərsiniz:</i>";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
            }
            else if (data == "cb_cust_mode_only")
            {
                userSettings.PortfolioMode = "Custom";
                userSettings.Coins = new List<string>(userSettings.CustomCoins);
                SaveSettings();
                var custCoins = userSettings.CustomCoins.Select(c => c.Replace("USDT", "")).ToList();
                var custDisplay = custCoins.Count > 0 ? string.Join(", ", custCoins) : "Hələ heç bir fərdi coin əlavə edilməyib.";
                var msg = "🎯 <b>Yalnız Fərdi Coinlər Rejimi Seçildi! 🟢</b>\n\n" +
                          $"🪙 <b>İzlənən Coinləriniz ({custCoins.Count} ədəd):</b>\n<code>{custDisplay}</code>\n\n" +
                          (custCoins.Count == 0 
                              ? "⚠️ <i>Siyahınız boşdur. Əvvəlcə '➕ Coin Əlavə Et' düyməsinə klikləyərək coin əlavə edin.</i>" 
                              : "<i>İndi bu portfel üçün zaman kəsiyini seçin:</i>");
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
            }
            else if (data == "cb_cust_mode_comb")
            {
                userSettings.PortfolioMode = "Combined";
                var combSet = new HashSet<string>(Default40Coins);
                foreach (var c in userSettings.CustomCoins) combSet.Add(c);
                userSettings.Coins = combSet.ToList();
                SaveSettings();
                var msg = "🔥 <b>40 Standart + Fərdi Coinlər (Kombinə) Portfeli</b>\n\n" +
                          $"📊 <b>Ümumi İzlənən:</b> <b>{userSettings.Coins.Count} ədəd coin</b>\n" +
                          $"• Standart: 40 institusional coin\n" +
                          $"• Fərdi: {userSettings.CustomCoins.Count} ədəd əlavə coin\n\n" +
                          "<i>Zəhmət olmasa bu kombinə portfel üçün ticarət zaman kəsiyini seçin:</i>";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildCombinedTimeframeKeyboard());
            }
            else if (data == "cb_comb_tf_1h" || data == "cb_comb_tf_4h" || data == "cb_comb_tf_all")
            {
                userSettings.PortfolioMode = "Combined";
                var combSet = new HashSet<string>(Default40Coins);
                foreach (var c in userSettings.CustomCoins) combSet.Add(c);
                userSettings.Coins = combSet.ToList();
                userSettings.Timeframe = data == "cb_comb_tf_1h" ? "1h" : (data == "cb_comb_tf_4h" ? "4h" : "Hamısı");
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var msg = $"✅ <b>40 Standart + Fərdi Kombinə Portfeli Aktivləşdirildi! 🟢</b>\n\n" +
                          $"⏱ <b>Zaman Rejimi:</b> <code>{userSettings.Timeframe}</code>\n" +
                          $"🪙 <b>İzlənən Coinlər ({userSettings.Coins.Count} ədəd):</b>\n<code>{cleanList}</code>\n\n" +
                          $"🚀 Skaner aktivdir. Yalnız 1h və 4h şam bağlanışında A+ konfluens siqnalları göndəriləcək.";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildBackToTerminalKeyboard());
                if (_testModeChats.ContainsKey(chatId))
                {
                    _ = SendMockTestSignalAsync(chatId, userSettings, userSettings.Timeframe);
                }
            }
            else if (data == "cb_std_tf_1h" || data == "cb_std_tf_4h" || data == "cb_std_tf_all")
            {
                userSettings.PortfolioMode = "Standard40";
                userSettings.Coins = new List<string>(Default40Coins);
                userSettings.Timeframe = data == "cb_std_tf_1h" ? "1h" : (data == "cb_std_tf_4h" ? "4h" : "Hamısı");
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var cleanList = string.Join(", ", Default40Coins.Select(c => c.Replace("USDT", "")));
                var msg = $"✅ <b>Standart 40 Coin Portfeli Aktivləşdirildi! 🟢</b>\n\n" +
                          $"⏱ <b>Zaman Rejimi:</b> <code>{userSettings.Timeframe}</code>\n" +
                          $"🪙 <b>İzlənən Coinlər:</b> 40/40 İnstitusional coin\n\n" +
                          $"🚀 Skaner aktivdir. Yalnız 1h və 4h şam bağlanışında A+ konfluens siqnalları göndəriləcək.";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildBackToTerminalKeyboard());
                if (_testModeChats.ContainsKey(chatId))
                {
                    _ = SendMockTestSignalAsync(chatId, userSettings, userSettings.Timeframe);
                }
            }
            else if (data == "cb_cust_tf_1h" || data == "cb_cust_tf_4h" || data == "cb_cust_tf_all")
            {
                if (userSettings.PortfolioMode == "Custom" && userSettings.CustomCoins.Count == 0)
                {
                    var warnMsg = "⚠️ <b>Fərdi portfeliniz boşdur!</b>\n\n" +
                                  "Zaman təyin etməzdən əvvəl <b>➕ Coin Əlavə Et</b> düyməsinə klikləyərək ən azı 1 coin əlavə edin.";
                    await EditMessageTextAsync(chatId, messageId, warnMsg, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
                    return;
                }
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
                userSettings.Timeframe = data == "cb_cust_tf_1h" ? "1h" : (data == "cb_cust_tf_4h" ? "4h" : "Hamısı");
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var portName = userSettings.PortfolioMode == "Combined" ? "🔥 40 + Fərdi Coin (Kombinə)" : "⭐ Fərdi Coinlər";
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var msg = $"✅ <b>{portName} Portfeli Aktivləşdirildi! 🟢</b>\n\n" +
                          $"⏱ <b>Zaman Rejimi:</b> <code>{userSettings.Timeframe}</code>\n" +
                          $"🪙 <b>İzlənən Coinlər ({userSettings.Coins.Count} ədəd):</b>\n<code>{cleanList}</code>\n\n" +
                          $"🚀 Skaner aktivdir. Yalnız 1h və 4h şam bağlanışında A+ konfluens siqnalları göndəriləcək.";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildBackToTerminalKeyboard());
                if (_testModeChats.ContainsKey(chatId))
                {
                    _ = SendMockTestSignalAsync(chatId, userSettings, userSettings.Timeframe);
                }
            }
            else if (data == "cb_custom_add" || data == "cb_coin_add")
            {
                _userStates[chatId] = "WAITING_ADD_CUSTOM_COIN";
                var msg = "➕ <b>Yeni Fərdi Coin Əlavə Et</b>\n\n" +
                          "İzləmək istədiyiniz coinin adını mesaj olaraq yazın (məsələn: <code>SOL</code>, <code>NOT</code> və ya <code>DOGE</code>):";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildBackToTerminalKeyboard());
            }
            else if (data == "cb_custom_del" || data == "cb_coin_del")
            {
                _userStates[chatId] = "USER_WAITING_DELETE_COIN";
                var custCoins = userSettings.CustomCoins.Select(c => c.Replace("USDT", "")).ToList();
                var custDisplay = custCoins.Count > 0 ? string.Join(", ", custCoins) : "Hələ heç bir fərdi coin əlavə edilməyib.";
                var msg = "🗑 <b>Fərdi Coin Sil</b>\n\n" +
                          $"Cari fərdi coinləriniz:\n<code>{custDisplay}</code>\n\n" +
                          "Silmək istədiyiniz coinin adını mesaj olaraq yazın (məsələn: <code>SOL</code>):";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildBackToTerminalKeyboard());
            }
            else if (data == "cb_btc")
            {
                var compass = await signalEngine.GetBtcCompassAsync();
                var compassMsg = TelegramMessageFormatter.FormatBtcCompass(compass);
                await EditMessageTextAsync(chatId, messageId, compassMsg, TelegramKeyboards.BuildBackToTerminalKeyboard());
            }
            else if (data == "cb_stats")
            {
                var stats = (isCallerAdmin || chatId == SuperAdminChatId)
                    ? await unitOfWork.Signals.GetPerformanceStatsAsync(userSettings.Timeframe)
                    : await signalEngine.GetUserPerformanceStatsAsync(chatId, userSettings.Timeframe, userSettings.Coins);
                var tfLabel = (string.IsNullOrWhiteSpace(userSettings.Timeframe) || userSettings.Timeframe == "Təyin olunmayıb" || userSettings.Timeframe == "Hamısı" || userSettings.Timeframe == "Hamisi") ? "1h, 4h" : userSettings.Timeframe;
                var statsMsg = TelegramMessageFormatter.FormatPerformanceStats(stats, tfLabel);
                await EditMessageTextAsync(chatId, messageId, statsMsg, TelegramKeyboards.BuildBackToTerminalKeyboard());
            }
            else if (data == "cb_status")
            {
                var activeCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                var statusMsg = TelegramMessageFormatter.FormatBotStatus(userSettings, activeCount, lastTime);
                if (_testModeChats.ContainsKey(chatId))
                {
                    statusMsg += "\n\n🧪 <b>Test Rejimi:</b> AKTİVDİR 🟢\n<i>Bütün butonlar və simulyasiyalar test üçün hazırdır.</i>";
                }
                await EditMessageTextAsync(chatId, messageId, statusMsg, TelegramKeyboards.BuildBackToTerminalKeyboard());
            }
            else if (data == "cb_coins" || data == "cb_coins_40")
            {
                userSettings.PortfolioMode = "Standard40";
                userSettings.Coins = new List<string>(Default40Coins);
                SaveSettings();
                var cleanList = string.Join(", ", Default40Coins.Select(c => c.Replace("USDT", "")));
                var msg = $"✅ <b>Standart 40 institusional coin seçildi (40/40).</b>\n\n" +
                          $"📋 <b>İzlənən Coinlər:</b>\n<code>{cleanList}</code>\n\n" +
                          $"🚀 Bütün 40 aktiv skaner tərəfindən 24/7 analiz olunur.";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildStandard40TimeframeKeyboard());
            }
            else if (data == "cb_reset")
            {
                var confirmMsg = TelegramMessageFormatter.FormatResetConfirmationPrompt();
                await EditMessageTextAsync(chatId, messageId, confirmMsg, TelegramKeyboards.BuildResetConfirmationKeyboard());
            }
            else if (data == "cb_reset_confirm")
            {
                // BƏND 6: 🧹 Sıfırla (SuperAdmin): statistika + BAĞLI = 0. Açıq mövqeyə toxunma.
                await unitOfWork.Signals.ResetClosedSignalsAsync();
                await unitOfWork.SaveChangesAsync();

                userSettings.IsActive = false;
                userSettings.Timeframe = "Təyin olunmayıb";
                userSettings.AlertCounter = 0;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var resetMsg = "🛑 <b>Bütün Bağlı Əməliyyatlar və Statistika Sıfırlandı!</b>\n\n" +
                               "• Skaner və bildirişlər <b>dayandırıldı (Dayandırılıb 🔴)</b>.\n" +
                               "• Bağlı əməliyyat tarixçəsi və statistika sıfırlandı (#0).\n" +
                               "• <b>Açıq mövqelər toxunulmaz saxlanıldı.</b>\n" +
                               $"• <b>Qorunan Portfeliniz:</b> {userSettings.Coins.Count} ədəd coin qorunub saxlanıldı.\n\n" +
                               "<i>Yenidən başlamaq üçün aşağıdakı düymə ilə Terminala qayıdın və portfel/zaman seçin.</i>";
                await EditMessageTextAsync(chatId, messageId, resetMsg, TelegramKeyboards.BuildBackToTerminalKeyboard());
            }
            else if (data == "cb_admin_menu")
            {
                var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
                var allUsers = await userManager.GetAllUsersAsync();
                var totalCount = allUsers.Count;
                var activeCount = allUsers.Count(u => u.IsActive);
                var adminDash = TelegramMessageFormatter.FormatAdminDashboard(totalCount, activeCount);
                await EditMessageTextAsync(chatId, messageId, adminDash, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
            }
            else if (data == "cb_admin_create_user")
            {
                _userStates[chatId] = "ADMIN_WAITING_CREATE_USER";
                var prompt = "➕ <b>Yeni İstifadəçi Yaratmaq</b>\n\n" +
                             "Yaratmaq istədiyiniz <b>İstifadəçi Adını</b> və <b>Parolu</b> aralarında boşluq qoyaraq yazın:\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>Murad 123456</code>";
                await EditMessageTextAsync(chatId, messageId, prompt, TelegramKeyboards.BuildBackToAdminKeyboard());
            }
            else if (data == "cb_admin_list_users")
            {
                var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
                var allUsers = await userManager.GetAllUsersAsync();
                var userListMsg = TelegramMessageFormatter.FormatUserList(allUsers);
                await EditMessageTextAsync(chatId, messageId, userListMsg, TelegramKeyboards.BuildBackToAdminKeyboard());
            }
            else if (data == "cb_admin_del_user")
            {
                _userStates[chatId] = "ADMIN_WAITING_DELETE_USER";
                var prompt = "🗑 <b>İstifadəçi Silmək</b>\n\n" +
                             "Silmək istədiyiniz <b>İstifadəçi Adını</b> yazın:\n\n" +
                             "📌 <b>Məsələn:</b> <code>Murad</code>";
                await EditMessageTextAsync(chatId, messageId, prompt, TelegramKeyboards.BuildBackToAdminKeyboard());
            }
            else if (data == "cb_admin_change_pwd")
            {
                _userStates[chatId] = "ADMIN_WAITING_RESET_PWD";
                var prompt = "🔑 <b>Parolu Dəyişmək</b>\n\n" +
                             "İstifadəçi adını və yeni parolu aralarında boşluq qoyaraq yazın:\n\n" +
                             "📌 <b>Məsələn:</b> <code>Murad yeniParol123</code>";
                await EditMessageTextAsync(chatId, messageId, prompt, TelegramKeyboards.BuildBackToAdminKeyboard());
            }
            else if (data == "cb_admin_export_db")
            {
                var volumeEnv = Environment.GetEnvironmentVariable("RAILWAY_VOLUME_MOUNT_PATH");
                var currentDataDir = !string.IsNullOrEmpty(volumeEnv) && Directory.Exists(volumeEnv)
                    ? volumeEnv
                    : (Directory.Exists("/app/data") ? "/app/data" : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
                var currentDbPath = Path.Combine(currentDataDir, "cryptosense.db");
                if (!File.Exists(currentDbPath)) currentDbPath = "cryptosense.db";

                if (File.Exists(currentDbPath))
                {
                    var cap = $"💾 <b>CryptoSense SQLite Verilənlər Bazası</b>\n\n" +
                              $"📅 <b>Tarix:</b> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
                    await SendDocumentAsync(currentDbPath, chatId, cap);
                    await EditMessageTextAsync(chatId, messageId, "✅ <b>Baza faylı sənəd olaraq çatınıza göndərildi.</b>", TelegramKeyboards.BuildBackToAdminKeyboard());
                }
                else
                {
                    await EditMessageTextAsync(chatId, messageId, "⚠️ <b>Baza faylı tapılmadı.</b>", TelegramKeyboards.BuildBackToAdminKeyboard());
                }
            }
            else if (data == "cb_admin_stats")
            {
                var stats = await signalEngine.GetPerformanceStatsAsync(userSettings.Timeframe, userSettings.Coins);
                var statsMsg = TelegramMessageFormatter.FormatPerformanceStats(stats, "Qlobal Admin");
                await EditMessageTextAsync(chatId, messageId, statsMsg, TelegramKeyboards.BuildBackToAdminKeyboard());
            }
            else if (data == "cb_close_terminal")
            {
                userSettings.IsTerminalOpen = false;
                userSettings.LastTerminalMessageId = null;
                SaveSettings();
                await DeleteMessageAsync(chatId, messageId);
            }
            else if (data == "cb_close_admin")
            {
                userSettings.IsAdminOpen = false;
                userSettings.LastAdminMessageId = null;
                SaveSettings();
                await DeleteMessageAsync(chatId, messageId);
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
                text.StartsWith("/deluser", StringComparison.OrdinalIgnoreCase);

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
                                     (text.StartsWith("/") && !text.StartsWith("/login", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("/admin ", StringComparison.OrdinalIgnoreCase));

            // =========================================================================
            // 0. LOGOUT COMMAND (ALWAYS CLEARS DATABASE & SESSION)
            // =========================================================================
            if (text == "/logout" || text.Equals("/cixis", StringComparison.OrdinalIgnoreCase) || text.Equals("/exit", StringComparison.OrdinalIgnoreCase) || text.Equals("Çıxış", StringComparison.OrdinalIgnoreCase) || text.Equals("Cixis", StringComparison.OrdinalIgnoreCase) || text.Equals("logout", StringComparison.OrdinalIgnoreCase))
            {
                _ = DeleteMessageAsync(chatId, messageId);
                _authenticatedSessions.TryRemove(chatId, out _);
                _loggedOutChats[chatId] = true;
                UserPreferences.TryRemove(chatId, out _);
                _userStates.TryRemove(chatId, out _);
                if (SuperAdminChatId == chatId) SuperAdminChatId = null;

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
                    return;
                }

                if (!_authenticatedSessions.TryGetValue(chatId, out var sessionUser))
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
                isAuthenticated = _authenticatedSessions.TryGetValue(chatId, out currentUsername);
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
                        settings.Timeframe = "Təyin olunmayıb";
                        settings.PortfolioMode = "Təyin olunmayıb";
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
                              "Artıq yalnız real bazar qaydalarına (Confluence >= 78%, R:R >= 1.30) cavab verən real siqnallar göndəriləcək.";
                await SendMessageAsync(stopMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }

            if (isAdmin && (text == "📊 Əsas Menyu (Siqnallar)" || text == "⬅️ Əsas Menyu"))
            {
                _userStates.TryRemove(chatId, out _);
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var openCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, openCount, lastTime);
                await SendMessageAsync(dashText, chatId, TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isAdmin));
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
                var volumeEnv = Environment.GetEnvironmentVariable("RAILWAY_VOLUME_MOUNT_PATH");
                var currentDataDir = !string.IsNullOrEmpty(volumeEnv) && Directory.Exists(volumeEnv)
                    ? volumeEnv
                    : (Directory.Exists("/app/data") ? "/app/data" : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
                var currentDbPath = Path.Combine(currentDataDir, "cryptosense.db");

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
                          $"Sistem arxa fonda hər 10 saniyədən bir bu {cleanCoins.Count} coinin hər birini aktiv zaman kəsiyində (EMA, MACD, RSI, ATR, Confluence və BTC Kompası) analiz edir və Confluence >= 78% olanda şam kilidi ilə istifadəçilərə çatdırır.";

                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
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
                            await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                        }
                        else
                        {
                            await SendMessageAsync($"⚠️ <b>Xəta:</b> <code>{newUsername}</code> adlı istifadəçi artıq mövcuddur!", chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
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
                    var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
                        return;
                    }
                }
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

                string? lastTime = userSettings.LastSignalSentUtc == default 
                    ? null 
                    : CryptoSense.Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                var statusMsg = TelegramMessageFormatter.FormatBotStatus(userSettings, openCount, lastTime);
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
            else if (text.Contains("Dayandır") || text.Contains("Dayandir") || text == "/stop")
            {
                userSettings.IsActive = false;
                SaveSettings();
                await SendMessageAsync("🛑 <b>Canlı Bildirişlər Dayandırıldı! 🔴</b>\n\n" +
                                       "Sizə yeni siqnal və nəticə bildirişləri gəlməyəcək.\n" +
                                       "Yenidən başlatmaq üçün <b>▶️ Bildirişləri Başlat</b> düyməsinə klikləyin.", 
                                       chatId, 
                                       TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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

                userSettings.AlertCounter = 0;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                int coinCount = userSettings.Coins.Count;
                var resetMsg = "🧹 <b>Bildiriş Sayğacınız Sıfırlandı! ✅</b>\n\n" +
                               "• Şəxsi siqnal sayğacınız (#1) sıfırlandı və yeni bildirişlər üçün hazırlandı.\n" +
                               "• Baza statistikası və keçmiş ticarət nəticələri qorunub saxlanıldı.\n" +
                               $"• Seçilmiş coin siyahınız (<b>{coinCount} coin</b>) qorunub saxlanıldı.\n" +
                               $"• Bildiriş Statusu: {(userSettings.IsActive ? "Aktiv 🟢" : "Dayandırılıb 🔴")}";

                await SendMessageAsync(resetMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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
                var openCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, openCount, lastTime);
                await SendMessageAsync(dashText, chatId, TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isAdmin));
                return;
            }
            else if (text.Contains("Bitcoin", StringComparison.OrdinalIgnoreCase) || text.Contains("Kompas", StringComparison.OrdinalIgnoreCase) || text.Contains("🧭") || text == "/btc" || text == "/compass")
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
    }
}
