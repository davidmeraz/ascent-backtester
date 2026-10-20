using System;

namespace Futures.Core
{
    /// <summary>A trailing arithmetic mean, available only after a full period of observations.</summary>
    public sealed class SimpleMovingAverage
    {
        private readonly double[] values;
        private int next;
        private double sum;

        public SimpleMovingAverage(int period)
        {
            if (period < 1)
                throw new ArgumentOutOfRangeException(nameof(period), "Period must be at least one.");
            Period = period;
            values = new double[period];
        }

        public int Period { get; }
        public int Count { get; private set; }
        public bool IsReady => Count == Period;
        public double? Value => IsReady ? sum / Period : (double?)null;

        public double? Update(double value)
        {
            Guard.Finite(value, nameof(value));
            if (IsReady)
                sum -= values[next];
            else
                Count++;
            values[next] = value;
            sum += value;
            next = (next + 1) % Period;
            return Value;
        }

        public void Reset()
        {
            Array.Clear(values, 0, values.Length);
            Count = 0;
            next = 0;
            sum = 0;
        }
    }
}
