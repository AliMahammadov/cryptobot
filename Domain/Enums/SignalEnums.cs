namespace CryptoSense.Domain.Enums
{
    public enum UserRole
    {
        Admin,
        User
    }

    public enum SignalDirection
    {
        Buy,   // LONG
        Sell   // SHORT
    }

    public enum SignalTimeframe
    {
        M1,
        M3,
        M5,
        M15,
        H1,
        H4
    }

    public enum SignalStatus
    {
        Open,
        Success,
        Failed,
        Neutral
    }

    public enum IndicatorVote
    {
        Bullish,
        Bearish,
        Neutral
    }

    public enum SignalCloseReason
    {
        TP1,
        TP2,
        TP3,
        SL,
        BE,
        TRAIL,
        INVALIDATION,
        TIME,
        NO_EDGE
    }

    public enum BtcMarketRegime
    {
        Bullish,
        Bearish,
        Ranging
    }
}
