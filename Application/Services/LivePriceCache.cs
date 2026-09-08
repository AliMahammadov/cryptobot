using System;
using System.Collections.Concurrent;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Application.Services
{
    public class LivePriceCache
    {
        private readonly ConcurrentDictionary<string, LivePriceSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);

        public event Action<LivePriceSnapshot>? OnPrice;

        public LivePriceSnapshot? GetSnapshot(string symbol)
        {
            if (_cache.TryGetValue(symbol, out var snap))
            {
                return snap;
            }
            return null;
        }

        private static long _serverTimeOffsetMs = 0;
        public static long ServerTimeOffsetMs => System.Threading.Volatile.Read(ref _serverTimeOffsetMs);

        public static void UpdateServerOffset(long offsetMs)
        {
            if (System.Threading.Volatile.Read(ref _serverTimeOffsetMs) == 0)
            {
                System.Threading.Volatile.Write(ref _serverTimeOffsetMs, offsetMs);
            }
            else
            {
                long current = System.Threading.Volatile.Read(ref _serverTimeOffsetMs);
                long smoothed = (long)(current * 0.85 + offsetMs * 0.15);
                System.Threading.Volatile.Write(ref _serverTimeOffsetMs, smoothed);
            }
        }

        public static long CurrentExchangeTimeMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ServerTimeOffsetMs;

        public void UpdateFromAggTrade(string symbol, decimal price, long exchangeTsMs, bool isRestFallback)
        {
            UpdateFromAggTrade(symbol, price, exchangeTsMs, isRestFallback ? "rest_fallback" : "ws_last");
        }

        public void UpdateFromAggTrade(string symbol, decimal price, long exchangeTsMs, string source = "ws_last")
        {
            if (price <= 0) return;

            long localNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var snap = _cache.AddOrUpdate(symbol,
                sym => new LivePriceSnapshot
                {
                    Symbol = sym,
                    Last = price,
                    SessionHigh = price,
                    SessionLow = price,
                    ExchangeTsMs = exchangeTsMs,
                    LocalReceiveTsMs = localNow,
                    Source = source
                },
                (sym, existing) =>
                {
                    existing.Last = price;
                    if (price > existing.SessionHigh) existing.SessionHigh = price;
                    if (price < existing.SessionLow || existing.SessionLow == 0) existing.SessionLow = price;
                    existing.ExchangeTsMs = exchangeTsMs;
                    existing.LocalReceiveTsMs = localNow;
                    existing.Source = source;
                    return existing;
                });

            OnPrice?.Invoke(snap);
        }

        public void ResetSession(string symbol)
        {
            if (_cache.TryGetValue(symbol, out var snap))
            {
                snap.SessionHigh = snap.Last;
                snap.SessionLow = snap.Last;
            }
        }

        public void ResetSession(string symbol, decimal initialPrice, long exchangeTsMs = 0)
        {
            if (exchangeTsMs == 0) exchangeTsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _cache.AddOrUpdate(symbol,
                sym => new LivePriceSnapshot
                {
                    Symbol = sym,
                    Last = initialPrice,
                    SessionHigh = initialPrice,
                    SessionLow = initialPrice,
                    ExchangeTsMs = exchangeTsMs,
                    Source = "ws_last"
                },
                (sym, existing) =>
                {
                    existing.Last = initialPrice;
                    existing.SessionHigh = initialPrice;
                    existing.SessionLow = initialPrice;
                    existing.ExchangeTsMs = exchangeTsMs;
                    return existing;
                });
        }
    }
}
