using System;

namespace Futures.Core
{
    /// <summary>
    /// Implements the Dubins-Savage (1965) Optimal Probability "Bold Play" strategy for negative-drift games:
    /// 1. Strictly 1 trade per day (Optimal Stopping) to prevent over-trading and commission decay.
    /// 2. Enters during the New York Open volatility expansion window (13:30 - 14:30 UTC) with a
    ///    randomized minute offset (0-45 min) to decorrelate multiple accounts.
    /// 3. Uses wide symmetrical 1:1 blocks (default 100 ticks = 25 pts NQ, $1,000 with 2 contracts)
    ///    to minimize commission drag (<0.9%) and satisfy the 40% consistency rule in 3 winning days.
    /// </summary>
    public sealed class DubinsSavageStrategy : IStrategy
    {
        private readonly int? initialSeed;
        private Random random;
        private DateTime? previousBarTime;
        private DateTime? lastTradeDate;
        private DateTime? currentDayDate;
        private int dailyOffsetMinutes;

        public DubinsSavageStrategy(StrategyParameters parameters, int? seed = null)
        {
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            this.initialSeed = seed;
            this.random = seed.HasValue ? new Random(seed.Value) : new Random(Guid.NewGuid().GetHashCode());
        }

        public string Name => "DubinsSavage";
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

            DateTime barDate = bar.CloseTimeUtc.Date;
            if (!currentDayDate.HasValue || currentDayDate.Value != barDate)
            {
                currentDayDate = barDate;
                // Descorrelación temporal: minuto aleatorio dentro de los primeros 45 min de la apertura NY (13:30 - 14:15 UTC)
                dailyOffsetMinutes = random.Next(0, 46);
            }

            bool alreadyTradedToday = lastTradeDate.HasValue && lastTradeDate.Value == barDate;

            if (!alreadyTradedToday && context.PositionQuantity == 0 && !context.HasPendingOrder)
            {
                TimeSpan nyOpenTarget = new TimeSpan(13, 30, 0).Add(TimeSpan.FromMinutes(dailyOffsetMinutes));
                TimeSpan barTime = bar.CloseTimeUtc.TimeOfDay;

                // Dispara en el minuto objetivo de expansión NY (o si la sesión configurada empieza después)
                if (barTime >= nyOpenTarget && barTime <= new TimeSpan(19, 30, 0))
                {
                    bool isLong = random.Next(2) == 0;
                    signal = isLong ? StrategySignal.Long : StrategySignal.Short;
                    int dir = isLong ? 1 : -1;

                    int slTicks = Parameters.StopLossTicks ?? 98;
                    int tpTicks = Parameters.TakeProfitTicks ?? 102;

                    intent = new OrderIntent(
                        dir * Parameters.Quantity,
                        "Dubins-Savage Bold Play (1/Día · NY Open)",
                        slTicks,
                        tpTicks,
                        Parameters.BreakEvenTriggerTicks,
                        Parameters.BreakEvenOffsetTicks,
                        Parameters.TrailingStopTicks);

                    lastTradeDate = barDate;
                }
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
            lastTradeDate = null;
            currentDayDate = null;
            random = initialSeed.HasValue ? new Random(initialSeed.Value) : new Random(Guid.NewGuid().GetHashCode());
        }
    }
}
