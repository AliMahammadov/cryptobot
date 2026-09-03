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

                        var tickers = await marketData.GetTopFuturesTickersAsync(35);
                        var tickerDict = tickers.ToDictionary(t => t.Symbol, t => t.Price);

                        // =========================================================================
                        // 1. LIVE SIGNAL OUTCOME TRACKER (TP, SL & CANDLE EXPIRY EVALUATION)
                        // =========================================================================
                        var activeSignals = await unitOfWork.Signals.GetOpenTrackedSignalsAsync();

                        foreach (var sig in activeSignals)
                        {
                            if (sig.OutcomeAlertSent || sig.IsClosed) continue;

                            if (tickerDict.TryGetValue(sig.Symbol, out var currentPrice))
                            {
                                var isLong = sig.Direction == SignalDirection.Buy || sig.SignalType.Contains("LONG");
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

                                        if (pct > 0.05m)
                                        {
                                            sig.Status = SignalStatus.Success;
                                            sig.OutcomeStatus = $"{sig.Timeframe} Vaxtı Tamamlandı (MÜSBƏT) ✅";
                                        }
                                        else if (pct < -0.05m)
                                        {
                                            sig.Status = SignalStatus.Failed;
                                            sig.OutcomeStatus = $"{sig.Timeframe} Vaxtı Tamamlandı (UĞURSUZ) ❌";
                                        }
                                        else
                                        {
                                            sig.Status = SignalStatus.Neutral;
                                            sig.OutcomeStatus = $"{sig.Timeframe} Vaxtı Tamamlandı (NEYTRAL) ⚪";
                                        }

                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Vaxtı Tamamlandı", currentPrice, pct);
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

                                        if (pct > 0.05m)
                                        {
                                            sig.Status = SignalStatus.Success;
                                            sig.OutcomeStatus = $"{sig.Timeframe} Vaxtı Tamamlandı (MÜSBƏT) ✅";
                                        }
                                        else if (pct < -0.05m)
                                        {
                                            sig.Status = SignalStatus.Failed;
                                            sig.OutcomeStatus = $"{sig.Timeframe} Vaxtı Tamamlandı (UĞURSUZ) ❌";
                                        }
                                        else
                                        {
                                            sig.Status = SignalStatus.Neutral;
                                            sig.OutcomeStatus = $"{sig.Timeframe} Vaxtı Tamamlandı (NEYTRAL) ⚪";
                                        }

                                        await unitOfWork.Signals.UpdateAsync(sig);
                                        await unitOfWork.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Vaxtı Tamamlandı", currentPrice, pct);
                                    }
                                }
                            }
                        }

                        // =========================================================================
                        // 2. AUTOMATIC SCANNER FOR SUBSCRIBED TIMEFRAMES & COINS
                        // =========================================================================
                        var activeTimeframes = new HashSet<string>();
                        var subscribedCoins = new HashSet<string>();

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

                                foreach (var c in s.Coins) subscribedCoins.Add(c);
                            }
                        }

                        if (subscribedCoins.Count == 0)
                        {
                            var defaultCoins = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT" };
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
                                catch (Exception coinEx)
                                {
                                    Console.WriteLine($"[BackgroundMarketScanner] Coin scan warning ({sym}): {coinEx.Message}");
                                }
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
