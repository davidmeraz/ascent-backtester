using System;

namespace Futures.Core
{
    /// <summary>
    /// Baseline strategy that executes random long/short entries with symmetrical 1:1 Stop Loss and Take Profit.
    /// In an efficient market or random walk, this demonstrates a theoretical ~50% win rate.
    /// </summary>
    public sealed class RandomEntryStrategy : IStrategy
    {
        private readonly int? initialSeed;
        private Random random;
        private DateTime? previousBarTime;

        public RandomEntryStrategy(StrategyParameters parameters, int? seed = null)
        {
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            this.initialSeed = seed;
            this.random = seed.HasValue ? new Random(seed.Value) : new Random(Guid.NewGuid().GetHashCode());
        }

        public string Name => "RandomEntry";
        public StrategyParameters Parameters { get; }
        public int PositionQuantity { get; private set; }
        public long BarsProcessed { get; private set; }

        public StrategySnapshot OnBarClose(Bar bar)
        {
            return OnBarClose(bar, new StrategyContext(PositionQuantity));
        }

        public StrategySnapshot OnBarClose(Bar bar, StrategyContext context)
        {
            if (bar == null) throw new ArgumentNullException(nameof(bar));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (previousBarTime.HasValue && bar.CloseTimeUtc <= previousBarTime.Value)
                throw new ArgumentException("Bars must be supplied once, in strictly increasing UTC order.", nameof(bar));

            PositionQuantity = context.PositionQuantity;
            StrategySignal signal = StrategySignal.None;
            OrderIntent? intent = null;

            if (context.PositionQuantity == 0 && !context.HasPendingOrder)
            {
                // 50% chance Long, 50% chance Short
                bool isLong = random.Next(2) == 0;
                signal = isLong ? StrategySignal.Long : StrategySignal.Short;
                int dir = isLong ? 1 : -1;

                intent = new OrderIntent(
                    dir * Parameters.Quantity,
                    "Random Entry (1:1 Ratio)",
                    Parameters.StopLossTicks,
                    Parameters.TakeProfitTicks,
                    Parameters.BreakEvenTriggerTicks,
                    Parameters.BreakEvenOffsetTicks,
                    Parameters.TrailingStopTicks);
            }

            previousBarTime = bar.CloseTimeUtc;
            BarsProcessed++;
            return new StrategySnapshot(bar.CloseTimeUtc, null, null, signal, intent);
        }

        public void OnExecution(ExecutionReport execution)
        {
            if (execution == null) throw new ArgumentNullException(nameof(execution));
            PositionQuantity = execution.PositionQuantity;
        }

        public void Reset()
        {
            PositionQuantity = 0;
            BarsProcessed = 0;
            previousBarTime = null;
            random = initialSeed.HasValue ? new Random(initialSeed.Value) : new Random(Guid.NewGuid().GetHashCode());
        }
    }
}
