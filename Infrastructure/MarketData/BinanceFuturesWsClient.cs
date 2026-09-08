using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CryptoSense.Application.Services;

namespace CryptoSense.Infrastructure.MarketData
{
    public class BinanceFuturesWsClient : IDisposable
    {
        private readonly LivePriceCache _priceCache;
        private readonly ConcurrentDictionary<string, byte> _subscribedSymbols = new(StringComparer.OrdinalIgnoreCase);
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private int _requestId = 1;
        private bool _disposed;

        public bool IsConnected => _ws?.State == WebSocketState.Open;

        public BinanceFuturesWsClient(LivePriceCache priceCache)
        {
            _priceCache = priceCache;
        }

        public void Start(CancellationToken parentToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _ = Task.Run(() => ConnectionLoopAsync(_cts.Token), _cts.Token);
        }

        public void Subscribe(string symbol)
        {
            _ = SubscribeAsync(symbol);
        }

        public async Task SubscribeAsync(string symbol)
        {
            var cleanSym = symbol.ToLowerInvariant();
            if (_subscribedSymbols.TryAdd(cleanSym, 1))
            {
                if (IsConnected)
                {
                    await SendSubscriptionCommandAsync("SUBSCRIBE", new[] { $"{cleanSym}@aggTrade", $"{cleanSym}@bookTicker" });
                }
            }
        }

        public void Unsubscribe(string symbol)
        {
            _ = UnsubscribeAsync(symbol);
        }

        public async Task UnsubscribeAsync(string symbol)
        {
            var cleanSym = symbol.ToLowerInvariant();
            if (_subscribedSymbols.TryRemove(cleanSym, out _))
            {
                if (IsConnected)
                {
                    await SendSubscriptionCommandAsync("UNSUBSCRIBE", new[] { $"{cleanSym}@aggTrade", $"{cleanSym}@bookTicker" });
                }
            }
        }

        private async Task SendSubscriptionCommandAsync(string method, IEnumerable<string> streamParams)
        {
            try
            {
                await _sendLock.WaitAsync();
                if (_ws?.State == WebSocketState.Open)
                {
                    var payload = new
                    {
                        method = method,
                        @params = streamParams,
                        id = Interlocked.Increment(ref _requestId)
                    };
                    var json = JsonSerializer.Serialize(payload);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BinanceWs] Subscription error: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ConnectionLoopAsync(CancellationToken token)
        {
            int[] backoffs = { 1000, 2000, 5000, 15000 };
            int attempt = 0;

            while (!token.IsCancellationRequested && !_disposed)
            {
                try
                {
                    _ws?.Dispose();
                    _ws = new ClientWebSocket();
                    _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                    // If we have initial symbols, include them in the query url, otherwise connect to base stream endpoint
                    var initialStreams = new List<string>();
                    foreach (var sym in _subscribedSymbols.Keys)
                    {
                        initialStreams.Add($"{sym}@aggTrade");
                        initialStreams.Add($"{sym}@bookTicker");
                    }

                    var wsUrl = initialStreams.Count > 0
                        ? $"wss://fstream.binance.com/market/stream?streams={string.Join("/", initialStreams)}"
                        : "wss://fstream.binance.com/market/stream?streams=btcusdt@aggTrade";

                    Console.WriteLine($"[BinanceWs] Connecting to {wsUrl}...");
                    await _ws.ConnectAsync(new Uri(wsUrl), token);
                    Console.WriteLine("[BinanceWs] Connected successfully.");
                    attempt = 0;

                    await ReceiveLoopAsync(_ws, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BinanceWs] Connection error: {ex.Message}");
                }

                if (!token.IsCancellationRequested && !_disposed)
                {
                    int delay = backoffs[Math.Min(attempt, backoffs.Length - 1)];
                    attempt++;
                    Console.WriteLine($"[BinanceWs] Reconnecting in {delay}ms (attempt {attempt})...");
                    try { await Task.Delay(delay, token); } catch { }
                }
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            Console.WriteLine("[BinanceWs ReceiveLoop] Started.");

            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("[BinanceWs] Received close message.");
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    ParseMessage(ms);
                }
            }
            Console.WriteLine($"[BinanceWs ReceiveLoop] Exited. State={ws.State}, Cancelled={token.IsCancellationRequested}");
        }

        private void ParseMessage(MemoryStream stream)
        {
            try
            {
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                // Combined stream format: { "stream": "...", "data": { ... } } or raw event
                var data = root.TryGetProperty("data", out var d) ? d : root;

                if (data.TryGetProperty("e", out var eventType))
                {
                    var eType = eventType.GetString();
                    if (eType == "aggTrade")
                    {
                        var sym = data.GetProperty("s").GetString() ?? "";
                        var pStr = data.GetProperty("p").GetString();
                        var ts = data.GetProperty("T").GetInt64();
                        var eventTs = data.TryGetProperty("E", out var eElem) ? eElem.GetInt64() : ts;

                        long localNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        LivePriceCache.UpdateServerOffset(eventTs - localNow);

                        if (!string.IsNullOrEmpty(sym) && decimal.TryParse(pStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
                        {
                            _priceCache.UpdateFromAggTrade(sym, price, ts, "ws_last");
                        }
                    }
                    else if (eType == "bookTicker")
                    {
                        // bookTicker used for diagnostic logging only
                        var sym = data.GetProperty("s").GetString() ?? "";
                        var bidStr = data.TryGetProperty("b", out var b) ? b.GetString() : null;
                        var askStr = data.TryGetProperty("a", out var a) ? a.GetString() : null;
                        var ts = data.TryGetProperty("T", out var t) ? t.GetInt64() : 0;
                        // Do not use bid/ask for Entry/TP/SL. Only aggTrade last price is used.
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BinanceWs ParseError] {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            try { _ws?.Dispose(); } catch { }
            _sendLock.Dispose();
        }
    }
}
