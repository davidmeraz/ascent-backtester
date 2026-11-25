using System;

namespace Futures.Core
{
    /// <summary>
    /// Calculates the Stochastic Oscillator (%K, %D) with customizable smoothing.
    /// Default values: PeriodK = 20, SmoothK = 5, PeriodD = 5.
    /// </summary>
    public sealed class StochasticOscillator
    {
        private readonly int periodK;
        private readonly double[] highBuffer;
        private readonly double[] lowBuffer;
        private readonly SimpleMovingAverage kSma;
        private readonly SimpleMovingAverage dSma;
        private int bufferIndex;
        private int count;

        public StochasticOscillator(int periodK = 20, int smoothK = 5, int periodD = 5)
        {
            if (periodK < 1) throw new ArgumentOutOfRangeException(nameof(periodK), "PeriodK must be >= 1.");
            if (smoothK < 1) throw new ArgumentOutOfRangeException(nameof(smoothK), "SmoothK must be >= 1.");
            if (periodD < 1) throw new ArgumentOutOfRangeException(nameof(periodD), "PeriodD must be >= 1.");

            this.periodK = periodK;
            highBuffer = new double[periodK];
            lowBuffer = new double[periodK];
            kSma = new SimpleMovingAverage(smoothK);
            dSma = new SimpleMovingAverage(periodD);
        }

        public int PeriodK => periodK;
        public int SmoothK => kSma.Period;
        public int PeriodD => dSma.Period;

        public bool IsReady => kSma.IsReady;
        public double? K => kSma.Value;
        public double? D => dSma.Value;

        public (double? K, double? D) Update(double high, double low, double close)
        {
            Guard.Finite(high, nameof(high));
            Guard.Finite(low, nameof(low));
            Guard.Finite(close, nameof(close));

            highBuffer[bufferIndex] = high;
            lowBuffer[bufferIndex] = low;
            bufferIndex = (bufferIndex + 1) % periodK;
            if (count < periodK) count++;

            if (count < periodK)
                return (null, null);

            double highestHigh = highBuffer[0];
            double lowestLow = lowBuffer[0];
            for (int i = 1; i < periodK; i++)
            {
                if (highBuffer[i] > highestHigh) highestHigh = highBuffer[i];
                if (lowBuffer[i] < lowestLow) lowestLow = lowBuffer[i];
            }

            double range = highestHigh - lowestLow;
            double rawK = Math.Abs(range) < 1e-9 ? 50.0 : ((close - lowestLow) / range) * 100.0;
            rawK = Math.Max(0.0, Math.Min(100.0, rawK));

            double? smoothedK = kSma.Update(rawK);
            double? smoothedD = smoothedK.HasValue ? dSma.Update(smoothedK.Value) : null;

            return (smoothedK, smoothedD);
        }

        public void Reset()
        {
            Array.Clear(highBuffer, 0, highBuffer.Length);
            Array.Clear(lowBuffer, 0, lowBuffer.Length);
            bufferIndex = 0;
            count = 0;
            kSma.Reset();
            dSma.Reset();
        }
    }
}
