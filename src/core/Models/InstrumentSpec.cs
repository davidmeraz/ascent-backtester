using System;

namespace Futures.Core
{
    public sealed class InstrumentSpec
    {
        public static InstrumentSpec ES { get; } = new InstrumentSpec("ES", 0.25, 50.0, false);
        public static InstrumentSpec NQ { get; } = new InstrumentSpec("NQ", 0.25, 20.0, false);
        public static InstrumentSpec MES { get; } = new InstrumentSpec("MES", 0.25, 5.0, true);
        public static InstrumentSpec MNQ { get; } = new InstrumentSpec("MNQ", 0.25, 2.0, true);

        public InstrumentSpec(string symbol, double tickSize, double pointValue, bool isMicro = false)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("An instrument symbol is required.", nameof(symbol));
            Guard.PositiveFinite(tickSize, nameof(tickSize));
            Guard.PositiveFinite(pointValue, nameof(pointValue));
            Symbol = symbol;
            TickSize = tickSize;
            PointValue = pointValue;
            IsMicro = isMicro;
        }

        public string Symbol { get; }
        public double TickSize { get; }
        public double PointValue { get; }
        public double TickValue => TickSize * PointValue;
        public bool IsMicro { get; }

        public static InstrumentSpec FromSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("An instrument symbol is required.", nameof(symbol));
            switch (symbol.Trim().ToUpperInvariant())
            {
                case "ES": return ES;
                case "NQ": return NQ;
                case "MES": return MES;
                case "MNQ": return MNQ;
                default:
                    throw new ArgumentException("Unsupported instrument symbol. Use ES, NQ, MES, or MNQ.", nameof(symbol));
            }
        }
    }
}
