using System;

namespace Futures.Core
{
    internal static class Guard
    {
        internal static void Utc(DateTime value, string name)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Timestamp must explicitly use DateTimeKind.Utc.", name);
        }

        internal static void Finite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "Value must be finite.");
        }

        internal static void PositiveFinite(double value, string name)
        {
            Finite(value, name);
            if (value <= 0)
                throw new ArgumentOutOfRangeException(name, "Value must be positive.");
        }
    }

    public interface IStrategy
    {
        string Name { get; }
        int PositionQuantity { get; }
        StrategySnapshot OnBarClose(Bar bar, StrategyContext context);
        void OnExecution(ExecutionReport execution);
        void Reset();
    }

    /// <summary>A host-confirmed execution and the resulting signed position, never an order request.</summary>
    public sealed class ExecutionReport
    {
        public ExecutionReport(DateTime timeUtc, int positionQuantity, double fillPrice)
        {
            Guard.Utc(timeUtc, nameof(timeUtc));
            Guard.Finite(fillPrice, nameof(fillPrice));
            TimeUtc = timeUtc;
            PositionQuantity = positionQuantity;
            FillPrice = fillPrice;
        }

        public DateTime TimeUtc { get; }
        public int PositionQuantity { get; }
        public double FillPrice { get; }
    }

    /// <summary>Authoritative state from the host immediately before a completed bar is evaluated.</summary>
    public sealed class StrategyContext
    {
        public StrategyContext(int positionQuantity = 0, bool hasPendingOrder = false)
        {
            PositionQuantity = positionQuantity;
            HasPendingOrder = hasPendingOrder;
        }

        /// <summary>Positive is long; negative is short; zero is flat.</summary>
        public int PositionQuantity { get; }
        public bool HasPendingOrder { get; }
    }

    public sealed class StrategyParameters
    {
        public StrategyParameters(
            int fastPeriod = 10,
            int slowPeriod = 20,
            int quantity = 1,
            int? stopLossTicks = null,
            int? takeProfitTicks = null)
        {
            if (fastPeriod < 1)
                throw new ArgumentOutOfRangeException(nameof(fastPeriod), "Fast period must be at least one.");
            if (slowPeriod <= fastPeriod)
                throw new ArgumentOutOfRangeException(nameof(slowPeriod), "Slow period must exceed the fast period.");
            if (quantity < 1)
                throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be at least one.");
            if (stopLossTicks.HasValue && stopLossTicks.Value < 1)
                throw new ArgumentOutOfRangeException(nameof(stopLossTicks), "Stop loss ticks must be at least one.");
            if (takeProfitTicks.HasValue && takeProfitTicks.Value < 1)
                throw new ArgumentOutOfRangeException(nameof(takeProfitTicks), "Take profit ticks must be at least one.");

            FastPeriod = fastPeriod;
            SlowPeriod = slowPeriod;
            Quantity = quantity;
            StopLossTicks = stopLossTicks;
            TakeProfitTicks = takeProfitTicks;
        }

        public int FastPeriod { get; }
        public int SlowPeriod { get; }
        public int Quantity { get; }
        public int? StopLossTicks { get; }
        public int? TakeProfitTicks { get; }
    }

    public enum StrategySignal
    {
        None,
        Long,
        Short
    }

    public sealed class OrderIntent
    {
        public OrderIntent(
            int targetPositionQuantity,
            string reason,
            int? stopLossTicks = null,
            int? takeProfitTicks = null)
        {
            TargetPositionQuantity = targetPositionQuantity;
            Reason = reason ?? throw new ArgumentNullException(nameof(reason));
            StopLossTicks = stopLossTicks;
            TakeProfitTicks = takeProfitTicks;
        }

        /// <summary>The desired resulting signed position, not a buy/sell order quantity.</summary>
        public int TargetPositionQuantity { get; }
        public string Reason { get; }
        public int? StopLossTicks { get; }
        public int? TakeProfitTicks { get; }
    }

    public sealed class StrategySnapshot
    {
        public StrategySnapshot(DateTime closeTimeUtc, double? fast, double? slow,
            StrategySignal signal, OrderIntent? orderIntent)
        {
            CloseTimeUtc = closeTimeUtc;
            Fast = fast;
            Slow = slow;
            Signal = signal;
            OrderIntent = orderIntent;
        }

        public DateTime CloseTimeUtc { get; }
        public double? Fast { get; }
        public double? Slow { get; }
        public bool IsReady => Fast.HasValue && Slow.HasValue;
        /// <summary>Raw crossing independent of position or pending orders, used for parity checks.</summary>
        public StrategySignal Signal { get; }
        public OrderIntent? OrderIntent { get; }
    }
}
