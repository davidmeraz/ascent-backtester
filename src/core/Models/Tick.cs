using System;

namespace Futures.Core
{
    /// <summary>
    /// Represents a single market trade tick with Level 1 Bid/Ask context.
    /// </summary>
    public sealed class Tick
    {
        public Tick(DateTime timeUtc, double price, double bid, double ask, long volume)
        {
            Guard.Utc(timeUtc, nameof(timeUtc));
            Guard.Finite(price, nameof(price));
            Guard.Finite(bid, nameof(bid));
            Guard.Finite(ask, nameof(ask));
            if (ask < bid)
                throw new ArgumentException("Ask price cannot be lower than Bid price.");
            if (volume < 0)
                throw new ArgumentOutOfRangeException(nameof(volume), "Volume cannot be negative.");

            TimeUtc = timeUtc;
            Price = price;
            Bid = bid;
            Ask = ask;
            Volume = volume;
        }

        public DateTime TimeUtc { get; }
        public double Price { get; }
        public double Bid { get; }
        public double Ask { get; }
        public long Volume { get; }

        /// <summary>
        /// Bid/Ask delta convention matching dav3e_hud: >= Ask is aggressive buy (+Volume),
        /// &lt;= Bid is aggressive sell (-Volume), inside spread is 0.
        /// </summary>
        public long SignedDeltaVolume =>
            Price >= Ask ? Volume :
            Price <= Bid ? -Volume : 0L;
    }
}
