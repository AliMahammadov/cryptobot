using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using CryptoSense.Infrastructure.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CryptoSense.Worker
{
    public class BackgroundMarketScanner : BackgroundService
    {
        private readonly ITelegramBotService _telegramService;
        private readonly IServiceProvider _serviceProvider;
        private readonly AppConfig _config;
        private readonly Dictionary<string, DateTime> _lastAlertSent = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _activeCandleLocks = new();

        public BackgroundMarketScanner(
            ITelegramBotService telegramService,
            IServiceProvider serviceProvider,
            IOptions<AppConfig> config)
        {
            _telegramService = telegramService;
            _serviceProvider = serviceProvider;
            _config = config.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[BackgroundMarketScanner] Clean Architecture v2 Skaner və Nəticə İzləyicisi başladı.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
                        var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
                        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                        var tickers = await marketData.GetTopFuturesTickersAsync(250);
                        var tickerDict = new Dictionary<string, decimal>();
                        foreach (var t in tickers)
                        {
                            tickerDict[t.Symbol] = t.Price;
                        }

                        // =========================================================================
                        // 1. LIVE SIGNAL OUTCOME TRACKER (TP, SL & CANDLE EXPIRY EVALUATION)
                        // =========================================================================
                        var activeSignals = await unitOfWork.Signals.GetOpenTrackedSignalsAsync();

                        foreach (var sig in activeSignals)
                        {
                            if (sig.OutcomeAlertSent || sig.IsClosed) continue;

                            decimal currentPrice = 0;
                            if (!tickerDict.TryGetValue(sig.Symbol, out currentPrice))
                            {
                                if (!tickerDict.TryGetValue("1000" + sig.Symbol, out currentPrice))
                                {
                                    if (sig.Symbol.StartsWith("1000"))
                                    {
                                        tickerDict.TryGetValue(sig.Symbol.Substring(4), out currentPrice);
                                    }
                                }
                            }

                            var duration = sig.Timeframe switch
                            {
                                "1m" => TimeSpan.FromMinutes(1),
                                "3m" => TimeSpan.FromMinutes(3),
                                "5m" => TimeSpan.FromMinutes(5),
                                "15m" => TimeSpan.FromMinutes(15),
                                "1h" => TimeSpan.FromHours(1),
                                "4h" => TimeSpan.FromHours(4),
                                _ => TimeSpan.FromMinutes(15)
                            };

                            var elapsed = DateTime.UtcNow - sig.GeneratedAt;
                            bool isExpired = elapsed >= duration;

                            if (currentPrice == 0 && isExpired)
                            {
                                try
                                {
                                    var klines = await marketData.GetKlinesAsync(sig.Symbol, sig.Timeframe, 2);
                                    if (klines.Count > 0) currentPrice = klines.Last().Close;
                                }
                                catch { }

                                if (currentPrice == 0) currentPrice = sig.EntryPrice;
                            }

                            if (currentPrice > 0)
                            {
                                var isLong = sig.Direction == SignalDirection.Buy || sig.SignalType.Contains("LONG");

                                if (isLong)
                                {
                                    // Long TP3 Hit
                                    if (currentPrice >= sig.TakeProfit3 && !sig.OutcomeAlertSent)
                                    {
                                        sig.Tp3Notified = true;
                                        sig.OutcomeAlertSent = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 3 (TP3) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 3 (TP3)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Long TP2 Hit
                                    else if (currentPrice >= sig.TakeProfit2 && !sig.OutcomeAlertSent)
                                    {
                                        sig.Tp2Notified = true;
                                        sig.OutcomeAlertSent = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 2 (TP2) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Long TP1 Hit
                                    else if (currentPrice >= sig.TakeProfit1 && !sig.OutcomeAlertSent)
                                    {
                                        sig.Tp1Notified = true;
                                        sig.OutcomeAlertSent = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 1 (TP1) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Long Stop Loss Hit
                                    else if (currentPrice <= sig.StopLoss && !sig.OutcomeAlertSent)
                                    {
                                        sig.OutcomeAlertSent = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Failed;
                                        sig.OutcomeStatus = "Stop Loss (SL) (UĞURSUZ) ❌";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Long Candle Expiration
                                    else if (isExpired && !sig.OutcomeAlertSent)
                                    {
                                        sig.OutcomeAlertSent = true;
                                        sig.IsClosed = true;
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        var pct = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                        sig.ResultPercent = pct;

                                        // In strict strategy, if candle timeframe ends without reaching any TP target, the signal failed
                                        sig.Status = SignalStatus.Failed;
                                        sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (Hədəfə Çatmadı) ❌";

                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Bitdi", currentPrice, pct);
                                    }
                                }
                                else // SHORT
                                {
                                    // Short TP3 Hit
                                    if (currentPrice <= sig.TakeProfit3 && !sig.OutcomeAlertSent)
                                    {
                                        sig.Tp3Notified = true;
                                        sig.OutcomeAlertSent = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 3 (TP3) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 3 (TP3)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Short TP2 Hit
                                    else if (currentPrice <= sig.TakeProfit2 && !sig.OutcomeAlertSent)
                                    {
                                        sig.Tp2Notified = true;
                                        sig.OutcomeAlertSent = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 2 (TP2) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Short TP1 Hit
                                    else if (currentPrice <= sig.TakeProfit1 && !sig.OutcomeAlertSent)
                                    {
                                        sig.Tp1Notified = true;
                                        sig.OutcomeAlertSent = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 1 (TP1) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Short Stop Loss Hit
                                    else if (currentPrice >= sig.StopLoss && !sig.OutcomeAlertSent)
                                    {
                                        sig.OutcomeAlertSent = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Failed;
                                        sig.OutcomeStatus = "Stop Loss (SL) (UĞURSUZ) ❌";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        var pct = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                                        if (pct > 0) pct = -Math.Abs(pct);
                                        sig.ResultPercent = pct;
                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL)", currentPrice, pct);
                                    }
                                    // Short Candle Expiration
                                    else if (isExpired && !sig.OutcomeAlertSent)
                                    {
                                        sig.OutcomeAlertSent = true;
                                        sig.IsClosed = true;
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        var pct = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                                        sig.ResultPercent = pct;

                                        // In strict strategy, if candle timeframe ends without reaching any TP target, the signal failed
                                        sig.Status = SignalStatus.Failed;
                                        sig.OutcomeStatus = $"{sig.Timeframe} Müddəti Bitdi (Hədəfə Çatmadı) ❌";

                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Müddəti Bitdi", currentPrice, pct);
                                    }
                                }

                                if (sig.IsClosed)
                                {
                                    _activeCandleLocks.TryRemove($"{sig.Symbol}_{sig.Timeframe}", out _);
                                }
                            }
                        }

                        // =========================================================================
                        // 2. AUTOMATIC SCANNER FOR SUBSCRIBED TIMEFRAMES & COINS
                        // =========================================================================
                        var activeTimeframes = new HashSet<string>();
                        var subscribedCoins = new HashSet<string>();
                        bool anyUserWantsAllCoins = false;

                        foreach (var s in TelegramBotService.UserPreferences.Values)
                        {
                            if (s.IsActive)
                            {
                                if (s.Timeframe == "Hamısı" || s.Timeframe == "Hamisi")
                                {
                                    activeTimeframes.Add("3m");
                                    activeTimeframes.Add("5m");
                                    activeTimeframes.Add("15m");
                                    activeTimeframes.Add("1h");
                                    activeTimeframes.Add("4h");
                                }
                                else
                                {
                                    activeTimeframes.Add(s.Timeframe);
                                }

                                if (s.Coins.Count == 0)
                                {
                                    anyUserWantsAllCoins = true;
                                }
                                else
                                {
                                    foreach (var c in s.Coins) subscribedCoins.Add(c);
                                }
                            }
                        }

                        if (anyUserWantsAllCoins || subscribedCoins.Count == 0)
                        {
                            var defaultCoins = _config.SelectedCoins != null && _config.SelectedCoins.Count > 0
                                ? _config.SelectedCoins
                                : new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT", "NOTUSDT" };
                            foreach (var c in defaultCoins) subscribedCoins.Add(c);
                        }

                        if (activeTimeframes.Count == 0)
                        {
                            activeTimeframes.Add("3m");
                            activeTimeframes.Add("15m");
                        }

                        foreach (var tf in activeTimeframes)
                        {
                            if (stoppingToken.IsCancellationRequested) break;

                            foreach (var sym in subscribedCoins)
                            {
                                if (stoppingToken.IsCancellationRequested) break;

                                // Anti-Spam / Active Candle Lock:
                                // If this coin has an active candle lock on this timeframe, wait until it expires!
                                var lockKey = $"{sym}_{tf}";
                                if (_activeCandleLocks.TryGetValue(lockKey, out var expiry) && DateTime.UtcNow < expiry)
                                {
                                    continue;
                                }

                                bool hasActiveUnclosedSignal = activeSignals.Any(s => s.Symbol == sym && s.Timeframe == tf && !s.IsClosed && s.Status == SignalStatus.Open);
                                if (hasActiveUnclosedSignal)
                                {
                                    continue;
                                }

                                try
                                {
                                    var signal = await signalEngine.AnalyzeCoinAsync(sym, tf, isLiveScan: true);
                                    if (signal.Confidence >= _config.MinConfidenceThreshold && (signal.SignalType.Contains("LONG") || signal.SignalType.Contains("SHORT")))
                                    {
                                        var alertKey = $"{signal.Symbol}_{signal.Timeframe}_{signal.SourceCandleOpenTimeUtc:yyyyMMddHHmmss}";
                                        if (!_lastAlertSent.ContainsKey(alertKey) && !signal.SignalAlertSent)
                                        {
                                            _lastAlertSent[alertKey] = DateTime.UtcNow;
                                            signal.SignalAlertSent = true;

                                            var duration = signal.Timeframe switch
                                            {
                                                "1m" => TimeSpan.FromMinutes(1),
                                                "3m" => TimeSpan.FromMinutes(3),
                                                "5m" => TimeSpan.FromMinutes(5),
                                                "15m" => TimeSpan.FromMinutes(15),
                                                "1h" => TimeSpan.FromHours(1),
                                                "4h" => TimeSpan.FromHours(4),
                                                _ => TimeSpan.FromMinutes(15)
                                            };
                                            _activeCandleLocks[lockKey] = DateTime.UtcNow.Add(duration);
                                            activeSignals.Add(signal);

                                            var dbSig = await unitOfWork.Signals.GetByIdAsync(signal.Id);
                                            if (dbSig != null)
                                            {
                                                dbSig.SignalAlertSent = true;
                                                await unitOfWork.Signals.UpdateAsync(dbSig);
                                                await unitOfWork.SaveChangesAsync(stoppingToken);
                                            }

                                            await _telegramService.SendSignalAlertAsync(signal);
                                        }
                                    }
                                }
                                catch (Exception)
                                {
                                }
                            }
                        }

                        // =========================================================================
                        // 3. PERIODIC LIVENESS HEARTBEAT (EVERY 15 MINUTES IF NO SIGNALS OCCURRED)
                        // =========================================================================
                        var nowUtc = DateTime.UtcNow;
                        foreach (var kvp in TelegramBotService.UserPreferences)
                        {
                            var chatId = kvp.Key;
                            var s = kvp.Value;
                            if (!s.IsActive) continue;

                            var minutesSinceSignal = (nowUtc - s.LastSignalSentUtc).TotalMinutes;
                            var minutesSinceHeartbeat = (nowUtc - s.LastHeartbeatSentUtc).TotalMinutes;

                            if (minutesSinceSignal >= 15 && minutesSinceHeartbeat >= 15)
                            {
                                s.LastHeartbeatSentUtc = nowUtc;
                                var heartbeatMsg = "🟢 <b>Sistem Canlı İzləmədədir (15 Dəqiqəlik Vəziyyət):</b>\n\n" +
                                                   "ℹ️ <i>Son 15 dəqiqə ərzində bazarda 70%+ uğur tələblərinə tam cavab verən risk-təsdiqli yeni siqnal aşkarlanmadı.</i>\n\n" +
                                                   "🎯 <b>Bot 24/7 rejimində bazarı analiz edir.</b> EMA, MA və MACD razılaşması olan yeni şam formalaşan kimi siqnal dərhal sizə göndəriləcək.";
                                await _telegramService.SendMessageAsync(heartbeatMsg, chatId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BackgroundMarketScanner] Scanner loop warning: {ex.Message}");
                }

                await Task.Delay(2500, stoppingToken);
            }
        }
    }
}
