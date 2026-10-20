using System;

namespace Futures.Core
{
    public sealed class InstrumentSpec
    {
        public static InstrumentSpec ES { get; } = new InstrumentSpec("ES", 0.25, 50.0);
        public static InstrumentSpec NQ { get; } = new InstrumentSpec("NQ", 0.25, 20.0);

        public InstrumentSpec(string symbol, double tickSize, double pointValue)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("An instrument symbol is required.", nameof(symbol));
            Guard.PositiveFinite(tickSize, nameof(tickSize));
            Guard.PositiveFinite(pointValue, nameof(pointValue));
            Symbol = symbol;
            TickSize = tickSize;
            PointValue = pointValue;
        }

        public string Symbol { get; }
        public double TickSize { get; }
        public double PointValue { get; }
        public double TickValue => TickSize * PointValue;
    }
}
