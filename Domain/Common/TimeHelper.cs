using System;

namespace CryptoSense.Domain.Common
{
    public static class TimeHelper
    {
        private static readonly TimeZoneInfo AzerbaijanTz;

        static TimeHelper()
        {
            try
            {
                // Linux / Docker / macOS standard identifier
                AzerbaijanTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baku");
            }
            catch
            {
                try
                {
                    // Windows standard identifier
                    AzerbaijanTz = TimeZoneInfo.FindSystemTimeZoneById("Azerbaijan Standard Time");
                }
                catch
                {
                    // Fallback to UTC+4 if timezone database is missing
                    AzerbaijanTz = TimeZoneInfo.CreateCustomTimeZone("AZT", TimeSpan.FromHours(4), "Azerbaijan Standard Time", "AZT");
                }
            }
        }

        public static DateTime NowAz => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, AzerbaijanTz);

        public static DateTime ToAzerbaijanTime(this DateTime utcDateTime)
        {
            var utc = utcDateTime.Kind == DateTimeKind.Utc 
                ? utcDateTime 
                : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, AzerbaijanTz);
        }

        public static string FormatAz(this DateTime dt)
        {
            var azTime = dt.Kind == DateTimeKind.Utc ? dt.ToAzerbaijanTime() : dt;
            return azTime.ToString("dd.MM.yyyy | HH:mm:ss");
        }

        public static string NowFormatted => NowAz.ToString("dd.MM.yyyy | HH:mm:ss");
    }
}
