using System.Collections.Generic;
using CryptoSense.Application.DTOs;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Application.Interfaces
{
    public interface IIndicatorEngine
    {
        IndicatorResult CalculateIndicators(List<Kline> klines, BtcMarketCompass? btcCompass = null);
        decimal CalculateEma(List<decimal> prices, int period);
        decimal CalculateRsi(List<decimal> prices, int period = 14);
        (decimal Macd, decimal Signal, decimal Hist) CalculateMacd(List<decimal> prices);
        decimal CalculateAtr(List<Kline> klines, int period = 14);
    }
}
