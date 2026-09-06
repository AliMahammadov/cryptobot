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
        private static readonly string DataDirectory = Directory.Exists("/app/data")
            ? "/app/data"
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string SettingsFilePath = Path.Combine(DataDirectory, "user_preferences.json");
        private static readonly string SignalMapFilePath = Path.Combine(DataDirectory, "signal_user_numbers.json");

        public static readonly List<string> Default16Coins = new()
        {
            "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", 
            "LINKUSDT", "AVAXUSDT", "NEARUSDT", "DOTUSDT", "ADAUSDT", "ATOMUSDT", 
            "ARBUSDT", "OPUSDT", "SUIUSDT", "LTCUSDT"
        };

        public static readonly List<string> OptionalCoins = new()
        {
            "UNIUSDT", "APTUSDT"
        };

        public static readonly HashSet<string> Supported50Coins = new(StringComparer.OrdinalIgnoreCase)
        {
            "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT", "NOTUSDT",
            "TONUSDT", "ADAUSDT", "LINKUSDT", "NEARUSDT", "APTUSDT", "TIAUSDT", "INJUSDT", "OPUSDT", "ARBUSDT", "RENDERUSDT",
            "FETUSDT", "TAOUSDT", "WIFUSDT", "FTMUSDT", "DOTUSDT", "LTCUSDT", "BCHUSDT", "UNIUSDT", "SEIUSDT", "JUPUSDT",
            "WLDUSDT", "SHIBUSDT", "BONKUSDT", "FLOKIUSDT", "ATOMUSDT", "XLMUSDT", "FILUSDT", "ETCUSDT", "ALGOUSDT", "ICPUSDT",
            "STXUSDT", "PYTHUSDT", "GALAUSDT", "SANDUSDT", "MANAUSDT", "AAVEUSDT", "CRVUSDT", "DYDXUSDT", "MKRUSDT", "PENDLEUSDT"
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
                            IsActive = true,
                            Timeframe = "1h"
                        });
                        s.Username = u.Username;
                        s.TelegramUserId = u.TelegramUserId;
                    }
                }
            }
            catch { }
        }

        public static UserSettings GetSettings(string chatId)
        {
            var settings = UserPreferences.GetOrAdd(chatId, _ => new UserSettings());
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
            using var scope = _serviceProvider.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            if (!string.IsNullOrEmpty(specificChatId))
            {
                var settings = GetSettings(specificChatId);
                var userSigNum = signal.SignalNumber > 0 ? signal.SignalNumber : signal.UserSignalNumbers.GetOrAdd(specificChatId, _ => ++settings.AlertCounter);
                settings.AlertCounter = Math.Max(settings.AlertCounter, userSigNum);
                _signalUserNumberMap[$"{signal.Id}_{specificChatId}"] = userSigNum;
                SaveSettings();
                var msg = TelegramMessageFormatter.FormatSignalAlert(signal, userSigNum);
                settings.LastSignalSentUtc = DateTime.UtcNow;
                settings.LastHeartbeatSentUtc = DateTime.UtcNow;
                bool sent = await SendMessageAsync(msg, specificChatId);
                if (sent)
                {
                    await uow.Signals.RecordDeliveryAsync(signal.Id, specificChatId, userSigNum);
                    signal.SignalAlertSent = true;
                    try
                    {
                        var dbSig = await uow.Signals.GetByIdAsync(signal.Id);
                        if (dbSig != null)
                        {
                            dbSig.SignalAlertSent = true;
                            await uow.Signals.UpdateAsync(dbSig);
                            await uow.SaveChangesAsync();
                        }
                    }
                    catch { }
                }
                return;
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

                // Strict Timeframe check: Only 15m, 1h, 4h
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
                    "15m" => TimeSpan.FromMinutes(15),
                    "1h" => TimeSpan.FromHours(1),
                    "4h" => TimeSpan.FromHours(4),
                    _ => TimeSpan.FromMinutes(15)
                };
                var maxTolerance = signal.Timeframe switch
                {
                    "15m" => TimeSpan.FromMinutes(8),
                    "1h" => TimeSpan.FromMinutes(15),
                    "4h" => TimeSpan.FromMinutes(30),
                    _ => TimeSpan.FromMinutes(10)
                };
                var candleCloseUtc = signal.SourceCandleOpenTimeUtc + candleDuration;
                if (DateTime.UtcNow - candleCloseUtc > maxTolerance)
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

                // Limit checks: Max 10 signals per day, Max 5 open positions
                var todayCount = await uow.Signals.GetUserTodaySignalsCountAsync(chatId);
                if (todayCount >= 10) continue;

                var openCount = await uow.Signals.GetUserOpenSignalsCountAsync(chatId);
                if (openCount >= 5) continue;

                var userSigNum = signal.SignalNumber > 0 ? signal.SignalNumber : signal.UserSignalNumbers.GetOrAdd(chatId, _ => ++settings.AlertCounter);
                settings.AlertCounter = Math.Max(settings.AlertCounter, userSigNum);
                _signalUserNumberMap[$"{signal.Id}_{chatId}"] = userSigNum;
                SaveSettings();
                var msg = TelegramMessageFormatter.FormatSignalAlert(signal, userSigNum);
                settings.LastSignalSentUtc = DateTime.UtcNow;
                settings.LastHeartbeatSentUtc = DateTime.UtcNow;
                bool sent = await SendMessageAsync(msg, chatId);
                if (sent)
                {
                    await uow.Signals.RecordDeliveryAsync(signal.Id, chatId, userSigNum);
                    anyDelivered = true;
                }
            }

            if (anyDelivered)
            {
                signal.SignalAlertSent = true;
                try
                {
                    var dbSig = await uow.Signals.GetByIdAsync(signal.Id);
                    if (dbSig != null)
                    {
                        dbSig.SignalAlertSent = true;
                        await uow.Signals.UpdateAsync(dbSig);
                        await uow.SaveChangesAsync();
                    }
                }
                catch { }
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

        private async Task ScanUserCoinsInstantlyAsync(UserSettings userSettings, string chatId, string timeframe)
        {
            if (userSettings.Coins.Count == 0) return;
            using var scope = _serviceProvider.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var tfDisplay = (timeframe == "Hamısı" || timeframe == "Hamisi") ? "Bütün Əsas Zamanlar (15m, 1h, 4h)" : timeframe;
            var userOpenSignals = await uow.Signals.GetUserOpenSignalsAsync(chatId);
            if (timeframe != "Hamısı" && timeframe != "Hamisi")
            {
                userOpenSignals = userOpenSignals.Where(s => s.Timeframe == timeframe).ToList();
            }

            var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
            var sb = new StringBuilder();
            sb.AppendLine($"⚡ <b>Seçilmiş Coinlər Üzrə Canlı İzləmə Aktivdir! 🟢</b>\n");
            sb.AppendLine($"⏱ <b>Aktiv Rejim:</b> <code>{tfDisplay}</code>");
            sb.AppendLine($"🪙 <b>Coinləriniz ({userSettings.Coins.Count} ədəd):</b> <code>{cleanList}</code>\n");

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
                sb.AppendLine($"ℹ️ <i>Seçilmiş coinləriniz üzrə hazırda açıq əməliyyat yoxdur.</i>\n");
            }
            sb.AppendLine($"🟢 <b>Sistem canlı izləmədədir.</b> Seçdiyiniz coinlərdə yeni şam bağlandıqca 75%+ siqnallar real vaxtda avtomatik çatınıza göndəriləcək.");
            await SendMessageAsync(sb.ToString(), chatId);
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
                        }
                    }
                }
                catch
                {
                }

                await Task.Delay(100, stoppingToken);
            }
        }

        private async Task HandleIncomingMessageAsync(string chatId, string telegramUsername, long? userId, long messageId, string text)
        {
            // Action debouncer: ignore rapid double-taps/clicks of the exact same action within 1.5 seconds
            var nowUtc = DateTime.UtcNow;
            if (_lastUserAction.TryGetValue(chatId, out var lastAct))
            {
                if (lastAct.Text == text && (nowUtc - lastAct.Time).TotalMilliseconds < 1500)
                {
                    return;
                }
            }
            _lastUserAction[chatId] = (text, nowUtc);

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

            // 👑 AUTO-RECOGNIZE SUPER ADMIN ALI (NEVER PROMPT FOR LOGIN AGAIN)
            if (currentUser == null && (userId == 1219998176 || (!string.IsNullOrEmpty(telegramUsername) && telegramUsername.Equals("Ali_Mahammadov", StringComparison.OrdinalIgnoreCase))))
            {
                var (validAdmin, adminAcc) = await userManager.ValidateLoginAsync("Ali", "23031999Am", userId, chatId);
                if (validAdmin && adminAcc != null)
                {
                    currentUser = adminAcc;
                }
            }

            // If user typed explicit login credentials or 2-word login (e.g. "dudu 123" or "Ali 23031999Am"):
            var loginParts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool isExplicitLoginCommand = text.StartsWith("/login", StringComparison.OrdinalIgnoreCase) ||
                                          text.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) ||
                                          text.Contains("23031999Am") ||
                                          (!text.StartsWith("/") && loginParts.Length == 2 && loginParts[0].Length >= 2 && loginParts[1].Length >= 2);

            bool isMenuButtonClick = text.StartsWith("🧭") || text.StartsWith("⚡") || text.StartsWith("⭐") || 
                                     text.StartsWith("📊") || text.StartsWith("📈") || text.StartsWith("⚙️") || 
                                     text.StartsWith("🗑") || text.StartsWith("⏱") || text.StartsWith("🌟") || 
                                     text.StartsWith("🧹") || text.StartsWith("🛑") || text.StartsWith("▶️") || 
                                     text.StartsWith("📰") || text.StartsWith("⬅️") || text.StartsWith("👥") || 
                                     text.StartsWith("🔑") || text.StartsWith("👑") || text.StartsWith("📋") || 
                                     text.StartsWith("ℹ️") || text == "➕ Öz coini əlavə et" || text == "➕ İstifadəçi Yarat" ||
                                     text.Contains("Siqnallar") || text.Contains("Menyu") || text.Contains("Statistika") ||
                                     text.StartsWith("/");

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
                    currentUser = user;
                    _userStates.TryRemove(chatId, out _);
                    var settings = GetSettings(chatId);
                    settings.TelegramUserId = userId;
                    settings.IsActive = true;
                    settings.Timeframe = "1h";
                    settings.LastResumeTime = DateTime.UtcNow;

                    if (user.Role == UserRole.Admin || user.Username.Equals("Ali", StringComparison.OrdinalIgnoreCase))
                    {
                        SuperAdminChatId = chatId;
                        settings.Username = "Ali (Super Admin)";

                        var welcomeAdmin = $"👑 <b>Giriş Təsdiqləndi! Xoş Gəldiniz, Super Admin ({user.Username})!</b>\n\n" +
                                           $"🚀 <b>Kripto Signals Bot Xidməti AKTİVDİR 🟢</b>\n\n" +
                                           $"📖 <b>Sistemdən Necə İstifadə Etməli?</b>\n" +
                                           $"• <b>⭐ Mənim Coinlərim:</b> Yalnız seçdiyiniz coinlər üzrə istədiyiniz zaman kəsiyində ticarət aparın.\n" +
                                           $"• <b>⚙️ Coin Seçimi:</b> Standart 16 coini seçin, yeni coin əlavə edin və ya siyahını tənzimləyin.\n" +
                                           $"• <b>🗑 Coin Sil:</b> İzləmək istəmədiyiniz coinləri siyahıdan çıxarın.\n" +
                                           $"• <b>🧭 Bitcoin Kompası:</b> Bazarın ümumi trendini və qüvvəsini izləyin.\n" +
                                           $"• <b>📊 Statistika:</b> Şəxsi əməliyyat performansınızı görün.\n" +
                                           $"• <b>📈 Dərin Statistika:</b> Seçilmiş coinlərin şamlar üzrə dərin win-rate və əməliyyat nəticələrinə baxın.\n" +
                                           $"• <b>🛑 Dayandır / 🧹 Sıfırla:</b> Bildirişləri dayandırın və ya bütün tarixi sıfırlayın.\n" +
                                           $"• <b>👑 Admin Paneli:</b> İstifadəçi idarəetməsi.\n\n" +
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
                                            $"• <b>⭐ Mənim Coinlərim:</b> Yalnız seçdiyiniz coinlər üzrə istədiyiniz zaman kəsiyində ticarət aparın.\n" +
                                            $"• <b>⚙️ Coin Seçimi:</b> Standart 16 coini seçin, yeni coin əlavə edin və ya siyahını tənzimləyin.\n" +
                                            $"• <b>🗑 Coin Sil:</b> İzləmək istəmədiyiniz coinləri siyahıdan çıxarın.\n" +
                                            $"• <b>🧭 Bitcoin Kompası:</b> Bazarın ümumi trendini və qüvvəsini izləyin.\n" +
                                            $"• <b>📊 Statistika:</b> Şəxsi əməliyyat performansınızı görün.\n" +
                                            $"• <b>🛑 Dayandır / 🧹 Sıfırla:</b> Bildirişləri dayandırın və ya bütün tarixi sıfırlayın.\n\n" +
                                            $"<i>Sistem 24/7 rejimdə canlı bazar qiymətlərini analiz edir və yüksək dəqiqlikli fürsətləri sizə göndərir.</i>\n\n" +
                                            $"<i>Çıxış etmək üçün: <code>/logout</code></i>";
                        
                        await SendMessageAsync(onboardingMsg, chatId, TelegramKeyboards.BuildUserKeyboard(settings, isAdmin: false));
                        await NotifySuperAdminUserLoginAsync(user.Username, $"Telegram (@{telegramUsername})");
                        SaveSettings();
                        return;
                    }
                }
                else
                {
                    if (currentUser == null || text.StartsWith("/login", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
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

            if (isAdmin && (text == "🌐 Bütün Coinlərin Siyahısı" || text == "/all_coins"))
            {
                _userStates.TryRemove(chatId, out _);
                var monitored = Default16Coins;

                var cleanCoins = monitored.Select(c => c.Replace("USDT", "")).Distinct().ToList();
                var msg = "🌐 <b>Sistemin Canlı İzlədiyi Bütün Coinlər və Zamanlar</b>\n\n" +
                          $"📊 <b>Ümumi Coin Sayı:</b> <b>{cleanCoins.Count} ədəd (Standart İnstitusional 16)</b>\n" +
                          $"🪙 <b>İzlənən Coinlər:</b>\n<code>{string.Join(", ", cleanCoins)}</code>\n\n" +
                          "⏱ <b>Dövri Olaraq Analiz Olunan Şamlar:</b>\n" +
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

                    if (userSettings.Coins.Contains(coinToDel))
                    {
                        userSettings.Coins.Remove(coinToDel);
                        removedCoins.Add(clean);
                    }
                    else
                    {
                        notFoundCoins.Add(clean);
                    }
                }

                userSettings.Coins.RemoveAll(c => !System.Text.RegularExpressions.Regex.IsMatch(c, @"^[A-Z0-9]+USDT$") || c.Contains("⚡") || c.Contains("SIQNALLAR") || c.Contains("BÜTÜN") || c.Contains("BUTUN"));

                if (removedCoins.Count > 0)
                {
                    userSettings.LastResumeTime = DateTime.UtcNow;
                    SaveSettings();
                }

                // Auto-stop trading if last coin is deleted
                if (userSettings.Coins.Count == 0)
                {
                    userSettings.IsActive = false;
                    SaveSettings();
                    var sbWarn = new StringBuilder();
                    if (removedCoins.Count > 0)
                    {
                        sbWarn.AppendLine($"✅ <b>Silinən coinlər:</b> <code>{string.Join(", ", removedCoins)}</code>\n");
                    }
                    sbWarn.AppendLine("⚠️ <b>Bütün coinlər siyahıdan silindi (0 coin qaldı).</b>");
                    sbWarn.AppendLine("🛑 <b>Ticarət və canlı bildirişlər avtomatik DAYANDIRILDI 🔴.</b>\n");
                    sbWarn.AppendLine("📌 <i>Yenidən başlamaq üçün <b>⚙️ Coin Seçimi</b> ilə ən azı 1 coin seçin.</i>");
                    await SendMessageAsync(sbWarn.ToString(), chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }

                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var sb = new StringBuilder();
                if (removedCoins.Count > 0)
                {
                    sb.AppendLine($"✅ <b>Silinən coinlər ({removedCoins.Count} ədəd):</b> <code>{string.Join(", ", removedCoins)}</code>");
                }
                if (notFoundCoins.Count > 0)
                {
                    sb.AppendLine($"⚠️ <b>Siyahıda tapılmayanlar:</b> <code>{string.Join(", ", notFoundCoins)}</code>");
                }
                sb.AppendLine();
                sb.AppendLine($"Cari siyahınız ({userSettings.Coins.Count} coin):\n<code>{cleanList}</code>");

                await SendMessageAsync(sb.ToString(), chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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
                        TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }

                var targetSymbol = normalized + "USDT";

                if (userSettings.Coins.Contains(targetSymbol))
                {
                    await SendMessageAsync(
                        $"ℹ️ <b>{normalized} artıq izləmə siyahınızda mövcuddur.</b>\nİndi izlənilən: {userSettings.Coins.Count} coin.",
                        chatId,
                        TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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
                        TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                    return;
                }

                userSettings.Coins.Add(targetSymbol);
                SaveSettings();

                await SendMessageAsync(
                    $"✅ {normalized} əlavə olundu. İndi izlənilən: {userSettings.Coins.Count} coin.",
                    chatId,
                    TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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
                await ScanUserCoinsInstantlyAsync(userSettings, chatId, targetTf);
                return;
            }

            // DIRECT TIMEFRAME PREFERENCE SELECTION
            if (text == "🌟 Bütün Əsas Zamanlar (15m, 1h, 4h)" || 
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
                               $"⏱ <b>Rejim:</b> <code>15m, 1h, 4h</code>\n" +
                               $"🪙 <b>İzlənən:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                await ScanUserCoinsInstantlyAsync(userSettings, chatId, "Hamısı");
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
                userSettings.Timeframe = "15m";
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var startMsg = $"✅ <b>Ticarət başladı</b>\n" +
                               $"⏱ <b>Rejim:</b> <code>15m</code>\n" +
                               $"🪙 <b>İzlənən:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                await ScanUserCoinsInstantlyAsync(userSettings, chatId, "15m");
                return;
            }
            else if (text == "⏱ 1 Saat (1h)" || text == "1h")
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
                await ScanUserCoinsInstantlyAsync(userSettings, chatId, "1h");
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
                await ScanUserCoinsInstantlyAsync(userSettings, chatId, "4h");
                return;
            }
            else if (text == "📋 Standart 16 Coini Seç")
            {
                userSettings.Coins = new List<string>(Default16Coins);
                SaveSettings();
                var cleanList = string.Join(", ", Default16Coins.Select(c => c.Replace("USDT", "")));
                var msg = $"✅ <b>Standart 16 institusional coin seçildi (16/16).</b>\n\n" +
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
                    using var sc = _serviceProvider.CreateScope();
                    var uow = sc.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    openCount = await uow.Signals.GetUserOpenSignalsCountAsync(chatId);
                }
                catch { }

                string? lastTime = userSettings.LastSignalSentUtc == default 
                    ? null 
                    : CryptoSense.Domain.Common.TimeHelper.ToAzerbaijanTime(userSettings.LastSignalSentUtc).ToString("dd.MM.yyyy HH:mm:ss");
                var statusMsg = TelegramMessageFormatter.FormatBotStatus(userSettings, openCount, lastTime);
                await SendMessageAsync(statusMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text.Contains("Geri") || text.Contains("Əsas Menyu") || text == "/menu")
            {
                await SendMessageAsync("📊 <b>Əsas Menyu:</b>", chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }

            if (text == "/start" || text == "/help" || text.Contains("Menyu"))
            {
                var coinSummary = userSettings.Coins.Count > 0 
                    ? $"{userSettings.Coins.Count} coin ({string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")))})"
                    : "Heç bir coin seçilməyib (0 coin)";

                var tfDisplay = (userSettings.Timeframe == "Hamısı" || userSettings.Timeframe == "Hamisi") ? "15m, 1h, 4h" : userSettings.Timeframe;

                var greeting = $"👋 <b>Xoş gəldiniz, {telegramUsername}!</b>\n\n" +
                               $"🚀 <b>CryptoSense v2 — Peşəkar Fyuçers Siqnal və Bazar İntellekti Botu</b>\n" +
                               $"Sistem seçilmiş kripto aktivləri riyazi/texniki indiqatorlar və BTC dominantlığı ilə 24/7 rejimində analiz edir.\n\n" +
                               $"👤 <b>İstifadəçi Hesabınız:</b> <code>{(string.IsNullOrEmpty(userSettings.Username) ? telegramUsername : userSettings.Username)}</code>\n" +
                               $"👑 <b>Rolunuz:</b> <code>{(isAdmin ? "SuperAdmin 🌟" : "Standart İstifadəçi 👤")}</code>\n" +
                               $"🪙 <b>Aktiv Coinlər:</b> <code>{coinSummary}</code>\n" +
                               $"⏱ <b>Aktiv Rejim:</b> <code>{tfDisplay}</code>\n" +
                               $"🔔 <b>Canlı Siqnallar:</b> <b>{(userSettings.IsActive ? "AKTİV 🟢" : "DAYANDIRILIB 🔴")}</b>\n\n" +
                               $"📌 <b>Əsas Funksiyalar:</b>\n" +
                               $"• <b>⭐ Mənim Coinlərim:</b> Yalnız seçdiyiniz coinləri izləyin və ticarətə başlayın.\n" +
                               $"• <b>⚙️ Coin Seçimi:</b> Standart 16 coini seçin, yeni coin əlavə edin və ya siyahını tənzimləyin.\n" +
                               $"• <b>🗑 Coin Sil:</b> İzləmək istəmədiyiniz coinləri siyahıdan çıxarın.\n" +
                               $"• <b>🧭 Bitcoin Kompası:</b> Canlı BTC trendi, RSI, EMA və Dominans (BTC.D) təhlili.\n" +
                               $"• <b>📊 Statistika:</b> Şəxsi əməliyyat performansınızı görün.\n" +
                               $"• <b>ℹ️ Bot Statusu:</b> Skanerin və bildirişlərinizin canlı vəziyyətinə baxın.\n" +
                               $"• <b>🧹 Siqnalları Sıfırla:</b> Şəxsi sayğacınızı və tarixçənizi təmizləyin.\n\n" +
                               $"💡 <i>Aşağıdakı menyu düymələrindən istifadə edərək sistemi idarə edə bilərsiniz:</i>";

                await SendMessageAsync(greeting, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
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

                var tfDisplay = (userSettings.Timeframe == "Hamısı" || userSettings.Timeframe == "Hamisi") ? "15m, 1h, 4h" : userSettings.Timeframe;
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));

                var startMsg = $"✅ <b>Ticarət başladı</b>\n" +
                               $"⏱ <b>Rejim:</b> <code>{tfDisplay}</code>\n" +
                               $"🪙 <b>İzlənən:</b> {userSettings.Coins.Count} coin\n" +
                               $"<code>{cleanList}</code>";
                await SendMessageAsync(startMsg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
            }
            else if (text.Contains("Sıfırla") || text.Contains("Sifirla") || text == "/clear" || text == "/reset")
            {
                userSettings.AlertCounter = 0;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                await signalEngine.ClearUserHistoryAsync(chatId);

                if (isAdmin)
                {
                    try
                    {
                        using var sc = _serviceProvider.CreateScope();
                        var se = sc.ServiceProvider.GetRequiredService<ISignalEngine>();
                        await se.ClearAllSignalsAsync();
                    }
                    catch { }

                    await SendMessageAsync(
                        "🧹 <b>Qlobal Siqnal Bazası və Şəxsi Sayğacınız Sıfırlandı! ✅ (Admin)</b>\n\n" +
                        "• Bazadakı bütün keçmiş siqnal qeydləri təmizləndi.\n" +
                        "• Qlobal statistik göstəricilər sıfırlandı.\n" +
                        $"• Seçilmiş coin siyahınız ({userSettings.Coins.Count} coin) qorunub saxlanıldı.\n" +
                        "• Digər istifadəçilərin bildiriş statusu dəyişdirilmədi.\n\n" +
                        $"📌 Bildiriş Statusu: {(userSettings.IsActive ? "Aktiv 🟢" : "Dayandırılıb 🔴")}", 
                        chatId, 
                        TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                }
                else
                {
                    await SendMessageAsync(
                        "🧹 <b>Şəxsi Bildiriş Sayğacınız və Tarixçəniz Sıfırlandı! ✅</b>\n\n" +
                        "• Şəxsi siqnal sayğacınız (#1) və çatdırılma qeydləriniz sıfırlandı.\n" +
                        $"• Seçilmiş coin siyahınız ({userSettings.Coins.Count} coin) qorunub saxlanıldı.\n" +
                        $"• Bildiriş Statusu: {(userSettings.IsActive ? "Aktiv 🟢" : "Dayandırılıb 🔴")}", 
                        chatId, 
                        TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                }
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
                if (isAdmin)
                {
                    // SuperAdmin: Global system stats across ALL coins
                    var stats = await signalEngine.GetPerformanceStatsAsync(null, null);
                    var msg = TelegramMessageFormatter.FormatPerformanceStats(stats, "Hamısı (Qlobal Sistem)");
                    await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                }
                else
                {
                    // Regular User: ONLY personal stats from delivered signals
                    var stats = await signalEngine.GetUserPerformanceStatsAsync(chatId, userSettings.Timeframe, userSettings.Coins);
                    var tfLabel = (userSettings.Timeframe == "Hamısı" || userSettings.Timeframe == "Hamisi") ? "15m, 1h, 4h" : userSettings.Timeframe;
                    var msg = TelegramMessageFormatter.FormatPerformanceStats(stats, tfLabel);
                    await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                }
            }
            else if (text == "⚡ Bütün Siqnallar" || text == "Bütün Siqnallar" || text == "/scan")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "⛔ Ticarət başlamadı.\nSəbəb: heç bir coin seçilməyib.\nƏvvəl ⚙️ Coin Seçimi ilə ən azı 1 coin seçin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }

                var tfAskMsg = "⚡ <b>Seçilmiş Coinlər Üzrə Siqnal Axtarışı</b>\n\n" +
                               $"🪙 İzlənən: <b>{userSettings.Coins.Count} coin</b>\n" +
                               "Hansı zaman aralığı üzrə 78%+ Confluence siqnalları axtarmaq istəyirsiniz?\n\n" +
                               "Aşağıdakı seçimlərdən birinə vurun:";
                await SendMessageAsync(tfAskMsg, chatId, TelegramKeyboards.BuildAllSignalsTimeframeKeyboard());
                return;
            }
            else if (text.Contains("Coinlərim") || text.Contains("Coinlerim") || text == "/my")
            {
                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "⛔ Ticarət başlamadı.\nSəbəb: heç bir coin seçilməyib.\nƏvvəl ⚙️ Coin Seçimi ilə ən azı 1 coin seçin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }

                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var prompt = "⭐ <b>Mənim Coinlərimlə Ticarət Sistemi</b>\n\n" +
                             $"🪙 <b>Ticarət Üçün Seçilmiş Coinləriniz ({userSettings.Coins.Count} coin):</b>\n" +
                             $"<code>{cleanList}</code>\n\n" +
                             $"Cari rejim: <b>{(userSettings.IsActive ? "AKTİV 🟢" : "DAYANDIRILIB 🔴")}</b> | Cari Zaman: <b>{(userSettings.Timeframe == "Hamısı" ? "15m, 1h, 4h" : userSettings.Timeframe)}</b>\n\n" +
                             "⏱ <b>Hansı zaman çərçivəsində ticarət/siqnal axınına başlamaq istəyirsiniz?</b>\n" +
                             "<i>Aşağıdakı zaman kəsiklərindən birini seçin:</i>";

                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildTimeframeKeyboard());
                return;
            }
            else if (text.Contains("Coin Sil") || text == "/delcoin")
            {
                _userStates[chatId] = "USER_WAITING_DELETE_COIN";
                var cleanList = userSettings.Coins.Count > 0 
                    ? string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", ""))) 
                    : "Siyahı boşdur";
                var prompt = "🗑 <b>Coin Silmək</b>\n\n" +
                             $"Cari siyahınız ({userSettings.Coins.Count} coin):\n" +
                             $"<code>{cleanList}</code>\n\n" +
                             "Siyahıdan silmək istədiyiniz coinlərin <b>Adını</b> yazın (məsələn: <code>SOL</code>, <code>LINK</code>):";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildUserKeyboard(userSettings, isAdmin));
                return;
            }
            else if (text.Contains("Coin Seçimi") || text.Contains("Coin Secimi") || text == "/setcoins")
            {
                var cleanList = userSettings.Coins.Count > 0 
                    ? string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", ""))) 
                    : "Heç bir coin seçilməyib (0 coin)";
                var prompt = "⚙️ <b>Coin Seçimi Paneli:</b>\n\n" +
                             $"🪙 <b>Hazırda Seçilmiş Coinlər:</b> {userSettings.Coins.Count} coin\n" +
                             $"<code>{cleanList}</code>\n\n" +
                             "Aşağıdakı seçimlərdən birini edin:\n" +
                             "• <b>📋 Standart 16 Coini Seç:</b> Əsas institusional 16 coini dərhal aktivləşdirin.\n" +
                             "• <b>➕ Öz coini əlavə et:</b> Binance Futures USDT siyahısından istənilən coini əlavə edin.\n" +
                             "• <b>🗑 Coin Sil:</b> Siyahınızdakı coinləri silin.";
                await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildCoinSelectionKeyboard());
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
