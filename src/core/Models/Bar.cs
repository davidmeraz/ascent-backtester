using System;

namespace Futures.Core
{
    /// <summary>A completed OHLCV bar. Its timestamp is the exclusive UTC closing boundary.</summary>
    public sealed class Bar
    {
        public Bar(DateTime closeTimeUtc, double open, double high, double low, double close, long volume)
        {
            Guard.Utc(closeTimeUtc, nameof(closeTimeUtc));
            Guard.Finite(open, nameof(open));
            Guard.Finite(high, nameof(high));
            Guard.Finite(low, nameof(low));
            Guard.Finite(close, nameof(close));
            if (high < low || open < low || open > high || close < low || close > high)
                throw new ArgumentException("OHLC values must lie within the low/high range.");
            if (volume < 0)
                throw new ArgumentOutOfRangeException(nameof(volume), "Volume cannot be negative.");

            CloseTimeUtc = closeTimeUtc;
            Open = open;
            High = high;
            Low = low;
            Close = close;
            Volume = volume;
        }

        public DateTime CloseTimeUtc { get; }
        public double Open { get; }
        public double High { get; }
        public double Low { get; }
        public double Close { get; }
        public long Volume { get; }
    }
}
