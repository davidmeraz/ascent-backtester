using System;

namespace Futures.Core
{
    /// <summary>
    /// Continuous Wilder Average True Range (ATR) with exact parity to dav3e_hud:
    /// True Range uses High-Low on the first bar and Max(H-L, |H-prevClose|, |L-prevClose|) thereafter;
    /// averages arithmetically up to Period bars and applies Wilder smoothing afterwards.
    /// </summary>
    public sealed class WilderAtr
    {
        private double atr;
        private double prevClose;
        private double trSum;

        public WilderAtr(int period = 14)
        {
            if (period < 1)
                throw new ArgumentOutOfRangeException(nameof(period), "ATR period must be at least one.");
            Period = period;
        }

        public int Period { get; }
        public int Count { get; private set; }
        public bool IsReady => Count >= Period;
        public double? Value => Count > 0 ? atr : (double?)null;

        public double Update(Bar bar)
        {
            if (bar == null)
                throw new ArgumentNullException(nameof(bar));
            return Update(bar.High, bar.Low, bar.Close);
        }

        public double Update(double high, double low, double close)
        {
            Guard.Finite(high, nameof(high));
            Guard.Finite(low, nameof(low));
            Guard.Finite(close, nameof(close));
            if (high < low)
                throw new ArgumentException("High cannot be lower than Low.");

            double tr = Count == 0
                ? Math.Max(0.0, high - low)
                : Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));

            Count++;
            if (Count <= Period)
            {
                trSum += tr;
                atr = trSum / Count;
            }
            else
            {
                atr = (atr * (Period - 1) + tr) / Period;
            }

            prevClose = close;
            return atr;
        }

        public void Reset()
        {
            atr = 0.0;
            prevClose = 0.0;
            trSum = 0.0;
            Count = 0;
        }
    }
}
