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
using Microsoft.Extensions.DependencyInjection;

namespace CryptoSense.Infrastructure.Telegram
{
    // Partial class: Callback query handler (inline button interactions)
    public partial class TelegramBotService
    {
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
                await SendMessageAsync("Ôøö <b>S╔Ölahiyy╔Ötiniz ├ğatm─▒r!</b>\n\nBu panel yaln─▒z Sistem Adminin╔Ö m╔Öxsusdur.", chatId);
                return;
            }

            if ((data == "cb_toggle_testmode" || data == "cb_reset" || data == "cb_reset_confirm") && !isCallerAdmin)
            {
                await SendMessageAsync("Ôøö <b>S╔Ölahiyy╔Ötiniz ├ğatm─▒r!</b>\n\nBu funksiya yaln─▒z Sistem Adminin╔Ö m╔Öxsusdur.", chatId);
                return;
            }

            if (data == "cb_menu")
            {
                var activeCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
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
                _ = BuildAndSendPortfolioSummaryAsync(chatId, userSettings, forceRefresh: false);
            }
            else if (data == "cb_refresh")
            {
                var activeCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
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
                if (!userSettings.IsActive && userSettings.Coins.Count == 0)
                {
                    await SendMessageAsync(
                        "Ôøö Ticar╔Öt ba┼şlamad─▒.\nS╔Öb╔Öb: he├ğ bir coin se├ğilm╔Öyib.\nãÅvv╔Öl ÔÜÖ´©Å Coin Se├ğimi il╔Ö ╔Ön az─▒ 1 coin se├ğin.",
                        chatId,
                        TelegramKeyboards.BuildCoinSelectionKeyboard());
                    return;
                }
                userSettings.IsActive = !userSettings.IsActive;
                SaveSettings();
                var activeCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
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
                    var activeCount = await unitOfWork.Signals.GetActiveSignalsCountAsync();
                    var lastTime = userSettings.LastSignalSentUtc == default ? "" : Domain.Common.TimeHelper.FormatAz(userSettings.LastSignalSentUtc);
                    var dashText = TelegramMessageFormatter.FormatTerminalDashboard(userSettings, activeCount, lastTime);
                    await EditMessageTextAsync(chatId, messageId, dashText, TelegramKeyboards.BuildTerminalInlineKeyboard(userSettings, isTestMode: true, isAdmin: true));

                    var testActiveMsg = "­şğ¬ <b>Test Simulyasiya Rejimi AKT─░VLãÅ┼ŞD─░R─░LD─░! ­şşó</b>\n\n" +
                                        "─░ndi ist╔Önil╔Ön portfel (m╔Ös. <b>­ş¬Ö Standart 40 Coin</b> v╔Ö ya <b>Ô¡É M╔Önim Coinl╔Örim</b>) se├ğib zaman aral─▒─ş─▒n─▒ (1h/4h) t╔Öyin edin.\n\n" +
                                        "ÔÜí Sistem d╔Örhal siz╔Ö real Confluence il╔Ö n├╝mun╔Övi <b>TEST Siqnal─▒</b> v╔Ö 6 saniy╔Ö sonra <b>TP1 H╔Öd╔Öfi (+1.20%)</b> bildiri┼şi g├Ând╔Ör╔Öc╔Ök!\n\n" +
                                        "<i>Testi dayand─▒rmaq ├╝├ğ├╝n: <code>stop test</code></i>";
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

                    var testStopMsg = "ÔÜ¬ <b>Test Rejimi DAYANDIRILDI!</b>\n\n" +
                                      "­şÜÇ Sistem 100% real canl─▒ bazar analizin╔Ö qay─▒td─▒. Yaln─▒z Binance birjas─▒nda t╔Ösdiql╔Ön╔Ön real bazar siqnallar─▒ g├Ând╔Öril╔Öc╔Ök.";
                    await SendMessageAsync(testStopMsg, chatId);
                }
            }
            else if (data == "cb_portfolio_std40")
            {
                userSettings.PortfolioMode = "Standard40";
                userSettings.Coins = new List<string>(Default40Coins);
                SaveSettings();

                var msg = "­ş¬Ö <b>Standart 40 Coin Portfeli Se├ğildi ­şşó</b>\n\n" +
                          "B├╝t├╝n 40 ╔Ösas institusional coin Binance USD-M fyu├ğers bazar─▒ ├╝zr╔Ö 24/7 analiz edilir.\n\n" +
                          "<i>Z╔Öhm╔Öt olmasa bu portfel ├╝├ğ├╝n ticar╔Öt zaman k╔Ösiyini se├ğin:</i>";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildStandard40TimeframeKeyboard());
            }
            else if (data == "cb_portfolio_custom")
            {
                var cleanCustom = userSettings.CustomCoins.Count > 0 
                    ? string.Join(", ", userSettings.CustomCoins.Select(c => c.Replace("USDT", "")))
                    : "<i>(H╔Öl╔Ö he├ğ bir f╔Ördi coin ╔Ölav╔Ö olunmay─▒b)</i>";

                var msg = "Ô¡É <b>F╔Ördi Coin Portfeli ─░dar╔Öetm╔Ösi</b>\n\n" +
                          $"­ş¬Ö <b>Sizin ãÅlav╔Ö Etdiyiniz Coinl╔Ör ({userSettings.CustomCoins.Count} ╔Öd╔Öd):</b>\n" +
                          $"{cleanCustom}\n\n" +
                          "A┼şa─ş─▒dak─▒ se├ğiml╔Örd╔Ön birini edin:\n" +
                          "ÔÇó <b>­şÄ» Yaln─▒z F╔Ördi Coinl╔Ör:</b> Yaln─▒z sizin se├ğdiyiniz coinl╔Öri izl╔Öyir\n" +
                          "ÔÇó <b>­şöÑ 40 + F╔Ördi Coin (Kombin╔Ö):</b> H╔Öm standart 40, h╔Öm d╔Ö sizin coinl╔Öri birlikd╔Ö izl╔Öyir\n" +
                          "ÔÇó <b>ÔŞò Coin ãÅlav╔Ö Et:</b> ─░st╔Ödiyiniz yeni coini portfel╔Ö qat─▒n\n" +
                          "ÔÇó <b>­şùæ Coin Sil:</b> Portfeld╔Ön coin ├ğ─▒xar─▒n";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
            }
            else if (data == "cb_cust_mode_only")
            {
                userSettings.PortfolioMode = "Custom";
                userSettings.Coins = new List<string>(userSettings.CustomCoins);
                SaveSettings();

                var cleanCustom = userSettings.CustomCoins.Count > 0 
                    ? string.Join(", ", userSettings.CustomCoins.Select(c => c.Replace("USDT", "")))
                    : "<i>(Bo┼şdur - ãÅvv╔Ölc╔Ö coin ╔Ölav╔Ö edin)</i>";

                var msg = "­şÄ» <b>Yaln─▒z F╔Ördi Coinl╔Ör Rejimi Se├ğildi!</b>\n\n" +
                          $"­ş¬Ö <b>─░zl╔Ön╔Ön Coinl╔Ör ({userSettings.Coins.Count} ╔Öd╔Öd):</b>\n{cleanCustom}\n\n" +
                          "<i>Z╔Öhm╔Öt olmasa bu portfel ├╝├ğ├╝n ticar╔Öt zaman k╔Ösiyini se├ğin:</i>";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildCustomCoinsKeyboard(userSettings));
            }
            else if (data == "cb_cust_mode_comb")
            {
                userSettings.PortfolioMode = "Combined";
                var combSet = new HashSet<string>(Default40Coins);
                foreach (var c in userSettings.CustomCoins) combSet.Add(c);
                userSettings.Coins = combSet.ToList();
                SaveSettings();

                var msg = "­şöÑ <b>40 Standart + F╔Ördi Coinl╔Ör (Kombin╔Ö) Rejimi Se├ğildi!</b>\n\n" +
                          $"­şôè <b>├£mumi ─░zl╔Ön╔Ön Coin Say─▒:</b> <b>{userSettings.Coins.Count} ╔Öd╔Öd</b>\n" +
                          $"ÔÇó Standart: 40 institusional coin\n" +
                          $"ÔÇó F╔Ördi: {userSettings.CustomCoins.Count} ╔Öd╔Öd ╔Ölav╔Ö coin\n\n" +
                          "<i>Z╔Öhm╔Öt olmasa bu kombin╔Ö portfel ├╝├ğ├╝n ticar╔Öt zaman k╔Ösiyini se├ğin:</i>";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildCombinedTimeframeKeyboard());
            }
            else if (data == "cb_comb_tf_1h" || data == "cb_comb_tf_4h" || data == "cb_comb_tf_all")
            {
                userSettings.PortfolioMode = "Combined";
                var combSet = new HashSet<string>(Default40Coins);
                foreach (var c in userSettings.CustomCoins) combSet.Add(c);
                userSettings.Coins = combSet.ToList();
                userSettings.Timeframe = data == "cb_comb_tf_1h" ? "1h" : (data == "cb_comb_tf_4h" ? "4h" : "Ham─▒s─▒");
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var msg = $"Ô£à <b>40 Standart + F╔Ördi Kombin╔Ö Portfeli Aktivl╔Ö┼şdirildi! ­şşó</b>\n\n" +
                          $"ÔÅ▒ <b>Zaman Rejimi:</b> <code>{userSettings.Timeframe}</code>\n" +
                          $"­ş¬Ö <b>─░zl╔Ön╔Ön Coinl╔Ör ({userSettings.Coins.Count} ╔Öd╔Öd):</b>\n<code>{cleanList}</code>\n\n" +
                          $"­şÜÇ Skaner aktivdir. Yaln─▒z 1h v╔Ö 4h ┼şam ba─şlan─▒┼ş─▒nda A+ konfluens siqnallar─▒ g├Ând╔Öril╔Öc╔Ök.";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildBackToTerminalKeyboard());
                _ = BuildAndSendPortfolioSummaryAsync(chatId, userSettings, forceRefresh: true);
                if (_testModeChats.ContainsKey(chatId))
                {
                    _ = SendMockTestSignalAsync(chatId, userSettings, userSettings.Timeframe);
                }
            }
            else if (data == "cb_std_tf_1h" || data == "cb_std_tf_4h" || data == "cb_std_tf_all")
            {
                userSettings.PortfolioMode = "Standard40";
                userSettings.Coins = new List<string>(Default40Coins);
                userSettings.Timeframe = data == "cb_std_tf_1h" ? "1h" : (data == "cb_std_tf_4h" ? "4h" : "Ham─▒s─▒");
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();
                var cleanList = string.Join(", ", Default40Coins.Select(c => c.Replace("USDT", "")));
                var msg = $"Ô£à <b>Standart 40 Coin Portfeli Aktivl╔Ö┼şdirildi! ­şşó</b>\n\n" +
                          $"ÔÅ▒ <b>Zaman Rejimi:</b> <code>{userSettings.Timeframe}</code>\n" +
                          $"­ş¬Ö <b>─░zl╔Ön╔Ön Coinl╔Ör:</b> 40/40 ─░nstitusional coin\n\n" +
                          $"­şÜÇ Skaner aktivdir. Yaln─▒z 1h v╔Ö 4h ┼şam ba─şlan─▒┼ş─▒nda A+ konfluens siqnallar─▒ g├Ând╔Öril╔Öc╔Ök.";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildBackToTerminalKeyboard());
                _ = BuildAndSendPortfolioSummaryAsync(chatId, userSettings, forceRefresh: true);
                if (_testModeChats.ContainsKey(chatId))
                {
                    _ = SendMockTestSignalAsync(chatId, userSettings, userSettings.Timeframe);
                }
            }
            else if (data == "cb_cust_tf_1h" || data == "cb_cust_tf_4h" || data == "cb_cust_tf_all")
            {
                if (userSettings.PortfolioMode == "Custom" && userSettings.CustomCoins.Count == 0)
                {
                    var warnMsg = "ÔÜá´©Å <b>F╔Ördi portfeliniz bo┼şdur!</b>\n\n" +
                                  "Zaman t╔Öyin etm╔Özd╔Ön ╔Övv╔Öl <b>ÔŞò Coin ãÅlav╔Ö Et</b> d├╝ym╔Ösin╔Ö klikl╔Öy╔Ör╔Ök ╔Ön az─▒ 1 coin ╔Ölav╔Ö edin.";
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
                userSettings.Timeframe = data == "cb_cust_tf_1h" ? "1h" : (data == "cb_cust_tf_4h" ? "4h" : "Ham─▒s─▒");
                userSettings.IsActive = true;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var portName = userSettings.PortfolioMode == "Combined" ? "­şöÑ 40 + F╔Ördi Coin (Kombin╔Ö)" : "Ô¡É F╔Ördi Coinl╔Ör";
                var cleanList = string.Join(", ", userSettings.Coins.Select(c => c.Replace("USDT", "")));
                var msg = $"Ô£à <b>{portName} Portfeli Aktivl╔Ö┼şdirildi! ­şşó</b>\n\n" +
                          $"ÔÅ▒ <b>Zaman Rejimi:</b> <code>{userSettings.Timeframe}</code>\n" +
                          $"­ş¬Ö <b>─░zl╔Ön╔Ön Coinl╔Ör ({userSettings.Coins.Count} ╔Öd╔Öd):</b>\n<code>{cleanList}</code>\n\n" +
                          $"­şÜÇ Skaner aktivdir. Yaln─▒z 1h v╔Ö 4h ┼şam ba─şlan─▒┼ş─▒nda A+ konfluens siqnallar─▒ g├Ând╔Öril╔Öc╔Ök.";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildBackToTerminalKeyboard());
                _ = BuildAndSendPortfolioSummaryAsync(chatId, userSettings, forceRefresh: true);
                if (_testModeChats.ContainsKey(chatId))
                {
                    _ = SendMockTestSignalAsync(chatId, userSettings, userSettings.Timeframe);
                }
            }
            else if (data == "cb_custom_add" || data == "cb_coin_add")
            {
                _userStates[chatId] = "WAITING_ADD_CUSTOM_COIN";
                var msg = "ÔŞò <b>Yeni F╔Ördi Coin ãÅlav╔Ö Et</b>\n\n" +
                          "─░zl╔Öm╔Ök ist╔Ödiyiniz coinin ad─▒n─▒ mesaj olaraq yaz─▒n (m╔Ös╔Öl╔Ön: <code>SOL</code>, <code>NOT</code> v╔Ö ya <code>DOGE</code>):";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildBackToTerminalKeyboard());
            }
            else if (data == "cb_custom_del" || data == "cb_coin_del")
            {
                _userStates[chatId] = "USER_WAITING_DELETE_COIN";
                var custCoins = userSettings.CustomCoins.Select(c => c.Replace("USDT", "")).ToList();
                var custDisplay = custCoins.Count > 0 ? string.Join(", ", custCoins) : "H╔Öl╔Ö he├ğ bir f╔Ördi coin ╔Ölav╔Ö edilm╔Öyib.";
                var msg = "­şùæ <b>F╔Ördi Coin Sil</b>\n\n" +
                          $"Cari f╔Ördi coinl╔Öriniz:\n<code>{custDisplay}</code>\n\n" +
                          "Silm╔Ök ist╔Ödiyiniz coinin ad─▒n─▒ mesaj olaraq yaz─▒n (m╔Ös╔Öl╔Ön: <code>SOL</code>):";
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
                var tfLabel = (string.IsNullOrWhiteSpace(userSettings.Timeframe) || userSettings.Timeframe == "T╔Öyin olunmay─▒b" || userSettings.Timeframe == "Ham─▒s─▒" || userSettings.Timeframe == "Hamisi") ? "1h, 4h" : userSettings.Timeframe;
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
                    statusMsg += "\n\n­şğ¬ <b>Test Rejimi:</b> AKT─░VD─░R ­şşó\n<i>B├╝t├╝n butonlar v╔Ö simulyasiyalar test ├╝├ğ├╝n haz─▒rd─▒r.</i>";
                }
                await EditMessageTextAsync(chatId, messageId, statusMsg, TelegramKeyboards.BuildBackToTerminalKeyboard());
            }
            else if (data == "cb_coins" || data == "cb_coins_40")
            {
                userSettings.PortfolioMode = "Standard40";
                userSettings.Coins = new List<string>(Default40Coins);
                SaveSettings();
                var cleanList = string.Join(", ", Default40Coins.Select(c => c.Replace("USDT", "")));
                var msg = $"Ô£à <b>Standart 40 institusional coin se├ğildi (40/40).</b>\n\n" +
                          $"­şôï <b>─░zl╔Ön╔Ön Coinl╔Ör:</b>\n<code>{cleanList}</code>\n\n" +
                          $"­şÜÇ B├╝t├╝n 40 aktiv skaner t╔Ör╔Öfind╔Ön 24/7 analiz olunur.";
                await EditMessageTextAsync(chatId, messageId, msg, TelegramKeyboards.BuildStandard40TimeframeKeyboard());
            }
            else if (data == "cb_reset")
            {
                var confirmMsg = TelegramMessageFormatter.FormatResetConfirmationPrompt();
                await EditMessageTextAsync(chatId, messageId, confirmMsg, TelegramKeyboards.BuildResetConfirmationKeyboard());
            }
            else if (data == "cb_reset_confirm")
            {
                // BãÅND 6: ­şğ╣ S─▒f─▒rla (SuperAdmin): statistika + BA─ŞLI = 0. A├ğ─▒q m├Âvqey╔Ö toxunma.
                await unitOfWork.Signals.ResetClosedSignalsAsync();
                await unitOfWork.SaveChangesAsync();

                userSettings.IsActive = false;
                userSettings.Timeframe = "T╔Öyin olunmay─▒b";
                userSettings.AlertCounter = 0;
                userSettings.LastResumeTime = DateTime.UtcNow;
                SaveSettings();

                var resetMsg = "­şøæ <b>B├╝t├╝n Ba─şl─▒ ãÅm╔Öliyyatlar v╔Ö Statistika S─▒f─▒rland─▒!</b>\n\n" +
                               "ÔÇó Skaner v╔Ö bildiri┼şl╔Ör <b>dayand─▒r─▒ld─▒ (Dayand─▒r─▒l─▒b ­şö┤)</b>.\n" +
                               "ÔÇó Ba─şl─▒ ╔Öm╔Öliyyat tarix├ğ╔Ösi v╔Ö statistika s─▒f─▒rland─▒ (#0).\n" +
                               "ÔÇó <b>A├ğ─▒q m├Âvqel╔Ör toxunulmaz saxlan─▒ld─▒.</b>\n" +
                               $"ÔÇó <b>Qorunan Portfeliniz:</b> {userSettings.Coins.Count} ╔Öd╔Öd coin qorunub saxlan─▒ld─▒.\n\n" +
                               "<i>Yenid╔Ön ba┼şlamaq ├╝├ğ├╝n a┼şa─ş─▒dak─▒ d├╝ym╔Ö il╔Ö Terminala qay─▒d─▒n v╔Ö portfel/zaman se├ğin.</i>";
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
                var prompt = "ÔŞò <b>Yeni ─░stifad╔Ö├ği Yaratmaq</b>\n\n" +
                             "Yaratmaq ist╔Ödiyiniz <b>─░stifad╔Ö├ği Ad─▒n─▒</b> v╔Ö <b>Parolu</b> aralar─▒nda bo┼şluq qoyaraq yaz─▒n:\n\n" +
                             "­şôî <b>M╔Ös╔Öl╔Ön:</b>\n" +
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
                var prompt = "­şùæ <b>─░stifad╔Ö├ği Silm╔Ök</b>\n\n" +
                             "Silm╔Ök ist╔Ödiyiniz <b>─░stifad╔Ö├ği Ad─▒n─▒</b> yaz─▒n:\n\n" +
                             "­şôî <b>M╔Ös╔Öl╔Ön:</b> <code>Murad</code>";
                bool edited = await EditMessageTextAsync(chatId, messageId, prompt, TelegramKeyboards.BuildBackToAdminKeyboard());
                if (!edited)
                    await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildBackToAdminKeyboard());
            }
            else if (data == "cb_admin_change_pwd")
            {
                _userStates[chatId] = "ADMIN_WAITING_RESET_PWD";
                var prompt = "­şöæ <b>Parolu D╔Öyi┼şm╔Ök</b>\n\n" +
                             "─░stifad╔Ö├ği ad─▒n─▒ v╔Ö yeni parolu aralar─▒nda bo┼şluq qoyaraq yaz─▒n:\n\n" +
                             "­şôî <b>M╔Ös╔Öl╔Ön:</b> <code>Murad yeniParol123</code>";
                bool edited = await EditMessageTextAsync(chatId, messageId, prompt, TelegramKeyboards.BuildBackToAdminKeyboard());
                if (!edited)
                    await SendMessageAsync(prompt, chatId, TelegramKeyboards.BuildBackToAdminKeyboard());
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
                    var cap = $"­şÆ¥ <b>CryptoSense SQLite Veril╔Önl╔Ör Bazas─▒</b>\n\n" +
                              $"­şôà <b>Tarix:</b> {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
                    await SendDocumentAsync(currentDbPath, chatId, cap);
                    await EditMessageTextAsync(chatId, messageId, "Ô£à <b>Baza fayl─▒ s╔Ön╔Öd olaraq ├ğat─▒n─▒za g├Ând╔Örildi.</b>", TelegramKeyboards.BuildBackToAdminKeyboard());
                }
                else
                {
                    await EditMessageTextAsync(chatId, messageId, "ÔÜá´©Å <b>Baza fayl─▒ tap─▒lmad─▒.</b>", TelegramKeyboards.BuildBackToAdminKeyboard());
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
            else if (data == "cb_close_admin")
            {
                userSettings.IsAdminOpen = false;
                userSettings.LastAdminMessageId = null;
                SaveSettings();
                await DeleteMessageAsync(chatId, messageId);
            }
        }
    }
}