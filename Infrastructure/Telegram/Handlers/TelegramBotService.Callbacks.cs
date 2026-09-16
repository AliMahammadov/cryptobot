using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Common;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoSense.Infrastructure.Telegram
{
    // Partial class: Callback query handler (inline button interactions)
    public partial class TelegramBotService
    {

        private async Task HandleCallbackQueryAsync(string chatId, long messageId, string data, string fromUser, long? fromUserId)
        {
            // Cyber-defense early return: Drop any callback from blocked IDs immediately before any DB query, state change, or answering callback
            if (IsTelegramUserBlocked(fromUserId, chatId, fromUser))
            {
                return;
            }

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
                if (userSettings.LastPortfolioSummaryMessageId.HasValue && userSettings.LastPortfolioSummaryMessageId.Value == messageId)
                {
                    userSettings.LastPortfolioSummaryMessageId = null;
                }

                var activeCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                var lastTime = lastDeliveredUtc.HasValue ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) : "";
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, activeCount, lastTime);
                var inlineKb = TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isCallerAdmin);

                if (userSettings.LastTerminalMessageId.HasValue && userSettings.LastTerminalMessageId.Value != messageId)
                {
                    _ = DeleteMessageAsync(chatId, userSettings.LastTerminalMessageId.Value);
                }

                bool edited = await EditMessageTextAsync(chatId, messageId, dashText, inlineKb);
                if (!edited)
                {
                    var newMsgId = await SendMessageReturnIdAsync(dashText, chatId, inlineKb);
                    userSettings.LastTerminalMessageId = newMsgId;
                }
                else
                {
                    userSettings.LastTerminalMessageId = messageId;
                }

                userSettings.IsTerminalOpen = true;
                SaveSettings();
            }
            else if (data == "cb_terminal")
            {
                var activeCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                var lastTime = lastDeliveredUtc.HasValue ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) : "";
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, activeCount, lastTime);
                var inlineKb = TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isCallerAdmin);
                bool edited = await EditMessageTextAsync(chatId, messageId, dashText, inlineKb);
                if (!edited)
                {
                    var newMsgId = await SendMessageReturnIdAsync(dashText, chatId, inlineKb);
                    userSettings.LastTerminalMessageId = newMsgId;
                }
                else
                {
                    userSettings.LastTerminalMessageId = messageId;
                }

                userSettings.IsTerminalOpen = true;
                SaveSettings();
            }
            else if (data == "cb_refresh")
            {
                var activeCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                var lastTime = lastDeliveredUtc.HasValue ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) : "";
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, activeCount, lastTime);
                var inlineKb = TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isCallerAdmin);
                bool edited = await EditMessageTextAsync(chatId, messageId, dashText, inlineKb);
                if (!edited)
                {
                    var newMsgId = await SendMessageReturnIdAsync(dashText, chatId, inlineKb);
                    userSettings.LastTerminalMessageId = newMsgId;
                    userSettings.IsTerminalOpen = true;
                    SaveSettings();
                }
                _ = BuildAndSendPortfolioSummaryAsync(chatId, userSettings, forceRefresh: true);
            }
            else if (data == "cb_news")
            {
                var newsService = scope.ServiceProvider.GetRequiredService<INewsService>();
                var newsSummary = await newsService.GetNewsAndSentimentAsync();
                var msg = TelegramMessageFormatter.FormatNewsSentiment(newsSummary);
                await SendMessageAsync(msg, chatId, TelegramKeyboards.BuildBackToTerminalKeyboard());
            }
            else if (data == "cb_toggle")
            {
                if (userSettings.IsActive)
                {
                    var confirmMsg = TelegramMessageFormatter.FormatStopConfirmPrompt();
                    await EditMessageTextAsync(chatId, messageId, confirmMsg, TelegramKeyboards.BuildStopConfirmationKeyboard());
                    return;
                }

                if (userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "⛔ Ticarət başlamadı.\nSəbəb: heç bir coin seçilməyib.\nƏvvəl ⚙️ Coin Seçimi ilə ən azı 1 coin seçin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var activeCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                var lastTime = lastDeliveredUtc.HasValue ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) : "";
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, activeCount, lastTime);
                var inlineKb = TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isCallerAdmin);
                bool edited = await EditMessageTextAsync(chatId, messageId, dashText, inlineKb);
                if (!edited)
                {
                    var newMsgId = await SendMessageReturnIdAsync(dashText, chatId, inlineKb);
                    userSettings.LastTerminalMessageId = newMsgId;
                    userSettings.IsTerminalOpen = true;
                    SaveSettings();
                }
            }
            else if (data == "cb_toggle_stop_confirm")
            {
                userSettings.IsActive = false;
                SaveSettings();
                var activeCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                var lastTime = lastDeliveredUtc.HasValue ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) : "";
                var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, activeCount, lastTime);
                var inlineKb = TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, _testModeChats.ContainsKey(chatId), isCallerAdmin);
                bool edited = await EditMessageTextAsync(chatId, messageId, dashText, inlineKb);
                if (!edited)
                {
                    var newMsgId = await SendMessageReturnIdAsync(dashText, chatId, inlineKb);
                    userSettings.LastTerminalMessageId = newMsgId;
                    userSettings.IsTerminalOpen = true;
                    SaveSettings();
                }
            }
            else if (data == "cb_toggle_testmode")
            {
                bool newState = !_testModeChats.ContainsKey(chatId);
                if (newState)
                {
                    _testModeChats[chatId] = true;
                    var activeCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                    var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                    var lastTime = lastDeliveredUtc.HasValue ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) : "";
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
                    var activeCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                    var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                    var lastTime = lastDeliveredUtc.HasValue ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) : "";
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

                var msg = "🪙 <b>Standart 40 Coin Portfeli Seçildi 🟢</b>\n\n" +
                          "Bütün 40 əsas institusional coin Binance USD-M fyuçers bazarı üzrə 24/7 analiz edilir.\n\n" +
                          "<i>Zəhmət olmasa bu portfel üçün ticarət zaman kəsiyini seçin:</i>";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildStandard40TimeframeKeyboard());
            }
            else if (data == "cb_portfolio_custom")
            {
                var cleanCustom = userSettings.CustomCoins.Count > 0 
                    ? string.Join(", ", userSettings.CustomCoins.Select(c => c.Replace("USDT", "")))
                    : "<i>(Hələ heç bir fərdi coin əlavə olunmayıb)</i>";

                var msg = "⭐ <b>Fərdi Coin Portfeli İdarəetməsi</b>\n\n" +
                          $"🪙 <b>Sizin Əlavə Etdiyiniz Coinlər ({userSettings.CustomCoins.Count} ədəd):</b>\n" +
                          $"{cleanCustom}\n\n" +
                          "Aşağıdakı seçimlərdən birini edin:\n" +
                          "• <b>🎯 Yalnız Fərdi Coinlər:</b> Yalnız sizin seçdiyiniz coinləri izləyir\n" +
                          "• <b>🔥 40 + Fərdi Coin (Kombinə):</b> Həm standart 40, həm də sizin coinləri birlikdə izləyir\n" +
                          "• <b>➕ Coin Əlavə Et:</b> İstədiyiniz yeni coini portfelə qatın\n" +
                          "• <b>🗑 Coin Sil:</b> Portfeldən coin çıxarın";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
            }
            else if (data == "cb_cust_mode_only")
            {
                userSettings.PortfolioMode = "Custom";
                userSettings.Coins = new List<string>(userSettings.CustomCoins);
                SaveSettings();

                var cleanCustom = userSettings.CustomCoins.Count > 0 
                    ? string.Join(", ", userSettings.CustomCoins.Select(c => c.Replace("USDT", "")))
                    : "<i>(Boşdur - Əvvəlcə coin əlavə edin)</i>";

                var msg = "🎯 <b>Yalnız Fərdi Coinlər Rejimi Seçildi!</b>\n\n" +
                          $"🪙 <b>İzlənən Coinlər ({userSettings.Coins.Count} ədəd):</b>\n{cleanCustom}\n\n" +
                          "<i>Zəhmət olmasa bu portfel üçün ticarət zaman kəsiyini seçin:</i>";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
            }
            else if (data == "cb_cust_mode_comb")
            {
                userSettings.PortfolioMode = "Combined";
                var combSet = new HashSet<string>(Default40Coins);
                foreach (var c in userSettings.CustomCoins) combSet.Add(c);
                userSettings.Coins = combSet.ToList();
                SaveSettings();

                var msg = "🔥 <b>40 Standart + Fərdi Coinlər (Kombinə) Rejimi Seçildi!</b>\n\n" +
                          $"📊 <b>Ümumi İzlənən Coin Sayı:</b> <b>{userSettings.Coins.Count} ədəd</b>\n" +
                          $"• Standart: 40 institusional coin\n" +
                          $"• Fərdi: {userSettings.CustomCoins.Count} ədəd əlavə coin\n\n" +
                          "<i>Zəhmət olmasa bu kombinə portfel üçün ticarət zaman kəsiyini seçin:</i>";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildCombinedTimeframeKeyboard());
            }
            else if (data == "cb_comb_tf_1h" || data == "cb_comb_tf_4h" || data == "cb_comb_tf_all")
            {
                var targetTf = data == "cb_comb_tf_1h" ? "1h" : (data == "cb_comb_tf_4h" ? "4h" : "Hamısı");
                if (IsModeAndTimeframeAlreadyActive(userSettings, "Combined", targetTf))
                {
                    await ShowAlreadyActiveAlertAsync(chatId, messageId, "Combined", targetTf);
                    return;
                }
                await ActivatePortfolioAndTimeframeAsync(chatId, messageId, userSettings, "Combined", targetTf);
            }
            else if (data == "cb_std_tf_1h" || data == "cb_std_tf_4h" || data == "cb_std_tf_all")
            {
                var targetTf = data == "cb_std_tf_1h" ? "1h" : (data == "cb_std_tf_4h" ? "4h" : "Hamısı");
                if (IsModeAndTimeframeAlreadyActive(userSettings, "Standard40", targetTf))
                {
                    await ShowAlreadyActiveAlertAsync(chatId, messageId, "Standard40", targetTf);
                    return;
                }
                await ActivatePortfolioAndTimeframeAsync(chatId, messageId, userSettings, "Standard40", targetTf);
            }
            else if (data == "cb_cust_tf_1h" || data == "cb_cust_tf_4h" || data == "cb_cust_tf_all")
            {
                var targetMode = userSettings.PortfolioMode == "Combined" ? "Combined" : "Custom";
                if (targetMode == "Custom" && userSettings.CustomCoins.Count == 0)
                {
                    var warnMsg = "⚠️ <b>Fərdi portfeliniz boşdur!</b>\n\n" +
                                  "Zaman təyin etməzdən əvvəl <b>➕ Coin Əlavə Et</b> düyməsinə klikləyərək ən azı 1 coin əlavə edin.";
                    await EditMessageTextAsync(chatId, messageId, warnMsg, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
                    return;
                }

                var targetTf = data == "cb_cust_tf_1h" ? "1h" : (data == "cb_cust_tf_4h" ? "4h" : "Hamısı");
                if (IsModeAndTimeframeAlreadyActive(userSettings, targetMode, targetTf))
                {
                    await ShowAlreadyActiveAlertAsync(chatId, messageId, targetMode, targetTf);
                    return;
                }
                await ActivatePortfolioAndTimeframeAsync(chatId, messageId, userSettings, targetMode, targetTf);
            }
            else if (data == "cb_refresh_portfolio" || data == "cb_show_portfolio")
            {
                var summary = await BuildPortfolioSummaryAsync(chatId, userSettings, forceRefresh: true);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    await EditMessageTextAsync(chatId, messageId, summary, TelegramKeyboards.BuildPortfolioSummaryKeyboard());
                    userSettings.LastPortfolioSummaryMessageId = messageId;
                    SaveSettings();
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
            else if (data == "cb_stats" || data == "cb_stats_today" || data == "cb_stats_alltime")
            {
                bool isAllTime = (data == "cb_stats_alltime");
                var stats = (isCallerAdmin || chatId == SuperAdminChatId)
                    ? await unitOfWork.Signals.GetPerformanceStatsAsync(userSettings.Timeframe, userCoins: null, isAllTime: isAllTime)
                    : await signalEngine.GetUserPerformanceStatsAsync(chatId, userSettings.Timeframe, userSettings.Coins, isAllTime: isAllTime);
                var tfLabel = (string.IsNullOrWhiteSpace(userSettings.Timeframe) || userSettings.Timeframe == "Təyin olunmayıb" || BotConstants.Timeframe.IsAll(userSettings.Timeframe)) ? "1h, 4h" : userSettings.Timeframe;
                var statsMsg = TelegramMessageFormatter.FormatPerformanceStats(stats, tfLabel, isAllTime: isAllTime);
                await EditMessageTextAsync(chatId, messageId, statsMsg, TelegramKeyboards.BuildStatsKeyboard(isAllTime: isAllTime));
            }
            else if (data == "cb_status")
            {
                var openCount = await unitOfWork.Signals.GetUserOpenSignalsCountAsync(chatId);
                var lastDeliveredUtc = await unitOfWork.Signals.GetLastDeliveredSignalTimeUtcAsync(chatId);
                var lastTime = lastDeliveredUtc.HasValue ? Domain.Common.TimeHelper.FormatAz(lastDeliveredUtc.Value) : "";
                bool canPush = await CanReceivePushAsync(chatId);
                var statusMsg = TelegramMessageFormatter.FormatBotStatus(userSettings, openCount, lastTime, canPush);
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
                bool edited = await EditMessageTextAsync(chatId, messageId, adminDash, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                if (!edited)
                {
                    var newMsgId = await SendMessageReturnIdAsync(adminDash, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                    userSettings.LastAdminMessageId = newMsgId;
                    userSettings.IsAdminOpen = true;
                    SaveSettings();
                }
            }
            else if (data == "cb_admin_create_user")
            {
                _userStates[chatId] = "ADMIN_WAITING_CREATE_USER";
                var prompt = "➕ <b>Yeni İstifadəçi Yaratmaq</b>\n\n" +
                             "Yaratmaq istədiyiniz <b>İstifadəçi Adını</b> və <b>Parolu</b> aralarında boşluq qoyaraq yazın:\n\n" +
                             "📌 <b>Məsələn:</b>\n" +
                             "<code>Murad 123456</code>";
                bool edited = await EditMessageTextAsync(chatId, messageId, prompt, TelegramKeyboards.BuildBackToAdminKeyboard());
                if (!edited)
                    await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildBackToAdminKeyboard());
            }
            else if (data == "cb_admin_list_users")
            {
                var userManager = scope.ServiceProvider.GetRequiredService<IUserManagerService>();
                var allUsers = await userManager.GetAllUsersAsync();
                var userListMsg = TelegramMessageFormatter.FormatUserList(allUsers);
                bool edited = await EditMessageTextAsync(chatId, messageId, userListMsg, TelegramKeyboards.BuildBackToAdminKeyboard());
                if (!edited)
                    await SendMessageAsync(userListMsg, chatId, TelegramKeyboards.BuildBackToAdminKeyboard());
            }
            else if (data == "cb_admin_del_user")
            {
                _userStates[chatId] = "ADMIN_WAITING_DELETE_USER";
                var prompt = "🗑 <b>İstifadəçi Silmək</b>\n\n" +
                             "Silmək istədiyiniz <b>İstifadəçi Adını</b> yazın:\n\n" +
                             "📌 <b>Məsələn:</b> <code>Murad</code>";
                bool edited = await EditMessageTextAsync(chatId, messageId, prompt, TelegramKeyboards.BuildBackToAdminKeyboard());
                if (!edited)
                    await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildBackToAdminKeyboard());
            }
            else if (data == "cb_admin_change_pwd")
            {
                _userStates[chatId] = "ADMIN_WAITING_RESET_PWD";
                var prompt = "🔑 <b>Parolu Dəyişmək</b>\n\n" +
                             "İstifadəçi adını və yeni parolu aralarında boşluq qoyaraq yazın:\n\n" +
                             "📌 <b>Məsələn:</b> <code>Murad yeniParol123</code>";
                bool edited = await EditMessageTextAsync(chatId, messageId, prompt, TelegramKeyboards.BuildBackToAdminKeyboard());
                if (!edited)
                    await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildBackToAdminKeyboard());
            }
            else if (data == "cb_admin_export_db")
            {
                var currentDbPath = CryptoSense.Domain.Common.AppPaths.DatabasePath;

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
                bool edited = await EditMessageTextAsync(chatId, messageId, statsMsg, TelegramKeyboards.BuildBackToAdminKeyboard());
                if (!edited)
                    await SendMessageAsync(statsMsg, chatId, TelegramKeyboards.BuildBackToAdminKeyboard());
            }
            else if (data == "cb_close_terminal")
            {
                userSettings.IsTerminalOpen = false;
                userSettings.LastTerminalMessageId = null;
                SaveSettings();
                await DeleteMessageAsync(chatId, messageId);
            }
            else if (data == "cb_admin_purge_test_users")
            {
                using var scope2 = _serviceProvider.CreateScope();
                var uow2 = scope2.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var ctx = scope2.ServiceProvider.GetRequiredService<CryptoSense.Infrastructure.Persistence.AppDbContext>();

                // Delete only test / QA / session accounts — never touch Admin or real users
                var testPrefixes = new[] { "qa_tester_", "session_user_", "testuser_", "AliTest_" };
                var toDelete = ctx.Users.ToList()
                    .Where(u => u.Role != Domain.Enums.UserRole.Admin &&
                                testPrefixes.Any(p => u.Username.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                int count = toDelete.Count;
                if (count > 0)
                {
                    ctx.Users.RemoveRange(toDelete);
                    ctx.SaveChanges();
                    // Also remove from UserPreferences in-memory cache
                    foreach (var u in toDelete)
                    {
                        UserPreferences.TryRemove(u.TelegramChatId ?? u.Username, out _);
                    }
                }

                var resultMsg = count > 0
                    ? $"🧹 <b>{count} test hesabı uğurla silindi.</b>\n\n✅ Real istifadəçilərə toxunulmadı."
                    : "ℹ️ <b>Silinəcək test hesabı tapılmadı.</b>\n\nBütün hesablar real görünür.";

                bool edited = await EditMessageTextAsync(chatId, messageId, resultMsg, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
                if (!edited)
                    await SendMessageAsync(resultMsg, chatId, TelegramKeyboards.BuildAdminTerminalInlineKeyboard());
            }
            else if (data == "cb_admin_blocked_ids" || data == "cb_admin_unblock_no")
            {
                var db = scope.ServiceProvider.GetRequiredService<CryptoSense.Infrastructure.Persistence.AppDbContext>();
                var blockedList = db.TelegramLoginBlocks
                    .Where(b => b.IsBlocked)
                    .OrderByDescending(b => b.BlockedAtUtc)
                    .ToList();

                if (blockedList.Count == 0)
                {
                    var emptyMsg = "🚫 <b>Bloklanmış Telegram ID-lər</b>\n\nHazırda bloklanmış Telegram ID yoxdur.";
                    bool edited = await EditMessageTextAsync(chatId, messageId, emptyMsg, TelegramKeyboards.BuildBackToAdminKeyboard());
                    if (!edited)
                        await SendMessageAsync(emptyMsg, chatId, TelegramKeyboards.BuildBackToAdminKeyboard());
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("🚫 <b>Bloklanmış Telegram ID-lər</b>\n");
                    var rows = new List<object[]>();
                    foreach (var b in blockedList)
                    {
                        var uname = string.IsNullOrWhiteSpace(b.TelegramUsername) ? "username yoxdur" : "@" + b.TelegramUsername.TrimStart('@');
                        var timeStr = b.BlockedAtUtc.HasValue ? TimeHelper.FormatAz(b.BlockedAtUtc.Value) : TimeHelper.FormatAz(b.LastAttemptAtUtc);
                        sb.AppendLine($"• ID: <code>{b.TelegramUserId}</code> ({uname}) | Son cəhd: <code>{b.LastAttemptUsername ?? "—"}</code> | Vaxt: {timeStr}");

                        rows.Add(new object[]
                        {
                            new { text = $"🔓 Blokdan çıxar ({b.TelegramUserId})", callback_data = $"cb_admin_unblock_{b.TelegramUserId}" }
                        });
                    }

                    rows.Add(new object[]
                    {
                        new { text = "⬅️ Admin Panelinə Qayıt", callback_data = "cb_admin_menu" }
                    });

                    var kb = new { inline_keyboard = rows.ToArray() };
                    var msg = sb.ToString();
                    bool edited = await EditMessageTextAsync(chatId, messageId, msg, kb);
                    if (!edited)
                        await SendMessageAsync(msg, chatId, kb);
                }
            }
            else if (data.StartsWith("cb_admin_unblock_") && !data.StartsWith("cb_admin_unblock_yes_") && data != "cb_admin_unblock_no")
            {
                var idStr = data.Substring("cb_admin_unblock_".Length);
                if (long.TryParse(idStr, out var targetId))
                {
                    var confirmMsg = $"Bu Telegram ID-ni blokdan çıxarmaq istəyirsiniz?\nID: <code>{targetId}</code>";
                    var confirmKb = new
                    {
                        inline_keyboard = new[]
                        {
                            new[]
                            {
                                new { text = "✅ Bəli, çıxar", callback_data = $"cb_admin_unblock_yes_{targetId}" },
                                new { text = "❌ Xeyr", callback_data = "cb_admin_unblock_no" }
                            }
                        }
                    };

                    bool edited = await EditMessageTextAsync(chatId, messageId, confirmMsg, confirmKb);
                    if (!edited)
                        await SendMessageAsync(confirmMsg, chatId, confirmKb);
                }
            }
            else if (data.StartsWith("cb_admin_unblock_yes_"))
            {
                var idStr = data.Substring("cb_admin_unblock_yes_".Length);
                if (long.TryParse(idStr, out var targetId))
                {
                    await UnblockTelegramUserAsync(targetId);
                    var resultMsg = "✅ ID blokdan çıxarıldı.";
                    var backKb = new
                    {
                        inline_keyboard = new[]
                        {
                            new[]
                            {
                                new { text = "🚫 Bloklanmış ID-lər", callback_data = "cb_admin_blocked_ids" },
                                new { text = "⬅️ Admin Paneli", callback_data = "cb_admin_menu" }
                            }
                        }
                    };

                    bool edited = await EditMessageTextAsync(chatId, messageId, resultMsg, backKb);
                    if (!edited)
                        await SendMessageAsync(resultMsg, chatId, backKb);
                }
            }
            else if (data == "cb_close_admin")
            {
                userSettings.IsAdminOpen = false;
                userSettings.LastAdminMessageId = null;
                SaveSettings();
                await DeleteMessageAsync(chatId, messageId);
            }
        }

        private bool IsModeAndTimeframeAlreadyActive(UserSettings settings, string targetMode, string targetTf)
        {
            if (!settings.IsActive) return false;
            if (!string.Equals(settings.PortfolioMode, targetMode, StringComparison.OrdinalIgnoreCase)) return false;

            bool currentIsAll = BotConstants.Timeframe.IsAll(settings.Timeframe);
            bool targetIsAll = BotConstants.Timeframe.IsAll(targetTf);

            if (currentIsAll && targetIsAll) return true;

            return string.Equals(settings.Timeframe, targetTf, StringComparison.OrdinalIgnoreCase);
        }

        private async Task ShowAlreadyActiveAlertAsync(string chatId, long messageId, string targetMode, string targetTf)
        {
            var modeName = targetMode switch
            {
                "Standard40" => "🪙 Standart 40 Coin",
                "Custom" => "⭐ Fərdi Coinlər",
                "Combined" => "🔥 40 + Fərdi Coin (Kombinə)",
                _ => targetMode
            };
            var tfDisplay = (targetTf == "Hamısı" || targetTf == "Hamisi") ? "1h + 4h (Hər İkisi)" : targetTf;

            var alertMsg = "⚠️ <b>Bu Rejim və Zaman Kəsiyi Artıq Seçilib!</b>\n\n" +
                           $"📌 <b>Aktiv Rejim:</b> {modeName}\n" +
                           $"⏱ <b>Aktiv Zaman Kəsiyi:</b> <code>{tfDisplay}</code>\n" +
                           $"🟢 <b>Cari Vəziyyət:</b> Skaner artıq bu rejim və zaman parametrləri ilə aktiv işləyir.\n\n" +
                           "🚫 <i>Eyni əmr təkrar icra edilə bilməz.</i>\n" +
                           "💡 <b>Qeyd:</b> Yalnız portfeli sıfırladıqdan (🧹 <b>Sıfırla</b>) və ya fərqli zaman kəsiyi/rejim seçdikdən sonra yenidən başlamaq olar.";

            await EditMessageTextAsync(chatId, messageId, alertMsg, TelegramKeyboards.BuildAlreadyActiveKeyboard());
        }

        private async Task ActivatePortfolioAndTimeframeAsync(string chatId, long messageId, UserSettings userSettings, string targetMode, string targetTf)
        {
            if (targetMode == "Custom" && userSettings.CustomCoins.Count == 0)
            {
                var warnMsg = "⚠️ <b>Fərdi portfeliniz boşdur!</b>\n\n" +
                              "Zaman təyin etməzdən əvvəl <b>➕ Coin Əlavə Et</b> düyməsinə klikləyərək ən azı 1 coin əlavə edin.";
                await EditMessageTextAsync(chatId, messageId, warnMsg, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
                return;
            }

            userSettings.PortfolioMode = targetMode;
            if (targetMode == "Standard40")
            {
                userSettings.Coins = new List<string>(Default40Coins);
            }
            else if (targetMode == "Custom")
            {
                userSettings.Coins = new List<string>(userSettings.CustomCoins);
            }
            else if (targetMode == "Combined")
            {
                var combSet = new HashSet<string>(Default40Coins);
                foreach (var c in userSettings.CustomCoins) combSet.Add(c);
                userSettings.Coins = combSet.ToList();
            }

            userSettings.Timeframe = targetTf;
            userSettings.IsActive = true;
            userSettings.LastResumeTime = DateTime.UtcNow;
            SaveSettings();

            // Sync IsActive=true to DB so CanReceivePushAsync DB-check passes
            try
            {
                using var activateScope = _serviceProvider.CreateScope();
                var activateUow = activateScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var dbUser = await activateUow.Users.GetByChatIdOrTelegramUserIdAsync(chatId, null);
                if (dbUser != null)
                {
                    dbUser.IsActive = true;
                    dbUser.IsLoggedIn = true;
                    await activateUow.Users.UpdateAsync(dbUser);
                    await activateUow.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ActivatePortfolio] DB sync error: {ex.Message}");
            }

            var modeName = targetMode switch
            {
                "Standard40" => "🪙 Standart 40 Coin",
                "Custom" => "⭐ Fərdi Coinlər",
                "Combined" => "🔥 40 + Fərdi Coin (Kombinə)",
                _ => targetMode
            };
            var tfDisplay = (targetTf == "Hamısı" || targetTf == "Hamisi") ? "1h, 4h" : targetTf;

            // 1. Immediate loading feedback on screen
            var loadingMsg = $"⏳ <b>{modeName} Aktivləşdirilir... 🟢</b>\n\n" +
                             $"⏱ <b>Zaman Rejimi:</b> <code>{tfDisplay}</code>\n" +
                             $"🪙 <b>İzlənən Coin Sayı:</b> <b>{userSettings.Coins.Count} ədəd</b>\n\n" +
                             $"<i>Binance USD-M fyuçers bazarı üzrə ən son canlı qiymətlər və bazar vəziyyəti yüklənir...</i>";
            await EditMessageTextAsync(chatId, messageId, loadingMsg);

            // 2. Fetch fresh live prices and market overview directly from Binance
            var summary = await BuildPortfolioSummaryAsync(chatId, userSettings, forceRefresh: true);
            if (string.IsNullOrWhiteSpace(summary))
            {
                var fallbackMsg = $"✅ <b>{modeName} Aktivləşdirildi! 🟢</b>\n\n" +
                                  $"⏱ <b>Zaman Rejimi:</b> <code>{tfDisplay}</code>\n" +
                                  $"🪙 <b>İzlənən Coinlər ({userSettings.Coins.Count} ədəd):</b> Bütün aktivlər 24/7 skaner tərəfindən izlənir.\n\n" +
                                  $"🚀 Skaner aktivdir. Yalnız 1h və 4h şam bağlanışında A+ konfluens siqnalları göndəriləcək.";
                await EditMessageTextAsync(chatId, messageId, fallbackMsg, TelegramKeyboards.BuildPortfolioSummaryKeyboard());
            }
            else
            {
                await EditMessageTextAsync(chatId, messageId, summary, TelegramKeyboards.BuildPortfolioSummaryKeyboard());
            }

            userSettings.LastPortfolioSummaryMessageId = messageId;
            SaveSettings();

            if (_testModeChats.ContainsKey(chatId))
            {
                _ = SendMockTestSignalAsync(chatId, userSettings, userSettings.Timeframe);
            }
        }
    }
}