using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Data;
using CryptoSense.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CryptoSense.Services
{
    public class BackgroundMarketScanner : BackgroundService
    {
        private readonly BinanceFuturesService _binanceService;
        private readonly SignalEngine _signalEngine;
        private readonly TelegramBotService _telegramService;
        private readonly IServiceProvider _serviceProvider;
        private readonly AppConfig _config;
        private readonly Dictionary<string, DateTime> _lastAlertSent = new();

        public BackgroundMarketScanner(
            BinanceFuturesService binanceService,
            SignalEngine signalEngine,
            TelegramBotService telegramService,
            IServiceProvider serviceProvider,
            IOptions<AppConfig> config)
        {
            _binanceService = binanceService;
            _signalEngine = signalEngine;
            _telegramService = telegramService;
            _serviceProvider = serviceProvider;
            _config = config.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("KriptoBot v2 Kvantitativ Skaner və Nəticə İzləyicisi başladı.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var tickers = await _binanceService.GetTopFuturesTickersAsync(35);
                    var tickerDict = tickers.ToDictionary(t => t.Symbol, t => t.Price);

                    // =========================================================================
                    // 1. LIVE SIGNAL OUTCOME TRACKER (TP, SL & CANDLE EXPIRY EVALUATION)
                    // =========================================================================
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var activeSignals = await db.Signals
                            .Where(s => s.Status == SignalStatus.Open && !s.IsClosed)
                            .ToListAsync(stoppingToken);

                        foreach (var sig in activeSignals)
                        {
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
                                    if (currentPrice >= sig.TakeProfit3 && !sig.Tp3Notified)
                                    {
                                        sig.Tp3Notified = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 3 (TP3) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                        await db.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 3 (TP3)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Long TP2 Hit
                                    else if (currentPrice >= sig.TakeProfit2 && !sig.Tp2Notified)
                                    {
                                        sig.Tp2Notified = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 2 (TP2) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                        await db.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Long TP1 Hit
                                    else if (currentPrice >= sig.TakeProfit1 && !sig.Tp1Notified)
                                    {
                                        sig.Tp1Notified = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 1 (TP1) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                        await db.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Long Stop Loss Hit (UGURSUZ)
                                    else if (currentPrice <= sig.StopLoss && !sig.IsClosed)
                                    {
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Failed;
                                        sig.OutcomeStatus = "Stop Loss (SL) (UĞURSUZ) ❌";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((currentPrice - sig.EntryPrice) / sig.EntryPrice) * 100, 2);
                                        await db.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Long Candle Expiration
                                    else if (isExpired && !sig.IsClosed)
                                    {
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

                                        await db.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Vaxtı Tamamlandı", currentPrice, pct);
                                    }
                                }
                                else // SHORT
                                {
                                    // Short TP3 Hit (Price drops to or below TP3)
                                    if (currentPrice <= sig.TakeProfit3 && !sig.Tp3Notified)
                                    {
                                        sig.Tp3Notified = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 3 (TP3) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                                        await db.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 3 (TP3)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Short TP2 Hit
                                    else if (currentPrice <= sig.TakeProfit2 && !sig.Tp2Notified)
                                    {
                                        sig.Tp2Notified = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 2 (TP2) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                                        await db.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 2 (TP2)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Short TP1 Hit
                                    else if (currentPrice <= sig.TakeProfit1 && !sig.Tp1Notified)
                                    {
                                        sig.Tp1Notified = true;
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Success;
                                        sig.OutcomeStatus = "Hədəf 1 (TP1) (UĞURLU) ✅";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        sig.ResultPercent = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                                        await db.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Hədəf 1 (TP1)", currentPrice, sig.ResultPercent.Value);
                                    }
                                    // Short Stop Loss Hit (Price rises to or above SL -> STRICT LOSS & FAILED!)
                                    else if (currentPrice >= sig.StopLoss && !sig.IsClosed)
                                    {
                                        sig.IsClosed = true;
                                        sig.Status = SignalStatus.Failed;
                                        sig.OutcomeStatus = "Stop Loss (SL) (UĞURSUZ) ❌";
                                        sig.ClosePrice = currentPrice;
                                        sig.ClosedAt = DateTime.UtcNow;
                                        // PnL for short when price rose is strictly negative
                                        var pct = Math.Round(((sig.EntryPrice - currentPrice) / sig.EntryPrice) * 100, 2);
                                        if (pct > 0) pct = -Math.Abs(pct);
                                        sig.ResultPercent = pct;
                                        await db.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL)", currentPrice, pct);
                                    }
                                    // Short Candle Expiration
                                    else if (isExpired && !sig.IsClosed)
                                    {
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

                                        await db.SaveChangesAsync(stoppingToken);
                                        await _telegramService.SendOutcomeAlertAsync(sig, $"{sig.Timeframe} Vaxtı Tamamlandı", currentPrice, pct);
                                    }
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
                            }
                            else
                            {
                                activeTimeframes.Add(s.Timeframe);
                            }

                            foreach (var c in s.Coins) subscribedCoins.Add(c);
                        }
                    }

                    // Baseline coins if users selected empty
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
                                var signal = await _signalEngine.AnalyzeCoinAsync(sym, tf, isLiveScan: true);
                                if (signal.Confidence >= _config.MinConfidenceThreshold && (signal.SignalType.Contains("LONG") || signal.SignalType.Contains("SHORT")))
                                {
                                    var alertKey = $"{signal.Symbol}_{signal.Timeframe}_{signal.SourceCandleOpenTimeUtc:yyyyMMddHHmmss}";
                                    if (!_lastAlertSent.ContainsKey(alertKey))
                                    {
                                        _lastAlertSent[alertKey] = DateTime.UtcNow;
                                        await _telegramService.SendSignalAlertAsync(signal);
                                    }
                                }
                            }
                            catch (Exception coinEx)
                            {
                                Console.WriteLine($"Coin scan error ({sym}): {coinEx.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Scanner error: {ex.Message}");
                }

                await Task.Delay(2500, stoppingToken);
            }
        }
    }
}
