using System;

namespace Futures.Core
{
    public sealed class VwapDeltaPullbackParameters
    {
        public VwapDeltaPullbackParameters(
            int quantity = 1,
            TimeSpan? anchorTimeUtc = null,
            int slopeLookbackBars = 10,
            double extendedDistancePoints = 6.0,
            double minSlopePoints = 0.5,
            double pullbackTolerancePoints = 2.5,
            int atrPeriod = 14,
            double atrStopMultiplier = 1.5,
            double atrTargetMultiplier = 2.5,
            long minAbsDelta = 3,
            double minAbsDeltaRatio = 0.005,
            int? stopLossTicks = null,
            int? takeProfitTicks = null,
            int? breakEvenTriggerTicks = null,
            int breakEvenOffsetTicks = 0,
            int? trailingStopTicks = null,
            double tickSize = 0.25)
        {
            if (quantity < 1)
                throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be at least one.");
            if (slopeLookbackBars < 1)
                throw new ArgumentOutOfRangeException(nameof(slopeLookbackBars), "Slope lookback bars must be at least one.");
            if (atrPeriod < 1)
                throw new ArgumentOutOfRangeException(nameof(atrPeriod), "ATR period must be at least one.");
            Guard.PositiveFinite(tickSize, nameof(tickSize));

            Quantity = quantity;
            AnchorTimeUtc = anchorTimeUtc ?? new TimeSpan(13, 30, 0);
            SlopeLookbackBars = slopeLookbackBars;
            ExtendedDistancePoints = extendedDistancePoints;
            MinSlopePoints = minSlopePoints;
            PullbackTolerancePoints = pullbackTolerancePoints;
            AtrPeriod = atrPeriod;
            AtrStopMultiplier = atrStopMultiplier;
            AtrTargetMultiplier = atrTargetMultiplier;
            MinAbsDelta = minAbsDelta;
            MinAbsDeltaRatio = minAbsDeltaRatio;
            StopLossTicks = stopLossTicks;
            TakeProfitTicks = takeProfitTicks;
            BreakEvenTriggerTicks = breakEvenTriggerTicks;
            BreakEvenOffsetTicks = breakEvenOffsetTicks;
            TrailingStopTicks = trailingStopTicks;
            TickSize = tickSize;
        }

        public int Quantity { get; }
        public TimeSpan AnchorTimeUtc { get; }
        public int SlopeLookbackBars { get; }
        public double ExtendedDistancePoints { get; }
        public double MinSlopePoints { get; }
        public double PullbackTolerancePoints { get; }
        public int AtrPeriod { get; }
        public double AtrStopMultiplier { get; }
        public double AtrTargetMultiplier { get; }
        public long MinAbsDelta { get; }
        public double MinAbsDeltaRatio { get; }
        public int? StopLossTicks { get; }
        public int? TakeProfitTicks { get; }
        public int? BreakEvenTriggerTicks { get; }
        public int BreakEvenOffsetTicks { get; }
        public int? TrailingStopTicks { get; }
        public double TickSize { get; }
    }

    /// <summary>
    /// Strategy combining session-anchored VWAP (dav3e_vwap parity), Wilder ATR, and Order Flow Bid/Ask Delta (dav3e_hud parity).
    /// Enters on trend-aligned pullbacks to VWAP when price is not over-extended and aggressive delta confirms rejection.
    /// </summary>
    public sealed class VwapDeltaPullbackStrategy : IStrategy
    {
        private readonly AnchoredVwap vwap;
        private readonly WilderAtr atr;
        private DateTime? previousBarTime;

        public VwapDeltaPullbackStrategy(VwapDeltaPullbackParameters? parameters = null)
        {
            Parameters = parameters ?? new VwapDeltaPullbackParameters();
            vwap = new AnchoredVwap(
                Parameters.AnchorTimeUtc,
                TimeSpan.Zero,
                1.0,
                2.0,
                Parameters.SlopeLookbackBars,
                Parameters.ExtendedDistancePoints);
            atr = new WilderAtr(Parameters.AtrPeriod);
        }

        public string Name => "VwapDeltaPullback";
        public VwapDeltaPullbackParameters Parameters { get; }
        public int PositionQuantity { get; private set; }
        public long BarsProcessed { get; private set; }
        public VwapSnapshot? LastVwap => vwap.Current;
        public double? LastAtr => atr.Value;

        public StrategySnapshot OnBarClose(Bar bar, StrategyContext context)
        {
            if (bar == null)
                throw new ArgumentNullException(nameof(bar));
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (previousBarTime.HasValue && bar.CloseTimeUtc <= previousBarTime.Value)
                throw new ArgumentException("Bars must be supplied once, in strictly increasing UTC order.", nameof(bar));

            VwapSnapshot vwapSnap = vwap.Update(bar);
            double currentAtr = atr.Update(bar);
            PositionQuantity = context.PositionQuantity;

            StrategySignal signal = EvaluateSignal(bar, vwapSnap);
            OrderIntent? intent = CreateIntent(signal, context, currentAtr);

            previousBarTime = bar.CloseTimeUtc;
            BarsProcessed++;

            return new StrategySnapshot(
                bar.CloseTimeUtc,
                bar.Close,
                vwapSnap.Vwap,
                signal,
                intent);
        }

        public void OnExecution(ExecutionReport execution)
        {
            if (execution == null)
                throw new ArgumentNullException(nameof(execution));
            PositionQuantity = execution.PositionQuantity;
        }

        public void Reset()
        {
            vwap.Reset();
            atr.Reset();
            previousBarTime = null;
            PositionQuantity = 0;
            BarsProcessed = 0;
        }

        private StrategySignal EvaluateSignal(Bar bar, VwapSnapshot vwapSnap)
        {
            if (!vwapSnap.Vwap.HasValue || !atr.IsReady || vwapSnap.IsExtended)
                return StrategySignal.None;

            double vwapVal = vwapSnap.Vwap.Value;
            double slope = vwapSnap.SlopePoints ?? 0.0;
            double tolerance = Math.Max(Parameters.PullbackTolerancePoints, (atr.Value ?? 1.0) * 0.6);

            bool orderFlowLongOk = !bar.HasOrderFlow
                || (bar.DeltaVolume >= Parameters.MinAbsDelta && bar.DeltaRatio >= Parameters.MinAbsDeltaRatio);
            bool orderFlowShortOk = !bar.HasOrderFlow
                || (bar.DeltaVolume <= -Parameters.MinAbsDelta && bar.DeltaRatio <= -Parameters.MinAbsDeltaRatio);

            bool bullishRejection = bar.Close > vwapVal
                && bar.Close > bar.Open
                && bar.Low <= vwapVal + tolerance
                && slope >= Parameters.MinSlopePoints
                && orderFlowLongOk;

            if (bullishRejection)
                return StrategySignal.Long;

            bool bearishRejection = bar.Close < vwapVal
                && bar.Close < bar.Open
                && bar.High >= vwapVal - tolerance
                && slope <= -Parameters.MinSlopePoints
                && orderFlowShortOk;

            if (bearishRejection)
                return StrategySignal.Short;

            return StrategySignal.None;
        }

        private OrderIntent? CreateIntent(StrategySignal signal, StrategyContext context, double currentAtr)
        {
            if (signal == StrategySignal.None || context.HasPendingOrder)
                return null;

            int direction = signal == StrategySignal.Long ? 1 : -1;
            if (context.PositionQuantity == 0)
            {
                int slTicks = Parameters.StopLossTicks
                    ?? Math.Max(4, (int)Math.Round((currentAtr * Parameters.AtrStopMultiplier) / Parameters.TickSize));
                int tpTicks = Parameters.TakeProfitTicks
                    ?? Math.Max(6, (int)Math.Round((currentAtr * Parameters.AtrTargetMultiplier) / Parameters.TickSize));

                return new OrderIntent(
                    direction * Parameters.Quantity,
                    "VWAP + Delta pullback entry",
                    slTicks,
                    tpTicks,
                    Parameters.BreakEvenTriggerTicks,
                    Parameters.BreakEvenOffsetTicks,
                    Parameters.TrailingStopTicks);
            }

            if (Math.Sign(context.PositionQuantity) != direction)
            {
                return new OrderIntent(0, "Opposite VWAP + Delta signal exit");
            }

            return null;
        }
    }
}
