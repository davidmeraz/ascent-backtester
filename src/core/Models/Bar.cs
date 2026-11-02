using System;

namespace Futures.Core
{
    /// <summary>A completed OHLCV bar. Its timestamp is the exclusive UTC closing boundary.</summary>
    public sealed class Bar
    {
        public Bar(DateTime closeTimeUtc, double open, double high, double low, double close, long volume)
            : this(closeTimeUtc, open, high, low, close, volume, 0L, 0L, 0L, 0L)
        {
        }

        public Bar(
            DateTime closeTimeUtc,
            double open,
            double high,
            double low,
            double close,
            long volume,
            long deltaVolume,
            long askVolume,
            long bidVolume,
            long tickCount)
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
            if (askVolume < 0)
                throw new ArgumentOutOfRangeException(nameof(askVolume), "Ask volume cannot be negative.");
            if (bidVolume < 0)
                throw new ArgumentOutOfRangeException(nameof(bidVolume), "Bid volume cannot be negative.");
            if (tickCount < 0)
                throw new ArgumentOutOfRangeException(nameof(tickCount), "Tick count cannot be negative.");

            CloseTimeUtc = closeTimeUtc;
            Open = open;
            High = high;
            Low = low;
            Close = close;
            Volume = volume;
            DeltaVolume = deltaVolume;
            AskVolume = askVolume;
            BidVolume = bidVolume;
            TickCount = tickCount;
        }

        public DateTime CloseTimeUtc { get; }
        public double Open { get; }
        public double High { get; }
        public double Low { get; }
        public double Close { get; }
        public long Volume { get; }
        public long DeltaVolume { get; }
        public long AskVolume { get; }
        public long BidVolume { get; }
        public long TickCount { get; }

        public bool HasOrderFlow => TickCount > 0;
        public double TypicalPrice => (High + Low + Close) / 3.0;
        public double Range => High - Low;
        public double DeltaRatio => Volume > 0 ? (double)DeltaVolume / Volume : 0.0;
    }
}
