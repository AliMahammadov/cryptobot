using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Application.Services;
using CryptoSense.Domain.Common;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using CryptoSense.Infrastructure.MarketData;
using CryptoSense.Infrastructure.Persistence;
using CryptoSense.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CryptoSense.Worker
{
    public partial class BackgroundMarketScanner : BackgroundService
    {
        private readonly ITelegramBotService _telegramService;
        private readonly IServiceProvider _serviceProvider;
        private readonly LivePriceCache _livePriceCache;
        private readonly BinanceFuturesWsClient _wsClient;
        private readonly AppConfig _config;

        public const int MaxGlobalOpenPositions = BotConstants.Thresholds.MaxGlobalOpenPositions;

        private static int _consecutiveLosses = 0;
        private static DateTime _circuitBreakerUntil = DateTime.MinValue;
        private static DateTime _lastCircuitBreakerAlertSent = DateTime.MinValue;

        private static int _nextSignalNumber = 0;
        private static readonly object _sendLock = new();
        private static readonly SemaphoreSlim _sendSemaphore = new(1, 1);
        private static readonly ConcurrentDictionary<string, DateTime> _lastRestFallbackTime = new();
        private static readonly ConcurrentDictionary<string, DateTime> _coinLastScanTime = new();

        private static readonly ConcurrentDictionary<string, DateTime> _lastAlertSent = new();
        private static readonly ConcurrentDictionary<string, DateTime> _coinCooldowns = new();
        private static readonly ConcurrentDictionary<string, byte> _coinActiveLocks = new();
        private static readonly ConcurrentDictionary<string, byte> _scanningCoins = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastVolatilityAlertSent = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastSymbolAlertTime = new();
        private static readonly object _heartbeatLock = new();
        private static readonly SemaphoreSlim _signalDispatchLock = new(1, 1);
        private static string _lastDailyReportDateBaku = "";

        private static readonly ConcurrentDictionary<string, List<(string Symbol, SignalDirection Direction)>> _hourlyDispatches = new();
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _signalOutcomeSemaphores = new();
        private static readonly ConcurrentDictionary<string, DateTime> _sentOutcomeDeduplication = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastPriceUpdateHandled = new();
        // Overfilter diaqnostika: coin+tf → (confluence%, skipReason)
        private static readonly ConcurrentDictionary<string, (double Confluence, string SkipReason)> _overfilterDiag = new();

        private static readonly ScanTelemetry _hourlyTelemetry = new();
        public static ScanTelemetry LatestTelemetrySnapshot { get; private set; } = new();
        private static string _lastLoggedHourKey = "";
        public static DateTime LastScanUtc { get; set; } = DateTime.MinValue;

        public static void ClearLocks()
        {
            _coinCooldowns.Clear();
            _lastAlertSent.Clear();
            _lastVolatilityAlertSent.Clear();
            _lastSymbolAlertTime.Clear();
            _hourlyDispatches.Clear();
            _signalOutcomeSemaphores.Clear();
            _sentOutcomeDeduplication.Clear();
            _lastPriceUpdateHandled.Clear();
        }

        public static void ResetSignalCounter()
        {
            SignalEngine.ResetSignalCounter();
            lock (_sendLock)
            {
                _nextSignalNumber = 0;
            }
        }

        public BackgroundMarketScanner(
            ITelegramBotService telegramService,
            IServiceProvider serviceProvider,
            LivePriceCache livePriceCache,
            BinanceFuturesWsClient wsClient,
            IOptions<AppConfig> config)
        {
            _telegramService = telegramService;
            _serviceProvider = serviceProvider;
            _livePriceCache = livePriceCache;
            _wsClient = wsClient;
            _config = config.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[BackgroundMarketScanner] Yüksək Dəqiqlikli Skaner və Nəticə İzləyicisi başladı.");

            // Initialize highest signal number from database and perform startup cleanup of orphaned signals
            using (var initScope = _serviceProvider.CreateScope())
            {
                try
                {
                    var uow = initScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    int cleaned = await uow.Signals.CleanupOrphanedSignalsAsync();
                    if (cleaned > 0)
                    {
                        Console.WriteLine($"[BackgroundMarketScanner] Cleaned {cleaned} orphaned signals from database on startup.");
                    }

                    var db = initScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var maxNum = db.Signals.Any(s => s.SignalAlertSent && s.SignalNumber > 0)
                        ? db.Signals.Where(s => s.SignalAlertSent && s.SignalNumber > 0).Max(s => s.SignalNumber)
                        : 0;
                    lock (_sendLock)
                    {
                        _nextSignalNumber = maxNum;
                    }
                    Console.WriteLine($"[BackgroundMarketScanner] _nextSignalNumber initialized to {_nextSignalNumber}");

                    // STARTUP CATCH-UP: Sönülü / deploy ikən SL və ya TP toxunubsa dərhal aşkarla, bağla və Telegram nəticə göndər
                    await PerformStartupCatchUpAsync(uow, initScope.ServiceProvider.GetRequiredService<IMarketDataProvider>());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BackgroundMarketScanner] Error initializing signal number / cleanup / catch-up: {ex.Message}");
                }
            }

            // Pre-subscribe Default 40 institutional coins and active portfolio coins to WebSocket stream on startup
            foreach (var coin in TelegramBotService.Default40Coins)
            {
                _wsClient.Subscribe(coin);
            }
            foreach (var pref in TelegramBotService.UserPreferences.Values)
            {
                if (pref.IsActive && pref.Coins != null)
                {
                    foreach (var c in pref.Coins)
                    {
                        var norm = c.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? c.ToUpperInvariant() : c.ToUpperInvariant() + "USDT";
                        _wsClient.Subscribe(norm);
                    }
                }
            }

            // Start WebSocket client independently
            _wsClient.Start(stoppingToken);

            // Wait until WS is healthy or seeded
            await WaitUntilWsHealthyAsync(stoppingToken);

            // Perform 1-time Market Catch-Up Scan & Send Boot Briefing to SuperAdmin
            await PerformStartupMarketCatchUpAsync(stoppingToken);

            // 1. DEDICATED FAST OUTCOME TRACKER (Evaluates TP/SL and Expirations every 2 seconds)
            var outcomeTrackerTask = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await TrackActiveSignalOutcomesAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[OutcomeTracker] Error: {ex.Message}");
                    }
                    await Task.Delay(2000, stoppingToken);
                }
            }, stoppingToken);

            // 2. CONTINUOUS HIGH-CONVICTION MARKET SCANNER
            var marketScannerTask = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await ScanMarketSignalsAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MarketScanner] Scanner loop warning: {ex.Message}");
                    }
                    await Task.Delay(5000, stoppingToken);
                }
            }, stoppingToken);

            await Task.WhenAll(outcomeTrackerTask, marketScannerTask);
        }
    }
}
