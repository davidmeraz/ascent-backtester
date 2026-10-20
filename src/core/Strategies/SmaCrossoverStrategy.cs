using System;

namespace Futures.Core
{
    /// <summary>
    /// Evaluates completed bars only. A cross enters from flat or closes the opposite position.
    /// It never reverses or adds to an existing position, and never submits orders itself.
    /// </summary>
    public sealed class SmaCrossoverStrategy : IStrategy
    {
        private readonly SimpleMovingAverage fastAverage;
        private readonly SimpleMovingAverage slowAverage;
        private double? previousFast;
        private double? previousSlow;
        private DateTime? previousBarTime;

        public SmaCrossoverStrategy(StrategyParameters parameters)
        {
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            fastAverage = new SimpleMovingAverage(parameters.FastPeriod);
            slowAverage = new SimpleMovingAverage(parameters.SlowPeriod);
        }

        public string Name => "SmaCrossover";
        public StrategyParameters Parameters { get; }
        public int PositionQuantity { get; private set; }
        public long BarsProcessed { get; private set; }

        /// <summary>For hosts which confirm every execution and have no outstanding order.</summary>
        public StrategySnapshot OnBarClose(Bar bar)
        {
            return OnBarClose(bar, new StrategyContext(PositionQuantity));
        }

        /// <summary>The supplied host context takes precedence over previously confirmed state.</summary>
        public StrategySnapshot OnBarClose(Bar bar, StrategyContext context)
        {
            if (bar == null)
                throw new ArgumentNullException(nameof(bar));
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (previousBarTime.HasValue && bar.CloseTimeUtc <= previousBarTime.Value)
                throw new ArgumentException("Bars must be supplied once, in strictly increasing UTC order.", nameof(bar));

            double? fast = fastAverage.Update(bar.Close);
            double? slow = slowAverage.Update(bar.Close);
            StrategySignal signal = StrategySignal.None;
            if (previousFast.HasValue && previousSlow.HasValue && fast.HasValue && slow.HasValue)
            {
                if (previousFast.Value <= previousSlow.Value && fast.Value > slow.Value)
                    signal = StrategySignal.Long;
                else if (previousFast.Value >= previousSlow.Value && fast.Value < slow.Value)
                    signal = StrategySignal.Short;
            }

            PositionQuantity = context.PositionQuantity;
            OrderIntent? intent = CreateIntent(signal, context);
            previousFast = fast;
            previousSlow = slow;
            previousBarTime = bar.CloseTimeUtc;
            BarsProcessed++;
            return new StrategySnapshot(bar.CloseTimeUtc, fast, slow, signal, intent);
        }

        public void OnExecution(ExecutionReport execution)
        {
            if (execution == null)
                throw new ArgumentNullException(nameof(execution));
            PositionQuantity = execution.PositionQuantity;
        }

        public void Reset()
        {
            fastAverage.Reset();
            slowAverage.Reset();
            previousFast = null;
            previousSlow = null;
            previousBarTime = null;
            PositionQuantity = 0;
            BarsProcessed = 0;
        }

        private OrderIntent? CreateIntent(StrategySignal signal, StrategyContext context)
        {
            if (signal == StrategySignal.None || context.HasPendingOrder)
                return null;
            int direction = signal == StrategySignal.Long ? 1 : -1;
            if (context.PositionQuantity == 0)
                return new OrderIntent(
                    direction * Parameters.Quantity,
                    "SMA crossing entry",
                    Parameters.StopLossTicks,
                    Parameters.TakeProfitTicks);
            if (Math.Sign(context.PositionQuantity) != direction)
                return new OrderIntent(0, "Opposite SMA crossing exit");
            return null;
        }
    }
}
