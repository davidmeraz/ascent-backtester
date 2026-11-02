using System;
using System.Collections.Generic;

namespace Futures.Core
{
    public sealed class VwapSnapshot
    {
        public VwapSnapshot(
            DateTime closeTimeUtc,
            double? vwap,
            double? standardDeviation,
            double? upperBand1,
            double? lowerBand1,
            double? upperBand2,
            double? lowerBand2,
            double? distancePoints,
            double? slopePoints,
            bool isExtended)
        {
            CloseTimeUtc = closeTimeUtc;
            Vwap = vwap;
            StandardDeviation = standardDeviation;
            UpperBand1 = upperBand1;
            LowerBand1 = lowerBand1;
            UpperBand2 = upperBand2;
            LowerBand2 = lowerBand2;
            DistancePoints = distancePoints;
            SlopePoints = slopePoints;
            IsExtended = isExtended;
        }

        public DateTime CloseTimeUtc { get; }
        public double? Vwap { get; }
        public double? StandardDeviation { get; }
        public double? UpperBand1 { get; }
        public double? LowerBand1 { get; }
        public double? UpperBand2 { get; }
        public double? LowerBand2 { get; }
        public double? DistancePoints { get; }
        public double? SlopePoints { get; }
        public bool IsExtended { get; }
        public bool HasValue => Vwap.HasValue;
    }

    /// <summary>
    /// Session-anchored Volume Weighted Average Price with exact closed-bar parity to dav3e_vwap:
    /// - Typical price = (High + Low + Close) / 3, skipping zero-volume bars
    /// - Anchors at AnchorTimeUtc each UTC day (default 13:30:00 UTC = 06:30 UTC-7)
    /// - Resets on UTC date change
    /// - Computes volume-weighted sigma bands, canonical lookback slope, and extended flag
    /// </summary>
    public sealed class AnchoredVwap
    {
        private readonly List<double> sessionVwapHistory = new List<double>(512);
        private double cumPv;
        private double cumVol;
        private double cumPv2;
        private bool anchored;
        private bool sawIdleBar;
        private DateTime currentDayUtc;

        public AnchoredVwap(
            TimeSpan? anchorTimeUtc = null,
            TimeSpan? dayEndTimeUtc = null,
            double band1Multiplier = 1.0,
            double band2Multiplier = 2.0,
            int slopeLookbackBars = 10,
            double extendedDistancePoints = 6.0)
        {
            Guard.PositiveFinite(band1Multiplier, nameof(band1Multiplier));
            Guard.PositiveFinite(band2Multiplier, nameof(band2Multiplier));
            if (slopeLookbackBars < 1)
                throw new ArgumentOutOfRangeException(nameof(slopeLookbackBars), "Slope lookback bars must be at least one.");
            Guard.Finite(extendedDistancePoints, nameof(extendedDistancePoints));
            if (extendedDistancePoints < 0)
                throw new ArgumentOutOfRangeException(nameof(extendedDistancePoints), "Extended distance cannot be negative.");

            AnchorTimeUtc = anchorTimeUtc ?? new TimeSpan(13, 30, 0);
            DayEndTimeUtc = dayEndTimeUtc ?? TimeSpan.Zero;
            Band1Multiplier = band1Multiplier;
            Band2Multiplier = band2Multiplier;
            SlopeLookbackBars = slopeLookbackBars;
            ExtendedDistancePoints = extendedDistancePoints;
        }

        public TimeSpan AnchorTimeUtc { get; }
        public TimeSpan DayEndTimeUtc { get; }
        public double Band1Multiplier { get; }
        public double Band2Multiplier { get; }
        public int SlopeLookbackBars { get; }
        public double ExtendedDistancePoints { get; }
        public VwapSnapshot? Current { get; private set; }
        public double? Value => Current?.Vwap;

        public VwapSnapshot Update(Bar bar)
        {
            if (bar == null)
                throw new ArgumentNullException(nameof(bar));

            // Bar.CloseTimeUtc is the exclusive closing boundary; bar open time is 1 minute earlier
            // or for sub-minute/tick bars we evaluate the bar timestamp within the UTC day.
            DateTime barDay = bar.CloseTimeUtc.Date;
            TimeSpan timeOfDay = bar.CloseTimeUtc.TimeOfDay;

            // If CloseTimeUtc is exactly 00:00:00 UTC, it belongs to the bar that opened at 23:59 of previous day,
            // or represents midnight cutoff. Match dav3e_vwap's partition by calendar date & anchor window.
            if (barDay != currentDayUtc)
            {
                currentDayUtc = barDay;
                anchored = false;
                sessionVwapHistory.Clear();
            }

            bool live = timeOfDay >= AnchorTimeUtc
                && (DayEndTimeUtc <= AnchorTimeUtc || timeOfDay < DayEndTimeUtc);

            if (!live)
            {
                anchored = false;
                sawIdleBar = true;
                sessionVwapHistory.Clear();
            }
            else if (!anchored && (sawIdleBar || timeOfDay == AnchorTimeUtc || sessionVwapHistory.Count == 0))
            {
                anchored = true;
                cumPv = 0.0;
                cumVol = 0.0;
                cumPv2 = 0.0;
                sessionVwapHistory.Clear();
            }

            if (!anchored)
            {
                Current = new VwapSnapshot(bar.CloseTimeUtc, null, null, null, null, null, null, null, null, false);
                return Current;
            }

            if (bar.Volume > 0)
            {
                double tp = bar.TypicalPrice;
                double vol = bar.Volume;
                cumPv += tp * vol;
                cumVol += vol;
                cumPv2 += tp * tp * vol;
            }

            if (cumVol <= 0.0)
            {
                Current = new VwapSnapshot(bar.CloseTimeUtc, null, null, null, null, null, null, null, null, false);
                return Current;
            }

            double vwap = cumPv / cumVol;
            double variance = (cumPv2 / cumVol) - (vwap * vwap);
            double sd = variance > 0.0 ? Math.Sqrt(variance) : 0.0;
            double dist = bar.Close - vwap;

            sessionVwapHistory.Add(vwap);
            int currentIdx = sessionVwapHistory.Count - 1;
            int lookbackIdx = Math.Max(0, currentIdx - SlopeLookbackBars);
            double? slope = currentIdx > 0 ? vwap - sessionVwapHistory[lookbackIdx] : (double?)0.0;
            bool isExtended = ExtendedDistancePoints > 0.0 && Math.Abs(dist) >= ExtendedDistancePoints;

            Current = new VwapSnapshot(
                bar.CloseTimeUtc,
                vwap,
                sd,
                vwap + Band1Multiplier * sd,
                vwap - Band1Multiplier * sd,
                vwap + Band2Multiplier * sd,
                vwap - Band2Multiplier * sd,
                dist,
                slope,
                isExtended);

            return Current;
        }

        public void Reset()
        {
            cumPv = 0.0;
            cumVol = 0.0;
            cumPv2 = 0.0;
            anchored = false;
            sawIdleBar = false;
            currentDayUtc = default;
            sessionVwapHistory.Clear();
            Current = null;
        }
    }
}
