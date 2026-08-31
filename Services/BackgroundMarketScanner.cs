using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CryptoSense.Services
{
    public class BackgroundMarketScanner : BackgroundService
    {
        private readonly BinanceFuturesService _binanceService;
        private readonly SignalEngine _signalEngine;
        private readonly TelegramBotService _telegramService;
        private readonly AppConfig _config;
        private readonly Dictionary<string, DateTime> _lastAlertSent = new();

        public BackgroundMarketScanner(
            BinanceFuturesService binanceService,
            SignalEngine signalEngine,
            TelegramBotService telegramService,
            IOptions<AppConfig> config)
        {
            _binanceService = binanceService;
            _signalEngine = signalEngine;
            _telegramService = telegramService;
            _config = config.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("KriptoBot 24/7 20-Indikatorlu Kvantitativ Skaner ve Netice Izleyici basladi.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var tickers = await _binanceService.GetTopFuturesTickersAsync(30);
                    var tickerDict = tickers.ToDictionary(t => t.Symbol, t => t.Price);

                    // 1. LIVE SIGNAL OUTCOME TRACKER (TP, SL & EXACT CANDLE EXPIRATION)
                    var activeSignals = SignalEngine.GetTrackedActiveSignals();
                    foreach (var sig in activeSignals)
                    {
                        if (tickerDict.TryGetValue(sig.Symbol, out var currentPrice))
                        {
                            var isLong = sig.SignalType.Contains("LONG");
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
                                    sig.ClosedAt = DateTime.UtcNow;
                                    var pct = Math.Round(((currentPrice - sig.EntryLow) / sig.EntryLow) * 100, 2);
                                    SignalEngine.PersistSignals();
                                    await _telegramService.SendOutcomeAlertAsync(sig, "H\u0259d\u0259f 3 (TP3)", currentPrice, pct);
                                }
                                // Long TP2 Hit
                                else if (currentPrice >= sig.TakeProfit2 && !sig.Tp2Notified)
                                {
                                    sig.Tp2Notified = true;
                                    sig.IsClosed = true;
                                    sig.ClosedAt = DateTime.UtcNow;
                                    var pct = Math.Round(((currentPrice - sig.EntryLow) / sig.EntryLow) * 100, 2);
                                    SignalEngine.PersistSignals();
                                    await _telegramService.SendOutcomeAlertAsync(sig, "H\u0259d\u0259f 2 (TP2)", currentPrice, pct);
                                }
                                // Long TP1 Hit
                                else if (currentPrice >= sig.TakeProfit1 && !sig.Tp1Notified)
                                {
                                    sig.Tp1Notified = true;
                                    sig.IsClosed = true;
                                    sig.ClosedAt = DateTime.UtcNow;
                                    var pct = Math.Round(((currentPrice - sig.EntryLow) / sig.EntryLow) * 100, 2);
                                    SignalEngine.PersistSignals();
                                    await _telegramService.SendOutcomeAlertAsync(sig, "H\u0259d\u0259f 1 (TP1)", currentPrice, pct);
                                }
                                // Long Stop Loss Hit (UGURSUZ)
                                else if (currentPrice <= sig.StopLoss && !sig.IsClosed)
                                {
                                    sig.IsClosed = true;
                                    sig.ClosedAt = DateTime.UtcNow;
                                    var pct = Math.Round(((currentPrice - sig.EntryLow) / sig.EntryLow) * 100, 2);
                                    SignalEngine.PersistSignals();
                                    await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL)", currentPrice, pct);
                                }
                                // EXACT TIMEFRAME EXPIRATION (3m, 5m, 15m, 1h, 4h) -> REPORT FINAL CANDLE OUTCOME
                                else if (isExpired && !sig.IsClosed)
                                {
                                    sig.IsClosed = true;
                                    sig.ClosedAt = DateTime.UtcNow;
                                    var pct = Math.Round(((currentPrice - sig.EntryLow) / sig.EntryLow) * 100, 2);
                                    var outcomeName = pct >= 0 ? $"{sig.Timeframe} Vaxt\u0131 Tamamland\u0131 (M\u00FCsb\u0259t)" : $"{sig.Timeframe} Vaxt\u0131 Tamamland\u0131 (M\u0259nfi)";
                                    SignalEngine.PersistSignals();
                                    await _telegramService.SendOutcomeAlertAsync(sig, outcomeName, currentPrice, pct);
                                }
                            }
                            else if (sig.SignalType.Contains("SHORT"))
                            {
                                // Short TP3 Hit
                                if (currentPrice <= sig.TakeProfit3 && !sig.Tp3Notified)
                                {
                                    sig.Tp3Notified = true;
                                    sig.IsClosed = true;
                                    sig.ClosedAt = DateTime.UtcNow;
                                    var pct = Math.Round(((sig.EntryHigh - currentPrice) / sig.EntryHigh) * 100, 2);
                                    SignalEngine.PersistSignals();
                                    await _telegramService.SendOutcomeAlertAsync(sig, "H\u0259d\u0259f 3 (TP3)", currentPrice, pct);
                                }
                                // Short TP2 Hit
                                else if (currentPrice <= sig.TakeProfit2 && !sig.Tp2Notified)
                                {
                                    sig.Tp2Notified = true;
                                    sig.IsClosed = true;
                                    sig.ClosedAt = DateTime.UtcNow;
                                    var pct = Math.Round(((sig.EntryHigh - currentPrice) / sig.EntryHigh) * 100, 2);
                                    SignalEngine.PersistSignals();
                                    await _telegramService.SendOutcomeAlertAsync(sig, "H\u0259d\u0259f 2 (TP2)", currentPrice, pct);
                                }
                                // Short TP1 Hit
                                else if (currentPrice <= sig.TakeProfit1 && !sig.Tp1Notified)
                                {
                                    sig.Tp1Notified = true;
                                    sig.IsClosed = true;
                                    sig.ClosedAt = DateTime.UtcNow;
                                    var pct = Math.Round(((sig.EntryHigh - currentPrice) / sig.EntryHigh) * 100, 2);
                                    SignalEngine.PersistSignals();
                                    await _telegramService.SendOutcomeAlertAsync(sig, "H\u0259d\u0259f 1 (TP1)", currentPrice, pct);
                                }
                                // Short Stop Loss Hit (UGURSUZ)
                                else if (currentPrice >= sig.StopLoss && !sig.IsClosed)
                                {
                                    sig.IsClosed = true;
                                    sig.ClosedAt = DateTime.UtcNow;
                                    var pct = Math.Round(((sig.EntryHigh - currentPrice) / sig.EntryHigh) * 100, 2);
                                    SignalEngine.PersistSignals();
                                    await _telegramService.SendOutcomeAlertAsync(sig, "Stop Loss (SL)", currentPrice, pct);
                                }
                                // EXACT TIMEFRAME EXPIRATION (3m, 5m, 15m, 1h, 4h) -> REPORT FINAL CANDLE OUTCOME
                                else if (isExpired && !sig.IsClosed)
                                {
                                    sig.IsClosed = true;
                                    sig.ClosedAt = DateTime.UtcNow;
                                    var pct = Math.Round(((sig.EntryHigh - currentPrice) / sig.EntryHigh) * 100, 2);
                                    var outcomeName = pct >= 0 ? $"{sig.Timeframe} Vaxt\u0131 Tamamland\u0131 (M\u00FCsb\u0259t)" : $"{sig.Timeframe} Vaxt\u0131 Tamamland\u0131 (M\u0259nfi)";
                                    SignalEngine.PersistSignals();
                                    await _telegramService.SendOutcomeAlertAsync(sig, outcomeName, currentPrice, pct);
                                }
                            }
                        }
                    }

                    // 2. AUTOMATIC 24/7 SCAN FOR FRESH 90%+ SIGNALS
                    var activeTimeframes = new HashSet<string> { "15m", "5m", "3m", "1h" };
                    foreach (var s in TelegramBotService.UserPreferences.Values)
                    {
                        if (s.IsActive)
                        {
                            if (s.Timeframe == "Ham\u0131s\u0131" || s.Timeframe == "Hamisi")
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
                        }
                    }

                    var topCoins = new[] { "SOLUSDT", "BTCUSDT", "ETHUSDT", "DOGEUSDT", "XRPUSDT", "BNBUSDT", "SUIUSDT", "PEPEUSDT", "AVAXUSDT", "NEARUSDT", "LINKUSDT", "ADAUSDT" };

                    foreach (var tf in activeTimeframes)
                    {
                        if (stoppingToken.IsCancellationRequested) break;

                        foreach (var sym in topCoins)
                        {
                            if (stoppingToken.IsCancellationRequested) break;

                            var signal = await _signalEngine.AnalyzeCoinAsync(sym, tf);
                            if (signal.Confidence >= 88 && (signal.SignalType.Contains("LONG") || signal.SignalType.Contains("SHORT")))
                            {
                                var alertKey = $"{signal.Symbol}_{signal.Timeframe}";
                                if (!_lastAlertSent.TryGetValue(alertKey, out var lastTime) || 
                                    DateTime.UtcNow - lastTime > TimeSpan.FromMinutes(8))
                                {
                                    _lastAlertSent[alertKey] = DateTime.UtcNow;
                                    await _telegramService.SendSignalAlertAsync(signal);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Scanner error: {ex.Message}");
                }

                await Task.Delay(3000, stoppingToken);
            }
        }
    }
}