using System;

namespace Futures.Core
{
    /// <summary>
    /// Implements the strategy from the course lesson "Mi Libro de Jugadas: El Arte de la Reversión":
    /// 1. Assets: NQ, GC or similar volatile futures.
    /// 2. Indicators: Stochastic Oscillator (20, 5, 5). Overbought > 80, Oversold < 20.
    /// 3. Levels: Prior Day High/Low (PDH/PDL), rolling horizontal support/resistance, and round numbers.
    /// 4. 2-Bullet DCA Position Building:
    ///    - Bullet #1: Entered at horizontal level when Stochastic is extreme (<20 for long, >80 for short).
    ///    - Bullet #2 ("La Mejora"): Entered if price continues against position by X points and Stochastic remains extreme.
    /// 5. Mechanical Exit ("La Señal de Escape"):
    ///    - Entire position is closed when Stochastic exits the extreme zone (>20 for long, <80 for short).
    /// </summary>
    public sealed class ReversionPlaybookStrategy : IStrategy
    {
        private readonly StochasticOscillator stochastic;
        private readonly double[] rollingHighs;
        private readonly double[] rollingLows;
        private readonly int rollingWindowBars;
        private int rollingIndex;
        private int rollingCount;

        private DateTime? currentDay;
        private double? currentDayHigh;
        private double? currentDayLow;
        private double? previousDayHigh;
        private double? previousDayLow;

        private double? previousK;
        private DateTime? previousBarTime;

        private int bulletCount;
        private double firstEntryPrice;

        public ReversionPlaybookStrategy(StrategyParameters parameters)
        {
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            int kPeriod = parameters.FastPeriod >= 1 ? parameters.FastPeriod : 20;
            int smooth = parameters.SlowPeriod >= 1 ? parameters.SlowPeriod : 5;
            stochastic = new StochasticOscillator(kPeriod, smooth, 5);

            rollingWindowBars = 30;
            rollingHighs = new double[rollingWindowBars];
            rollingLows = new double[rollingWindowBars];
        }

        public string Name => "ReversionPlaybook";
        public StrategyParameters Parameters { get; }
        public int PositionQuantity { get; private set; }
        public long BarsProcessed { get; private set; }

        public double OverboughtThreshold { get; set; } = 80.0;
        public double OversoldThreshold { get; set; } = 20.0;
        public int DcaDistanceTicks { get; set; } = 40; // 40 ticks = 10 pts in NQ
        public double RoundNumberInterval { get; set; } = 50.0;
        public double RoundNumberTolerance { get; set; } = 4.0;

        public StrategySnapshot OnBarClose(Bar bar)
        {
            return OnBarClose(bar, new StrategyContext(PositionQuantity));
        }

        public StrategySnapshot OnBarClose(Bar bar, StrategyContext context)
        {
            if (bar == null) throw new ArgumentNullException(nameof(bar));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (previousBarTime.HasValue && bar.CloseTimeUtc <= previousBarTime.Value)
                throw new ArgumentException("Bars must be supplied in strictly increasing UTC order.", nameof(bar));

            PositionQuantity = context.PositionQuantity;
            if (PositionQuantity == 0)
            {
                bulletCount = 0;
            }

            // 1. Update Daily High/Low and Prior Day Levels
            DateTime barDate = bar.CloseTimeUtc.Date;
            if (!currentDay.HasValue || currentDay.Value != barDate)
            {
                if (currentDay.HasValue)
                {
                    previousDayHigh = currentDayHigh;
                    previousDayLow = currentDayLow;
                }
                currentDay = barDate;
                currentDayHigh = bar.High;
                currentDayLow = bar.Low;
            }
            else
            {
                if (bar.High > currentDayHigh) currentDayHigh = bar.High;
                if (bar.Low < currentDayLow) currentDayLow = bar.Low;
            }

            // 2. Update Rolling S/R Buffer (last 30 bars)
            rollingHighs[rollingIndex] = bar.High;
            rollingLows[rollingIndex] = bar.Low;
            rollingIndex = (rollingIndex + 1) % rollingWindowBars;
            if (rollingCount < rollingWindowBars) rollingCount++;

            double rollingSupport = rollingLows[0];
            double rollingResistance = rollingHighs[0];
            for (int i = 1; i < rollingCount; i++)
            {
                if (rollingLows[i] < rollingSupport) rollingSupport = rollingLows[i];
                if (rollingHighs[i] > rollingResistance) rollingResistance = rollingHighs[i];
            }

            // 3. Update Stochastic Oscillator
            var (kVal, dVal) = stochastic.Update(bar.High, bar.Low, bar.Close);

            StrategySignal signal = StrategySignal.None;
            OrderIntent? intent = null;

            if (kVal.HasValue && previousK.HasValue)
            {
                double k = kVal.Value;
                double prevK = previousK.Value;
                int pos = context.PositionQuantity;
                int baseQty = Math.Max(1, Parameters.Quantity);

                // --- 4. GESTIÓN DE SALIDA: "La Señal de Escape" ---
                if (pos > 0)
                {
                    // Long escape: K crosses back above Oversold zone
                    if (prevK < OversoldThreshold && k >= OversoldThreshold)
                    {
                        intent = new OrderIntent(0, "Señal de Escape: Estocástico salió de sobreventa (<20)");
                    }
                }
                else if (pos < 0)
                {
                    // Short escape: K crosses back below Overbought zone
                    if (prevK > OverboughtThreshold && k <= OverboughtThreshold)
                    {
                        intent = new OrderIntent(0, "Señal de Escape: Estocástico salió de sobrecompra (>80)");
                    }
                }

                // --- 5. GESTIÓN DE ENTRADA Y PROMEDIACIÓN (2 BALAS) ---
                if (intent == null && !context.HasPendingOrder)
                {
                    double tickSize = 0.25; // Default NQ
                    double dcaPoints = DcaDistanceTicks * tickSize;

                    if (pos == 0)
                    {
                        // Buscar Bala #1 (Entrada inicial)
                        bool nearRoundNumber = IsNearRoundNumber(bar.Close);

                        // Setup de Compra (Largo)
                        bool nearSupport = (previousDayLow.HasValue && Math.Abs(bar.Close - previousDayLow.Value) <= 10.0)
                                           || Math.Abs(bar.Close - rollingSupport) <= 6.0
                                           || nearRoundNumber;

                        if (k < OversoldThreshold && nearSupport)
                        {
                            signal = StrategySignal.Long;
                            intent = new OrderIntent(
                                baseQty,
                                "Reversión: Bala #1 (Largo en Soporte + Sobreventa)",
                                Parameters.StopLossTicks,
                                Parameters.TakeProfitTicks,
                                Parameters.BreakEvenTriggerTicks,
                                Parameters.BreakEvenOffsetTicks,
                                Parameters.TrailingStopTicks);
                            bulletCount = 1;
                            firstEntryPrice = bar.Close;
                        }
                        else
                        {
                            // Setup de Venta (Corto)
                            bool nearResistance = (previousDayHigh.HasValue && Math.Abs(bar.Close - previousDayHigh.Value) <= 10.0)
                                                  || Math.Abs(bar.Close - rollingResistance) <= 6.0
                                                  || nearRoundNumber;

                            if (k > OverboughtThreshold && nearResistance)
                            {
                                signal = StrategySignal.Short;
                                intent = new OrderIntent(
                                    -baseQty,
                                    "Reversión: Bala #1 (Corto en Resistencia + Sobrecompra)",
                                    Parameters.StopLossTicks,
                                    Parameters.TakeProfitTicks,
                                    Parameters.BreakEvenTriggerTicks,
                                    Parameters.BreakEvenOffsetTicks,
                                    Parameters.TrailingStopTicks);
                                bulletCount = 1;
                                firstEntryPrice = bar.Close;
                            }
                        }
                    }
                    else if (Math.Abs(pos) == baseQty && bulletCount == 1)
                    {
                        // Buscar Bala #2 ("La Mejora" / Promediación)
                        if (pos > 0 && bar.Close <= firstEntryPrice - dcaPoints && k < OversoldThreshold)
                        {
                            // Compra 2.ª bala
                            intent = new OrderIntent(
                                2 * baseQty,
                                "Reversión: Bala #2 (La Mejora · Promediación en Sobreventa)",
                                Parameters.StopLossTicks,
                                Parameters.TakeProfitTicks,
                                Parameters.BreakEvenTriggerTicks,
                                Parameters.BreakEvenOffsetTicks,
                                Parameters.TrailingStopTicks);
                            bulletCount = 2;
                        }
                        else if (pos < 0 && bar.Close >= firstEntryPrice + dcaPoints && k > OverboughtThreshold)
                        {
                            // Vende 2.ª bala
                            intent = new OrderIntent(
                                -2 * baseQty,
                                "Reversión: Bala #2 (La Mejora · Promediación en Sobrecompra)",
                                Parameters.StopLossTicks,
                                Parameters.TakeProfitTicks,
                                Parameters.BreakEvenTriggerTicks,
                                Parameters.BreakEvenOffsetTicks,
                                Parameters.TrailingStopTicks);
                            bulletCount = 2;
                        }
                    }
                }
            }

            previousK = kVal;
            previousBarTime = bar.CloseTimeUtc;
            BarsProcessed++;

            return new StrategySnapshot(bar.CloseTimeUtc, kVal, dVal, signal, intent);
        }

        public void OnExecution(ExecutionReport execution)
        {
            if (execution == null) throw new ArgumentNullException(nameof(execution));
            PositionQuantity = execution.PositionQuantity;
            if (PositionQuantity == 0)
            {
                bulletCount = 0;
                firstEntryPrice = 0.0;
            }
            else if (bulletCount == 1 && firstEntryPrice == 0.0)
            {
                firstEntryPrice = execution.FillPrice;
            }
        }

        public void Reset()
        {
            stochastic.Reset();
            Array.Clear(rollingHighs, 0, rollingHighs.Length);
            Array.Clear(rollingLows, 0, rollingLows.Length);
            rollingIndex = 0;
            rollingCount = 0;
            currentDay = null;
            currentDayHigh = null;
            currentDayLow = null;
            previousDayHigh = null;
            previousDayLow = null;
            previousK = null;
            previousBarTime = null;
            PositionQuantity = 0;
            BarsProcessed = 0;
            bulletCount = 0;
            firstEntryPrice = 0.0;
        }

        private bool IsNearRoundNumber(double price)
        {
            double remainder = price % RoundNumberInterval;
            if (remainder < 0) remainder += RoundNumberInterval;
            return remainder <= RoundNumberTolerance || (RoundNumberInterval - remainder) <= RoundNumberTolerance;
        }
    }
}
