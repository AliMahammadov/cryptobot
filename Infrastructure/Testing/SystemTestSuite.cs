using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CryptoSense.Application.DTOs;
using CryptoSense.Application.Interfaces;
using CryptoSense.Domain.Entities;
using CryptoSense.Domain.Enums;
using CryptoSense.Domain.Interfaces;
using CryptoSense.Infrastructure.Telegram;

namespace CryptoSense.Infrastructure.Testing
{
    public class SystemTestSuite
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IUserManagerService _userManager;
        private readonly IIndicatorEngine _indicatorEngine;
        private readonly ISignalEngine _signalEngine;
        private readonly IMarketDataProvider _marketData;
        private readonly INewsService _newsService;

        public SystemTestSuite(
            IUnitOfWork unitOfWork,
            IUserManagerService userManager,
            IIndicatorEngine indicatorEngine,
            ISignalEngine signalEngine,
            IMarketDataProvider marketData,
            INewsService newsService)
        {
            _unitOfWork = unitOfWork;
            _userManager = userManager;
            _indicatorEngine = indicatorEngine;
            _signalEngine = signalEngine;
            _marketData = marketData;
            _newsService = newsService;
        }

        public async Task RunAllTestsAsync()
        {
            Console.WriteLine("\n========================================================");
            Console.WriteLine("🧪 SENIOR QA & DEVELOPER SYSTEM TEST SUITE BAŞLADI");
            Console.WriteLine("========================================================\n");

            int passed = 0;
            int failed = 0;

            async Task AssertTest(string testName, Func<Task<bool>> testFunc)
            {
                try
                {
                    bool result = await testFunc();
                    if (result)
                    {
                        Console.WriteLine($"✅ [PASS] {testName}");
                        passed++;
                    }
                    else
                    {
                        Console.WriteLine($"❌ [FAIL] {testName}");
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [FAIL] {testName} - Exception: {ex.Message}");
                    failed++;
                }
            }

            // 1. Indicator Engine Tests
            await AssertTest("Test 1: Indicator Engine - RSI & EMA Math Calculation", () =>
            {
                var samplePrices = new List<decimal> { 100, 102, 101, 103, 105, 104, 107, 108, 110, 109, 112, 114, 113, 115, 117, 116, 119, 120, 122, 121, 125 };
                var rsi = _indicatorEngine.CalculateRsi(samplePrices, 14);
                var ema9 = _indicatorEngine.CalculateEma(samplePrices, 9);
                return Task.FromResult(rsi > 50 && rsi <= 100 && ema9 > 110);
            });

            await AssertTest("Test 2: Indicator Engine - Multi-Indicator Confluence Scoring (0-100 scale)", () =>
            {
                var klines = new List<Kline>();
                var baseTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (50 * 60 * 1000);
                for (int i = 0; i < 50; i++)
                {
                    decimal price = 50000m + (i * 150m);
                    klines.Add(new Kline
                    {
                        OpenTime = baseTime + (i * 60 * 1000),
                        Open = price - 50,
                        High = price + 100,
                        Low = price - 80,
                        Close = price,
                        Volume = 1000 + (i * 20)
                    });
                }
                var indicators = _indicatorEngine.CalculateIndicators(klines);
                return Task.FromResult(indicators.ConfluenceScore >= 0 && indicators.ConfluenceScore <= 100 && indicators.TrendScore >= 0);
            });

            await AssertTest("Test 2b: Indicator Engine - Bearish Trend & SuperTrend Detection", () =>
            {
                var klines = new List<Kline>();
                var baseTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (50 * 60 * 1000);
                for (int i = 0; i < 50; i++)
                {
                    decimal price = 50000m - (i * 150m);
                    klines.Add(new Kline
                    {
                        OpenTime = baseTime + (i * 60 * 1000),
                        Open = price + 50,
                        High = price + 80,
                        Low = price - 100,
                        Close = price,
                        Volume = 1000 + (i * 20)
                    });
                }
                var indicators = _indicatorEngine.CalculateIndicators(klines);
                return Task.FromResult(indicators.SuperTrendVote == IndicatorVote.Bearish && indicators.EmaVote == IndicatorVote.Bearish);
            });

            // 2. User Manager & Auth Tests
            await AssertTest("Test 3: Authentication - SuperAdmin Login ('Ali' / '23031999Am')", async () =>
            {
                var (success, user) = await _userManager.ValidateLoginAsync("Ali", "23031999Am", 1219998176, "1219998176");
                return success && user != null && user.Role == UserRole.Admin;
            });

            await AssertTest("Test 4: Authentication - SuperAdmin Invalid Password Rejection", async () =>
            {
                var (success, _) = await _userManager.ValidateLoginAsync("Ali", "WrongPassword123");
                return !success;
            });

            await AssertTest("Test 5: User Management - Create, Login, Reset Password, Soft Delete", async () =>
            {
                string testUser = "testuser_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                string initialPass = "Pass123!";
                string newPass = "NewPass456!";

                // Create
                bool created = await _userManager.CreateUserAsync(testUser, initialPass);
                if (!created) return false;

                // Login
                var (loginSuccess, user) = await _userManager.ValidateLoginAsync(testUser, initialPass);
                if (!loginSuccess || user == null) return false;

                // Reset Password
                bool reset = await _userManager.ResetPasswordAsync(testUser, newPass);
                if (!reset) return false;

                // Validate New Password
                var (newLoginSuccess, _) = await _userManager.ValidateLoginAsync(testUser, newPass);
                if (!newLoginSuccess) return false;

                // Delete
                bool deleted = await _userManager.DeleteUserAsync(testUser);
                if (!deleted) return false;

                // Validate Deleted User Cannot Login
                var (deletedLoginSuccess, _) = await _userManager.ValidateLoginAsync(testUser, newPass);
                return !deletedLoginSuccess;
            });

            // 3. Signal Engine & Deduplication Tests
            await AssertTest("Test 6: Signal Engine - Live Analysis & ATR Sizing", async () =>
            {
                var signal = await _signalEngine.AnalyzeCoinAsync("BTCUSDT", "15m", isLiveScan: false);
                return !string.IsNullOrEmpty(signal.Symbol) && signal.EntryPrice > 0 && signal.TakeProfit1 > 0 && signal.StopLoss > 0;
            });

            await AssertTest("Test 7: Signal Engine - Deduplication on Same Candle", async () =>
            {
                var signal1 = await _signalEngine.AnalyzeCoinAsync("ETHUSDT", "15m", isLiveScan: true);
                var signal2 = await _signalEngine.AnalyzeCoinAsync("ETHUSDT", "15m", isLiveScan: true);
                return signal1.SourceCandleOpenTimeUtc == signal2.SourceCandleOpenTimeUtc && signal1.SignalNumber == signal2.SignalNumber;
            });

            // 4. Live Market Integration Tests
            await AssertTest("Test 8: Market Data Provider - Binance Futures Live Tickers", async () =>
            {
                var tickers = await _marketData.GetTopFuturesTickersAsync(10);
                return tickers.Count > 0 && tickers[0].Price > 0;
            });

            await AssertTest("Test 9: BTC Compass - Live Bitcoin Trend Compass", async () =>
            {
                var compass = await _signalEngine.GetBtcCompassAsync();
                return compass.Price > 0 && compass.BullishScore >= 0 && compass.BullishScore <= 100;
            });

            await AssertTest("Test 10: News Service - Crypto Sentiment & RSS Translation", async () =>
            {
                var news = await _newsService.GetNewsAndSentimentAsync();
                return news.LatestNews.Count > 0 && !string.IsNullOrEmpty(news.Status);
            });

            // 5. Telegram Formatter Tests
            await AssertTest("Test 11: Telegram Message Formatter - Signal HTML Validation", () =>
            {
                var sampleSignal = new FuturesSignal
                {
                    SignalNumber = 200,
                    Symbol = "SOLUSDT",
                    Direction = SignalDirection.Buy,
                    Timeframe = "3m",
                    EntryPrice = 145.50m,
                    CurrentPrice = 145.50m,
                    TakeProfit1 = 147.20m,
                    TakeProfit2 = 148.50m,
                    TakeProfit3 = 150.00m,
                    StopLoss = 143.80m,
                    ConfluenceScore = 88.5m,
                    AnalysisReasons = new List<string> { "RSI Bullish", "EMA Trend Up" }
                };

                var formatted = TelegramMessageFormatter.FormatSignalAlert(sampleSignal, 1);
                return Task.FromResult(formatted.Contains("#1 🟢 <b>SİQNAL</b>") && 
                       formatted.Contains("SOL") && 
                       formatted.Contains("Confluence Razılaşma Balı"));
            });

            await AssertTest("Test 12: Telegram Message Formatter - Outcome Report Validation", () =>
            {
                var sampleSignal = new FuturesSignal
                {
                    SignalNumber = 200,
                    Symbol = "SOLUSDT",
                    Direction = SignalDirection.Buy,
                    Timeframe = "3m",
                    EntryPrice = 145.50m
                };

                var formatted = TelegramMessageFormatter.FormatOutcomeAlert(sampleSignal, 1, "Hədəf 1 (TP1)", 147.20m, 1.17m);
                return Task.FromResult(formatted.Contains("#1 NƏTİCƏ HESABATI") && formatted.Contains("+1.17%") && formatted.Contains("UĞURLU"));
            });

            // 6. Retest & Pullback Signal Logic Test
            await AssertTest("Test 13: Signal Engine - Retest & Pullback Model on 3m/5m", async () =>
            {
                var sig3m = await _signalEngine.AnalyzeCoinAsync("BTCUSDT", "3m", isLiveScan: false);
                var sig5m = await _signalEngine.AnalyzeCoinAsync("ETHUSDT", "5m", isLiveScan: false);
                return sig3m.CurrentPrice > 0 && sig5m.CurrentPrice > 0 && 
                       (sig3m.TakeProfit1 > 0 || sig3m.SignalType.Contains("NEYTRAL"));
            });

            // 7. Live BTC Compass & Dominance Format Test
            await AssertTest("Test 14: BTC Compass - Live Dominance & Format Validation", async () =>
            {
                var compass = await _signalEngine.GetBtcCompassAsync();
                var formatted = TelegramMessageFormatter.FormatBtcCompass(compass);
                return formatted.Contains("Bitcoin Makro Bazar Kompası") && 
                       formatted.Contains("CANLI QİYMƏT") && 
                       compass.Price > 0;
            });

            // 8. Admin User Operations Test (Create, List, Reset Password, Delete)
            await AssertTest("Test 15: Admin Operations - User CRUD & Security Workflow", async () =>
            {
                var testUser = "qa_tester_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                var created = await _userManager.CreateUserAsync(testUser, "pass123");
                if (!created) return false;

                var all = await _userManager.GetAllUsersAsync();
                if (!all.Any(u => u.Username == testUser)) return false;

                var reset = await _userManager.ResetPasswordAsync(testUser, "newpass456");
                if (!reset) return false;

                var deleted = await _userManager.DeleteUserAsync(testUser);
                return deleted;
            });

            // 9. Deep Coin Stats Breakdown Test
            await AssertTest("Test 16: Coin Performance Breakdown - Active & Closed Trades", async () =>
            {
                var monitored = new List<string> { "BTCUSDT", "ETHUSDT", "SOLUSDT" };
                var breakdown = await _signalEngine.GetCoinPerformanceBreakdownAsync(monitored);
                var formatted = TelegramMessageFormatter.FormatCoinPerformanceBreakdown(breakdown, monitored);
                return breakdown.Count >= 3 && formatted.Contains("Qlobal Win-Rate");
            });

            // 10. User Coin Selection & Comma-Separated Deletion Logic Test
            await AssertTest("Test 17: User Coin Selection - Multi-Coin Add and Comma-Separated Deletion", () =>
            {
                var settings = new UserSettings();
                var toAdd = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "DOGEUSDT" };
                foreach (var c in toAdd) settings.Coins.Add(c);

                if (settings.Coins.Count != 4) return Task.FromResult(false);

                // Simulate comma-separated deletion: "ETH, DOGE"
                var delInput = "ETH, DOGE";
                var parts = delInput.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var p in parts)
                {
                    var coin = p.ToUpper();
                    if (!coin.EndsWith("USDT")) coin += "USDT";
                    settings.Coins.Remove(coin);
                }

                return Task.FromResult(settings.Coins.Count == 2 && settings.Coins.Contains("BTCUSDT") && settings.Coins.Contains("SOLUSDT"));
            });

            // 11. Database Signal Reset & Clean Slate Verification
            await AssertTest("Test 18: Reset Workflow - ClearAllSignalsAsync & Stats Reset to Zero", async () =>
            {
                await _signalEngine.ClearAllSignalsAsync();
                var stats = await _signalEngine.GetPerformanceStatsAsync();
                var active = await _signalEngine.GetTrackedActiveSignalsAsync();
                return stats.TotalSignals == 0 && stats.OpenSignals == 0 && stats.SuccessSignals == 0 && stats.FailedSignals == 0 && active.Count == 0;
            });

            // 12. SignalAlertSent Filter Verification (Only delivered signals tracked)
            await AssertTest("Test 19: SignalAlertSent Filter Integrity", async () =>
            {
                var sig = new FuturesSignal
                {
                    Symbol = "BTCUSDT",
                    Timeframe = "3m",
                    Direction = SignalDirection.Buy,
                    SignalType = "GÜCLÜ LONG (ALIŞ) 🟢",
                    EntryPrice = 85000,
                    TakeProfit1 = 86000,
                    TakeProfit2 = 87000,
                    TakeProfit3 = 88000,
                    StopLoss = 84000,
                    Confidence = 90,
                    SignalAlertSent = true // Delivered
                };

                await _unitOfWork.Signals.AddAsync(sig);
                await _unitOfWork.SaveChangesAsync();

                var active = await _signalEngine.GetTrackedActiveSignalsAsync();
                bool found = active.Any(s => s.Symbol == "BTCUSDT" && s.SignalAlertSent);

                // Clean up
                await _signalEngine.ClearAllSignalsAsync();
                var afterClear = await _signalEngine.GetTrackedActiveSignalsAsync();

                return found && afterClear.Count == 0;
            });

            Console.WriteLine("\n========================================================");
            Console.WriteLine($"🏁 TEST NƏTİCƏLƏRİ: {passed} UĞURLU (PASS), {failed} UĞURSUZ (FAIL)");
            Console.WriteLine("========================================================\n");

            if (failed > 0)
            {
                throw new Exception($"Testlərdən {failed} ədədi uğursuz oldu!");
            }
        }
    }
}
