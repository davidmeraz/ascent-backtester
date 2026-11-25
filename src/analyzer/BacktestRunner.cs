using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Futures.Core;

namespace Futures.Analyzer;

/// <summary>
/// Executes strategies over minute bars or tick-by-tick streams with Stop-First intrabar resolution,
/// gap-aware fills, chronological MAE/MFE &amp; trailing drawdown tracking, Break-Even / Trailing Stop management,
/// session windows, and daily risk limits.
/// </summary>
public sealed class BacktestRunner
{
    public BacktestResult Run(BacktestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Instrument);
        if (!double.IsFinite(request.CommissionPerContractPerSide) || request.CommissionPerContractPerSide < 0)
            throw new ArgumentOutOfRangeException(nameof(request.CommissionPerContractPerSide));
        if (request.SlippageTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(request.SlippageTicks));

        if (request.Ticks is { Count: > 0 })
            return RunTickByTick(request, cancellationToken);

        ArgumentNullException.ThrowIfNull(request.Bars);
        if (request.Bars.Count == 0)
            throw new ArgumentException("At least one bar or tick is required.", nameof(request));

        var strategy = request.Strategy ?? new SmaCrossoverStrategy(new StrategyParameters(
            request.FastPeriod,
            request.SlowPeriod,
            request.Quantity,
            request.StopLossTicks,
            request.TakeProfitTicks,
            request.BreakEvenTriggerTicks,
            request.BreakEvenOffsetTicks,
            request.TrailingStopTicks));
        strategy.Reset();

        var signals = new List<SignalTrace>(request.Bars.Count);
        var trades = new List<TradeRecord>();

        var position = 0;
        var entryTime = default(DateTime);
        var entryPrice = 0d;
        var entryCommission = 0d;
        double? initialStopPrice = null;
        double? stopPrice = null;
        double? targetPrice = null;
        int? activeBreakEvenTriggerTicks = null;
        int activeBreakEvenOffsetTicks = 0;
        int? activeTrailingStopTicks = null;
        bool breakEvenActivated = false;
        double bestFavorablePrice = 0d;

        var currentMaeUsd = 0d;
        var currentMfeUsd = 0d;
        var tradePeakEquityUsd = 0d;
        var tradePeakToTroughDdUsd = 0d;

        var realizedEquityUsd = 0d;
        var globalIntradayPeakEquityUsd = 0d;
        var maxIntradayTrailingDrawdownUsd = 0d;
        var paidCommissions = 0d;

        PendingDecisionRecord? pending = null;
        DateTime? previousClose = null;
        DateTime currentDay = default;
        var dayRealizedPnl = 0d;
        var dayTrades = 0;

        double slippagePoints = request.SlippageTicks * request.Instrument.TickSize;
        int totalBars = request.Bars.Count;
        int barReportInterval = Math.Max(2000, totalBars / 100);
        int barIndex = 0;

        foreach (var bar in request.Bars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            barIndex++;
            if (barIndex % barReportInterval == 0)
            {
                request.Progress?.Report((double)barIndex / totalBars);
            }
            if (bar is null)
                throw new ArgumentException("Bars may not contain null entries.", nameof(request));
            if (bar.CloseTimeUtc.Second != 0 || bar.CloseTimeUtc.Ticks % TimeSpan.TicksPerMinute != 0)
                throw new ArgumentException("Minute runner requires UTC minute-close bars.", nameof(request));
            if (previousClose.HasValue && bar.CloseTimeUtc <= previousClose.Value)
                throw new ArgumentException("Bars must have strictly increasing close timestamps.", nameof(request));
            previousClose = bar.CloseTimeUtc;

            var barOpenTime = bar.CloseTimeUtc.AddMinutes(-1);
            if (barOpenTime.Date != currentDay)
            {
                currentDay = barOpenTime.Date;
                dayRealizedPnl = 0d;
                dayTrades = 0;
            }

            bool inSession = IsInSession(TimeOnly.FromDateTime(barOpenTime), request.SessionStartUtc, request.SessionEndUtc);

            // 1. Execute pending market orders at bar open
            if (pending is not null)
            {
                int targetQty = pending.TargetPositionQuantity;
                if (position == 0)
                {
                    if (targetQty != 0 && inSession && !IsDailyLimitLocked())
                    {
                        var dir = Math.Sign(targetQty);
                        var fill = bar.Open + dir * slippagePoints;
                        var comm = Math.Abs(targetQty) * request.CommissionPerContractPerSide;
                        paidCommissions += comm;
                        OpenNewPosition(barOpenTime, targetQty, fill, comm, pending);
                        strategy.OnExecution(new ExecutionReport(barOpenTime, position, fill));
                    }
                }
                else if (targetQty == 0)
                {
                    var dir = Math.Sign(position);
                    var fill = bar.Open - dir * slippagePoints;
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    ObservePrice(fill);
                    CloseActivePosition(barOpenTime, fill, pending.Reason, exitComm);
                    strategy.OnExecution(new ExecutionReport(barOpenTime, 0, fill));
                }
                else if (Math.Sign(targetQty) != Math.Sign(position))
                {
                    // Reversal: first close existing position cleanly, then open opposite if allowed
                    var prevDir = Math.Sign(position);
                    var exitFill = bar.Open - prevDir * slippagePoints;
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    ObservePrice(exitFill);
                    CloseActivePosition(barOpenTime, exitFill, pending.Reason, exitComm);
                    strategy.OnExecution(new ExecutionReport(barOpenTime, 0, exitFill));

                    if (inSession && !IsDailyLimitLocked())
                    {
                        var newDir = Math.Sign(targetQty);
                        var entryFill = bar.Open + newDir * slippagePoints;
                        var entryComm = Math.Abs(targetQty) * request.CommissionPerContractPerSide;
                        paidCommissions += entryComm;
                        OpenNewPosition(barOpenTime, targetQty, entryFill, entryComm, pending);
                        strategy.OnExecution(new ExecutionReport(barOpenTime, position, entryFill));
                    }
                }
                else if (Math.Sign(targetQty) == Math.Sign(position) && Math.Abs(targetQty) > Math.Abs(position) && inSession && !IsDailyLimitLocked())
                {
                    // Scaling in (Promediación / Bala #2)
                    int addedQty = targetQty - position;
                    var dir = Math.Sign(targetQty);
                    var fill = bar.Open + dir * slippagePoints;
                    var comm = Math.Abs(addedQty) * request.CommissionPerContractPerSide;
                    paidCommissions += comm;
                    entryCommission += comm;
                    entryPrice = ((entryPrice * Math.Abs(position)) + (fill * Math.Abs(addedQty))) / Math.Abs(targetQty);
                    position = targetQty;
                    var slTicks = pending.StopLossTicks ?? request.StopLossTicks;
                    var tpTicks = pending.TakeProfitTicks ?? request.TakeProfitTicks;
                    if (slTicks.HasValue)
                        stopPrice = entryPrice - dir * slTicks.Value * request.Instrument.TickSize;
                    if (tpTicks.HasValue)
                        targetPrice = entryPrice + dir * tpTicks.Value * request.Instrument.TickSize;
                    ObservePrice(fill);
                    strategy.OnExecution(new ExecutionReport(barOpenTime, position, fill));
                }
                pending = null;
            }

            // 2. Evaluate active position intrabar (Gap-aware + Stop-First rule + chronological MAE/MFE)
            if (position != 0)
            {
                var dir = Math.Sign(position);
                var worstPrice = dir > 0 ? bar.Low : bar.High;
                var bestPrice = dir > 0 ? bar.High : bar.Low;

                bool stopHitAtOpen = stopPrice.HasValue && (dir > 0 ? bar.Open <= stopPrice.Value : bar.Open >= stopPrice.Value);
                bool targetHitAtOpen = !stopHitAtOpen && targetPrice.HasValue && (dir > 0 ? bar.Open >= targetPrice.Value : bar.Open <= targetPrice.Value);

                if (stopHitAtOpen)
                {
                    var exitFill = bar.Open - dir * slippagePoints;
                    ObservePrice(exitFill);
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    CloseActivePosition(bar.CloseTimeUtc, exitFill, "Stop loss", exitComm);
                    strategy.OnExecution(new ExecutionReport(bar.CloseTimeUtc, 0, exitFill));
                }
                else if (targetHitAtOpen)
                {
                    var exitFill = bar.Open;
                    ObservePrice(exitFill);
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    CloseActivePosition(bar.CloseTimeUtc, exitFill, "Take profit", exitComm);
                    strategy.OnExecution(new ExecutionReport(bar.CloseTimeUtc, 0, exitFill));
                }
                else
                {
                    ObservePrice(bar.Open);

                    bool stopHit = stopPrice.HasValue && (dir > 0 ? bar.Low <= stopPrice.Value : bar.High >= stopPrice.Value);
                    bool targetHit = targetPrice.HasValue && (dir > 0 ? bar.High >= targetPrice.Value : bar.Low <= targetPrice.Value);

                    // Realistic pessimistic order: Stop Loss is evaluated before Take Profit on the same bar
                    if (stopHit)
                    {
                        var exitFill = stopPrice!.Value - dir * slippagePoints;
                        ObservePrice(exitFill);
                        var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                        paidCommissions += exitComm;
                        CloseActivePosition(bar.CloseTimeUtc, exitFill, breakEvenActivated && Math.Abs(stopPrice.Value - entryPrice) < 1e-9 ? "Break-even stop" : "Stop loss", exitComm);
                        strategy.OnExecution(new ExecutionReport(bar.CloseTimeUtc, 0, exitFill));
                    }
                    else if (targetHit)
                    {
                        // Adverse excursion within the bar occurred before the limit target closed the position
                        ObservePrice(worstPrice);
                        var exitFill = targetPrice!.Value;
                        ObservePrice(exitFill);
                        var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                        paidCommissions += exitComm;
                        CloseActivePosition(bar.CloseTimeUtc, exitFill, "Take profit", exitComm);
                        strategy.OnExecution(new ExecutionReport(bar.CloseTimeUtc, 0, exitFill));
                    }
                    else
                    {
                        // Chronological intrabar path based on bar polarity
                        bool barFavorableClose = (dir > 0 && bar.Close >= bar.Open) || (dir < 0 && bar.Close <= bar.Open);
                        if (barFavorableClose)
                        {
                            ObservePrice(worstPrice);
                            ObservePrice(bestPrice);
                        }
                        else
                        {
                            ObservePrice(bestPrice);
                            ObservePrice(worstPrice);
                        }
                        ObservePrice(bar.Close);

                        UpdateDynamicStops(dir, bestPrice);

                        if (!IsInSession(TimeOnly.FromDateTime(bar.CloseTimeUtc), request.SessionStartUtc, request.SessionEndUtc))
                        {
                            var exitFill = bar.Close - dir * slippagePoints;
                            ObservePrice(exitFill);
                            var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                            paidCommissions += exitComm;
                            CloseActivePosition(bar.CloseTimeUtc, exitFill, "Session close", exitComm);
                            strategy.OnExecution(new ExecutionReport(bar.CloseTimeUtc, 0, exitFill));
                        }
                        else if (request.MaxDailyLossUsd is double maxLoss
                            && request.LiquidateOnDailyLossBreach
                            && (dayRealizedPnl + ((bar.Close - entryPrice) * position * request.Instrument.PointValue) - entryCommission - Math.Abs(position) * request.CommissionPerContractPerSide) <= -maxLoss)
                        {
                            var exitFill = bar.Close - dir * slippagePoints;
                            ObservePrice(exitFill);
                            var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                            paidCommissions += exitComm;
                            CloseActivePosition(bar.CloseTimeUtc, exitFill, "Daily loss limit", exitComm);
                            strategy.OnExecution(new ExecutionReport(bar.CloseTimeUtc, 0, exitFill));
                        }
                    }
                }
            }

            // 3. Evaluate strategy at bar close
            var snapshot = strategy.OnBarClose(bar, new StrategyContext(position, false));
            signals.Add(new SignalTrace(
                bar.CloseTimeUtc, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume,
                snapshot.Fast, snapshot.Slow, snapshot.Signal,
                snapshot.OrderIntent?.TargetPositionQuantity, position, false));
            if (snapshot.OrderIntent is { } order)
            {
                pending = new PendingDecisionRecord(
                    bar.CloseTimeUtc,
                    order.TargetPositionQuantity,
                    order.Reason,
                    order.StopLossTicks,
                    order.TakeProfitTicks,
                    order.BreakEvenTriggerTicks,
                    order.BreakEvenOffsetTicks,
                    order.TrailingStopTicks);
            }
        }

        var lastClose = request.Bars[request.Bars.Count - 1].Close;
        return BuildResult(
            request,
            strategy.Name,
            "minute-close signal; Stop-First intrabar SL/TP; chronological MAE/MFE & trailing DD",
            signals,
            trades,
            position,
            entryTime,
            entryPrice,
            paidCommissions,
            lastClose,
            pending,
            maxIntradayTrailingDrawdownUsd);

        bool IsDailyLimitLocked() =>
            (request.MaxDailyLossUsd is double maxLoss && dayRealizedPnl <= -maxLoss)
            || (request.MaxTradesPerDay is int maxTrades && dayTrades >= maxTrades);

        void OpenNewPosition(DateTime timeUtc, int qty, double fillPrice, double comm, PendingDecisionRecord order)
        {
            position = qty;
            entryTime = timeUtc;
            entryPrice = fillPrice;
            entryCommission = comm;
            currentMaeUsd = 0d;
            currentMfeUsd = 0d;
            tradePeakEquityUsd = realizedEquityUsd - entryCommission;
            tradePeakToTroughDdUsd = 0d;
            bestFavorablePrice = fillPrice;
            breakEvenActivated = false;

            var dir = Math.Sign(position);
            var slTicks = order.StopLossTicks ?? request.StopLossTicks;
            var tpTicks = order.TakeProfitTicks ?? request.TakeProfitTicks;
            activeBreakEvenTriggerTicks = order.BreakEvenTriggerTicks ?? request.BreakEvenTriggerTicks;
            activeBreakEvenOffsetTicks = order.BreakEvenTriggerTicks.HasValue ? order.BreakEvenOffsetTicks : request.BreakEvenOffsetTicks;
            activeTrailingStopTicks = order.TrailingStopTicks ?? request.TrailingStopTicks;

            initialStopPrice = slTicks.HasValue ? entryPrice - dir * slTicks.Value * request.Instrument.TickSize : null;
            stopPrice = initialStopPrice;
            targetPrice = tpTicks.HasValue ? entryPrice + dir * tpTicks.Value * request.Instrument.TickSize : null;

            ObservePrice(fillPrice);
        }

        void ObservePrice(double price)
        {
            var grossUnrealized = (price - entryPrice) * position * request.Instrument.PointValue;
            if (grossUnrealized < 0d)
                currentMaeUsd = Math.Max(currentMaeUsd, -grossUnrealized);
            else if (grossUnrealized > 0d)
                currentMfeUsd = Math.Max(currentMfeUsd, grossUnrealized);

            var floatingEquity = realizedEquityUsd + grossUnrealized - entryCommission;
            if (floatingEquity > tradePeakEquityUsd)
                tradePeakEquityUsd = floatingEquity;
            tradePeakToTroughDdUsd = Math.Max(tradePeakToTroughDdUsd, tradePeakEquityUsd - floatingEquity);

            if (floatingEquity > globalIntradayPeakEquityUsd)
                globalIntradayPeakEquityUsd = floatingEquity;
            maxIntradayTrailingDrawdownUsd = Math.Max(maxIntradayTrailingDrawdownUsd, globalIntradayPeakEquityUsd - floatingEquity);
        }

        void UpdateDynamicStops(int dir, double barBestPrice)
        {
            if ((dir > 0 && barBestPrice > bestFavorablePrice) || (dir < 0 && barBestPrice < bestFavorablePrice))
                bestFavorablePrice = barBestPrice;

            double tickSize = request.Instrument.TickSize;
            if (activeBreakEvenTriggerTicks.HasValue && !breakEvenActivated)
            {
                double favorablePoints = (bestFavorablePrice - entryPrice) * dir;
                if (favorablePoints >= activeBreakEvenTriggerTicks.Value * tickSize)
                {
                    breakEvenActivated = true;
                    double beStop = entryPrice + dir * activeBreakEvenOffsetTicks * tickSize;
                    if (!stopPrice.HasValue || (dir > 0 ? beStop > stopPrice.Value : beStop < stopPrice.Value))
                        stopPrice = beStop;
                }
            }

            if (activeTrailingStopTicks.HasValue)
            {
                double trailStop = bestFavorablePrice - dir * activeTrailingStopTicks.Value * tickSize;
                if (!stopPrice.HasValue || (dir > 0 ? trailStop > stopPrice.Value : trailStop < stopPrice.Value))
                    stopPrice = trailStop;
            }
        }

        void CloseActivePosition(DateTime exitTime, double exitFill, string reason, double exitCommission)
        {
            var dir = Math.Sign(position);
            var absQty = Math.Abs(position);
            var gross = (exitFill - entryPrice) * position * request.Instrument.PointValue;
            var roundTripCommission = entryCommission + exitCommission;
            var net = gross - roundTripCommission;

            realizedEquityUsd += net;
            if (realizedEquityUsd > globalIntradayPeakEquityUsd)
                globalIntradayPeakEquityUsd = realizedEquityUsd;
            maxIntradayTrailingDrawdownUsd = Math.Max(maxIntradayTrailingDrawdownUsd, globalIntradayPeakEquityUsd - realizedEquityUsd);
            tradePeakToTroughDdUsd = Math.Max(tradePeakToTroughDdUsd, tradePeakEquityUsd - realizedEquityUsd);

            double points = (exitFill - entryPrice) * dir;
            double ticksMoved = points / request.Instrument.TickSize;
            double durationSeconds = Math.Max(0d, (exitTime - entryTime).TotalSeconds);

            double? initialRiskUsd = initialStopPrice.HasValue
                ? Math.Abs(entryPrice - initialStopPrice.Value) * absQty * request.Instrument.PointValue + roundTripCommission
                : null;
            double? rMultiple = initialRiskUsd is > 0d ? net / initialRiskUsd.Value : null;
            double? mfeCapture = currentMfeUsd > 0d ? gross / currentMfeUsd : null;

            trades.Add(new TradeRecord(
                trades.Count + 1,
                position > 0 ? "Long" : "Short",
                absQty,
                entryTime,
                entryPrice,
                exitTime,
                exitFill,
                gross,
                roundTripCommission,
                net,
                currentMaeUsd,
                currentMfeUsd,
                reason,
                points,
                ticksMoved,
                durationSeconds,
                initialRiskUsd,
                rMultiple,
                mfeCapture,
                tradePeakToTroughDdUsd,
                realizedEquityUsd));

            dayRealizedPnl += net;
            dayTrades++;
            position = 0;
            entryCommission = 0d;
            initialStopPrice = null;
            stopPrice = null;
            targetPrice = null;
            breakEvenActivated = false;
        }
    }

    private BacktestResult RunTickByTick(BacktestRequest request, CancellationToken cancellationToken)
    {
        var strategy = request.Strategy ?? new SmaCrossoverStrategy(new StrategyParameters(
            request.FastPeriod,
            request.SlowPeriod,
            request.Quantity,
            request.StopLossTicks,
            request.TakeProfitTicks,
            request.BreakEvenTriggerTicks,
            request.BreakEvenOffsetTicks,
            request.TrailingStopTicks));
        strategy.Reset();

        var signals = new List<SignalTrace>();
        var trades = new List<TradeRecord>();

        var position = 0;
        var entryTime = default(DateTime);
        var entryPrice = 0d;
        var entryCommission = 0d;
        double? initialStopPrice = null;
        double? stopPrice = null;
        double? targetPrice = null;
        int? activeBreakEvenTriggerTicks = null;
        int activeBreakEvenOffsetTicks = 0;
        int? activeTrailingStopTicks = null;
        bool breakEvenActivated = false;
        double bestFavorablePrice = 0d;

        var currentMaeUsd = 0d;
        var currentMfeUsd = 0d;
        var tradePeakEquityUsd = 0d;
        var tradePeakToTroughDdUsd = 0d;

        var realizedEquityUsd = 0d;
        var globalIntradayPeakEquityUsd = 0d;
        var maxIntradayTrailingDrawdownUsd = 0d;
        var paidCommissions = 0d;

        PendingDecisionRecord? pending = null;
        DateTime currentDay = default;
        var dayRealizedPnl = 0d;
        var dayTrades = 0;

        DateTime? bucketCloseTime = null;
        double barOpen = 0, barHigh = 0, barLow = 0, barClose = 0;
        long barVolume = 0, barDeltaVolume = 0, barAskVolume = 0, barBidVolume = 0, barTickCount = 0;

        double slippagePoints = request.SlippageTicks * request.Instrument.TickSize;
        int totalTicks = request.Ticks.Count;
        int tickReportInterval = Math.Max(10000, totalTicks / 100);
        int tickIndex = 0;

        foreach (var tick in request.Ticks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tickIndex++;
            if (tickIndex % tickReportInterval == 0)
            {
                request.Progress?.Report((double)tickIndex / totalTicks);
            }
            if (tick is null)
                throw new ArgumentException("Ticks may not contain null entries.", nameof(request));

            if (tick.TimeUtc.Date != currentDay)
            {
                currentDay = tick.TimeUtc.Date;
                dayRealizedPnl = 0d;
                dayTrades = 0;
            }

            var tickMinuteClose = new DateTime(
                tick.TimeUtc.Year, tick.TimeUtc.Month, tick.TimeUtc.Day,
                tick.TimeUtc.Hour, tick.TimeUtc.Minute, 0, DateTimeKind.Utc).AddMinutes(1);

            if (bucketCloseTime.HasValue && tickMinuteClose > bucketCloseTime.Value)
            {
                var closedBar = new Bar(
                    bucketCloseTime.Value,
                    barOpen, barHigh, barLow, barClose, barVolume,
                    barDeltaVolume, barAskVolume, barBidVolume, barTickCount);
                var snapshot = strategy.OnBarClose(closedBar, new StrategyContext(position, false));
                signals.Add(new SignalTrace(
                    closedBar.CloseTimeUtc, closedBar.Open, closedBar.High, closedBar.Low, closedBar.Close, closedBar.Volume,
                    snapshot.Fast, snapshot.Slow, snapshot.Signal,
                    snapshot.OrderIntent?.TargetPositionQuantity, position, false));
                if (snapshot.OrderIntent is { } order)
                {
                    pending = new PendingDecisionRecord(
                        closedBar.CloseTimeUtc,
                        order.TargetPositionQuantity,
                        order.Reason,
                        order.StopLossTicks,
                        order.TakeProfitTicks,
                        order.BreakEvenTriggerTicks,
                        order.BreakEvenOffsetTicks,
                        order.TrailingStopTicks);
                }
                bucketCloseTime = null;
            }

            if (!bucketCloseTime.HasValue)
            {
                bucketCloseTime = tickMinuteClose;
                barOpen = barHigh = barLow = barClose = tick.Price;
                barVolume = tick.Volume;
                long delta = tick.SignedDeltaVolume;
                barDeltaVolume = delta;
                barAskVolume = delta > 0 ? delta : 0L;
                barBidVolume = delta < 0 ? -delta : 0L;
                barTickCount = 1L;
            }
            else
            {
                barHigh = Math.Max(barHigh, tick.Price);
                barLow = Math.Min(barLow, tick.Price);
                barClose = tick.Price;
                barVolume += tick.Volume;
                long delta = tick.SignedDeltaVolume;
                barDeltaVolume += delta;
                if (delta > 0) barAskVolume += delta;
                else if (delta < 0) barBidVolume += -delta;
                barTickCount++;
            }

            bool inSession = IsInSession(TimeOnly.FromDateTime(tick.TimeUtc), request.SessionStartUtc, request.SessionEndUtc);

            // 1. Execute pending order on first available tick using Bid/Ask + slippage
            if (pending is not null)
            {
                int targetQty = pending.TargetPositionQuantity;
                if (position == 0)
                {
                    if (targetQty != 0 && inSession && !IsDailyLimitLocked())
                    {
                        var dir = Math.Sign(targetQty);
                        var rawBookPrice = dir > 0 ? tick.Ask : tick.Bid;
                        var fill = rawBookPrice + dir * slippagePoints;
                        var comm = Math.Abs(targetQty) * request.CommissionPerContractPerSide;
                        paidCommissions += comm;
                        OpenNewTickPosition(tick.TimeUtc, targetQty, fill, comm, pending);
                        strategy.OnExecution(new ExecutionReport(tick.TimeUtc, position, fill));
                    }
                }
                else if (targetQty == 0)
                {
                    var dir = Math.Sign(position);
                    var rawBookPrice = dir > 0 ? tick.Bid : tick.Ask;
                    var fill = rawBookPrice - dir * slippagePoints;
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    ObserveTickPrice(fill);
                    CloseTickPosition(tick.TimeUtc, fill, pending.Reason, exitComm);
                    strategy.OnExecution(new ExecutionReport(tick.TimeUtc, 0, fill));
                }
                else if (Math.Sign(targetQty) != Math.Sign(position))
                {
                    var prevDir = Math.Sign(position);
                    var exitBook = prevDir > 0 ? tick.Bid : tick.Ask;
                    var exitFill = exitBook - prevDir * slippagePoints;
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    ObserveTickPrice(exitFill);
                    CloseTickPosition(tick.TimeUtc, exitFill, pending.Reason, exitComm);
                    strategy.OnExecution(new ExecutionReport(tick.TimeUtc, 0, exitFill));

                    if (inSession && !IsDailyLimitLocked())
                    {
                        var newDir = Math.Sign(targetQty);
                        var entryBook = newDir > 0 ? tick.Ask : tick.Bid;
                        var entryFill = entryBook + newDir * slippagePoints;
                        var entryComm = Math.Abs(targetQty) * request.CommissionPerContractPerSide;
                        paidCommissions += entryComm;
                        OpenNewTickPosition(tick.TimeUtc, targetQty, entryFill, entryComm, pending);
                        strategy.OnExecution(new ExecutionReport(tick.TimeUtc, position, entryFill));
                    }
                }
                else if (Math.Sign(targetQty) == Math.Sign(position) && Math.Abs(targetQty) > Math.Abs(position) && inSession && !IsDailyLimitLocked())
                {
                    // Scaling in on tick (Promediación / Bala #2)
                    int addedQty = targetQty - position;
                    var dir = Math.Sign(targetQty);
                    var entryBook = dir > 0 ? tick.Ask : tick.Bid;
                    var fill = entryBook + dir * slippagePoints;
                    var comm = Math.Abs(addedQty) * request.CommissionPerContractPerSide;
                    paidCommissions += comm;
                    entryCommission += comm;
                    entryPrice = ((entryPrice * Math.Abs(position)) + (fill * Math.Abs(addedQty))) / Math.Abs(targetQty);
                    position = targetQty;
                    var slTicks = pending.StopLossTicks ?? request.StopLossTicks;
                    var tpTicks = pending.TakeProfitTicks ?? request.TakeProfitTicks;
                    if (slTicks.HasValue)
                        stopPrice = entryPrice - dir * slTicks.Value * request.Instrument.TickSize;
                    if (tpTicks.HasValue)
                        targetPrice = entryPrice + dir * tpTicks.Value * request.Instrument.TickSize;
                    ObserveTickPrice(fill);
                    strategy.OnExecution(new ExecutionReport(tick.TimeUtc, position, fill));
                }
                pending = null;
            }

            // 2. Evaluate active position on exact tick price & Level 1 book
            if (position != 0)
            {
                var dir = Math.Sign(position);
                var markPrice = dir > 0 ? tick.Bid : tick.Ask;

                if (stopPrice.HasValue && (dir > 0 ? tick.Price <= stopPrice.Value : tick.Price >= stopPrice.Value))
                {
                    var exitFill = markPrice - dir * slippagePoints;
                    ObserveTickPrice(exitFill);
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    CloseTickPosition(tick.TimeUtc, exitFill, "Stop loss (tick)", exitComm);
                    strategy.OnExecution(new ExecutionReport(tick.TimeUtc, 0, exitFill));
                }
                else if (targetPrice.HasValue && (dir > 0 ? tick.Price >= targetPrice.Value : tick.Price <= targetPrice.Value))
                {
                    var exitFill = targetPrice.Value;
                    ObserveTickPrice(exitFill);
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    CloseTickPosition(tick.TimeUtc, exitFill, "Take profit (tick)", exitComm);
                    strategy.OnExecution(new ExecutionReport(tick.TimeUtc, 0, exitFill));
                }
                else
                {
                    ObserveTickPrice(markPrice);
                    UpdateDynamicTickStops(dir, tick.Price);

                    if (!inSession)
                    {
                        var exitFill = markPrice - dir * slippagePoints;
                        ObserveTickPrice(exitFill);
                        var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                        paidCommissions += exitComm;
                        CloseTickPosition(tick.TimeUtc, exitFill, "Session close (tick)", exitComm);
                        strategy.OnExecution(new ExecutionReport(tick.TimeUtc, 0, exitFill));
                    }
                    else if (request.MaxDailyLossUsd is double maxLoss
                        && request.LiquidateOnDailyLossBreach
                        && (dayRealizedPnl + ((markPrice - entryPrice) * position * request.Instrument.PointValue) - entryCommission - Math.Abs(position) * request.CommissionPerContractPerSide) <= -maxLoss)
                    {
                        var exitFill = markPrice - dir * slippagePoints;
                        ObserveTickPrice(exitFill);
                        var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                        paidCommissions += exitComm;
                        CloseTickPosition(tick.TimeUtc, exitFill, "Daily loss limit (tick)", exitComm);
                        strategy.OnExecution(new ExecutionReport(tick.TimeUtc, 0, exitFill));
                    }
                }
            }
        }

        if (bucketCloseTime.HasValue)
        {
            var closedBar = new Bar(
                bucketCloseTime.Value,
                barOpen, barHigh, barLow, barClose, barVolume,
                barDeltaVolume, barAskVolume, barBidVolume, barTickCount);
            var snapshot = strategy.OnBarClose(closedBar, new StrategyContext(position, false));
            signals.Add(new SignalTrace(
                closedBar.CloseTimeUtc, closedBar.Open, closedBar.High, closedBar.Low, closedBar.Close, closedBar.Volume,
                snapshot.Fast, snapshot.Slow, snapshot.Signal,
                snapshot.OrderIntent?.TargetPositionQuantity, position, false));
            if (snapshot.OrderIntent is { } order)
            {
                pending = new PendingDecisionRecord(
                    closedBar.CloseTimeUtc,
                    order.TargetPositionQuantity,
                    order.Reason,
                    order.StopLossTicks,
                    order.TakeProfitTicks,
                    order.BreakEvenTriggerTicks,
                    order.BreakEvenOffsetTicks,
                    order.TrailingStopTicks);
            }
        }

        var lastPrice = request.Ticks[request.Ticks.Count - 1].Price;
        return BuildResult(
            request,
            strategy.Name,
            "tick-by-tick Bid/Ask execution; exact SL/TP and chronological MAE/MFE",
            signals,
            trades,
            position,
            entryTime,
            entryPrice,
            paidCommissions,
            lastPrice,
            pending,
            maxIntradayTrailingDrawdownUsd);

        bool IsDailyLimitLocked() =>
            (request.MaxDailyLossUsd is double maxLoss && dayRealizedPnl <= -maxLoss)
            || (request.MaxTradesPerDay is int maxTrades && dayTrades >= maxTrades);

        void OpenNewTickPosition(DateTime timeUtc, int qty, double fillPrice, double comm, PendingDecisionRecord order)
        {
            position = qty;
            entryTime = timeUtc;
            entryPrice = fillPrice;
            entryCommission = comm;
            currentMaeUsd = 0d;
            currentMfeUsd = 0d;
            tradePeakEquityUsd = realizedEquityUsd - entryCommission;
            tradePeakToTroughDdUsd = 0d;
            bestFavorablePrice = fillPrice;
            breakEvenActivated = false;

            var dir = Math.Sign(position);
            var slTicks = order.StopLossTicks ?? request.StopLossTicks;
            var tpTicks = order.TakeProfitTicks ?? request.TakeProfitTicks;
            activeBreakEvenTriggerTicks = order.BreakEvenTriggerTicks ?? request.BreakEvenTriggerTicks;
            activeBreakEvenOffsetTicks = order.BreakEvenTriggerTicks.HasValue ? order.BreakEvenOffsetTicks : request.BreakEvenOffsetTicks;
            activeTrailingStopTicks = order.TrailingStopTicks ?? request.TrailingStopTicks;

            initialStopPrice = slTicks.HasValue ? entryPrice - dir * slTicks.Value * request.Instrument.TickSize : null;
            stopPrice = initialStopPrice;
            targetPrice = tpTicks.HasValue ? entryPrice + dir * tpTicks.Value * request.Instrument.TickSize : null;

            ObserveTickPrice(fillPrice);
        }

        void ObserveTickPrice(double price)
        {
            var grossUnrealized = (price - entryPrice) * position * request.Instrument.PointValue;
            if (grossUnrealized < 0d)
                currentMaeUsd = Math.Max(currentMaeUsd, -grossUnrealized);
            else if (grossUnrealized > 0d)
                currentMfeUsd = Math.Max(currentMfeUsd, grossUnrealized);

            var floatingEquity = realizedEquityUsd + grossUnrealized - entryCommission;
            if (floatingEquity > tradePeakEquityUsd)
                tradePeakEquityUsd = floatingEquity;
            tradePeakToTroughDdUsd = Math.Max(tradePeakToTroughDdUsd, tradePeakEquityUsd - floatingEquity);

            if (floatingEquity > globalIntradayPeakEquityUsd)
                globalIntradayPeakEquityUsd = floatingEquity;
            maxIntradayTrailingDrawdownUsd = Math.Max(maxIntradayTrailingDrawdownUsd, globalIntradayPeakEquityUsd - floatingEquity);
        }

        void UpdateDynamicTickStops(int dir, double tickPrice)
        {
            if ((dir > 0 && tickPrice > bestFavorablePrice) || (dir < 0 && tickPrice < bestFavorablePrice))
                bestFavorablePrice = tickPrice;

            double tickSize = request.Instrument.TickSize;
            if (activeBreakEvenTriggerTicks.HasValue && !breakEvenActivated)
            {
                double favorablePoints = (bestFavorablePrice - entryPrice) * dir;
                if (favorablePoints >= activeBreakEvenTriggerTicks.Value * tickSize)
                {
                    breakEvenActivated = true;
                    double beStop = entryPrice + dir * activeBreakEvenOffsetTicks * tickSize;
                    if (!stopPrice.HasValue || (dir > 0 ? beStop > stopPrice.Value : beStop < stopPrice.Value))
                        stopPrice = beStop;
                }
            }

            if (activeTrailingStopTicks.HasValue)
            {
                double trailStop = bestFavorablePrice - dir * activeTrailingStopTicks.Value * tickSize;
                if (!stopPrice.HasValue || (dir > 0 ? trailStop > stopPrice.Value : trailStop < stopPrice.Value))
                    stopPrice = trailStop;
            }
        }

        void CloseTickPosition(DateTime exitTime, double exitFill, string reason, double exitCommission)
        {
            var dir = Math.Sign(position);
            var absQty = Math.Abs(position);
            var gross = (exitFill - entryPrice) * position * request.Instrument.PointValue;
            var roundTripCommission = entryCommission + exitCommission;
            var net = gross - roundTripCommission;

            realizedEquityUsd += net;
            if (realizedEquityUsd > globalIntradayPeakEquityUsd)
                globalIntradayPeakEquityUsd = realizedEquityUsd;
            maxIntradayTrailingDrawdownUsd = Math.Max(maxIntradayTrailingDrawdownUsd, globalIntradayPeakEquityUsd - realizedEquityUsd);
            tradePeakToTroughDdUsd = Math.Max(tradePeakToTroughDdUsd, tradePeakEquityUsd - realizedEquityUsd);

            double points = (exitFill - entryPrice) * dir;
            double ticksMoved = points / request.Instrument.TickSize;
            double durationSeconds = Math.Max(0d, (exitTime - entryTime).TotalSeconds);

            double? initialRiskUsd = initialStopPrice.HasValue
                ? Math.Abs(entryPrice - initialStopPrice.Value) * absQty * request.Instrument.PointValue + roundTripCommission
                : null;
            double? rMultiple = initialRiskUsd is > 0d ? net / initialRiskUsd.Value : null;
            double? mfeCapture = currentMfeUsd > 0d ? gross / currentMfeUsd : null;

            trades.Add(new TradeRecord(
                trades.Count + 1,
                position > 0 ? "Long" : "Short",
                absQty,
                entryTime,
                entryPrice,
                exitTime,
                exitFill,
                gross,
                roundTripCommission,
                net,
                currentMaeUsd,
                currentMfeUsd,
                reason,
                points,
                ticksMoved,
                durationSeconds,
                initialRiskUsd,
                rMultiple,
                mfeCapture,
                tradePeakToTroughDdUsd,
                realizedEquityUsd));

            dayRealizedPnl += net;
            dayTrades++;
            position = 0;
            entryCommission = 0d;
            initialStopPrice = null;
            stopPrice = null;
            targetPrice = null;
            breakEvenActivated = false;
        }
    }

    private static bool IsInSession(TimeOnly time, TimeOnly? start, TimeOnly? end)
    {
        if (!start.HasValue || !end.HasValue) return true;
        return start.Value <= end.Value
            ? time >= start.Value && time < end.Value
            : time >= start.Value || time < end.Value;
    }

    private static BacktestResult BuildResult(
        BacktestRequest request,
        string strategyName,
        string executionModel,
        List<SignalTrace> signals,
        List<TradeRecord> trades,
        int position,
        DateTime entryTime,
        double entryPrice,
        double paidCommissions,
        double lastClose,
        PendingDecisionRecord? pending,
        double trackedIntradayTrailingDrawdown)
    {
        OpenPositionRecord? openPosition = position == 0
            ? null
            : new OpenPositionRecord(
                position,
                entryTime,
                entryPrice,
                Math.Abs(position) * request.CommissionPerContractPerSide,
                lastClose,
                (lastClose - entryPrice) * position * request.Instrument.PointValue);

        int closedCount = trades.Count;
        var winTrades = trades.Where(t => t.NetProfit > 0).ToList();
        var lossTrades = trades.Where(t => t.NetProfit < 0).ToList();
        int winningCount = winTrades.Count;
        int losingCount = lossTrades.Count;
        int breakevenCount = trades.Count(t => t.NetProfit == 0);

        double grossWinsUsd = winTrades.Sum(t => t.NetProfit);
        double grossLossesUsd = -lossTrades.Sum(t => t.NetProfit);
        double totalGrossProfit = trades.Sum(t => t.GrossProfit);
        double closedCommissions = trades.Sum(t => t.Commission);
        double totalNetProfit = trades.Sum(t => t.NetProfit);

        double winRate = closedCount == 0 ? 0d : (double)winningCount / closedCount;
        double? profitFactor = grossLossesUsd == 0d
            ? (grossWinsUsd > 0d ? double.PositiveInfinity : null)
            : grossWinsUsd / grossLossesUsd;
        if (profitFactor.HasValue && !double.IsFinite(profitFactor.Value))
            profitFactor = null;

        double avgWinUsd = winningCount == 0 ? 0d : grossWinsUsd / winningCount;
        double avgLossUsd = losingCount == 0 ? 0d : lossTrades.Average(t => t.NetProfit);
        double? payoffRatio = avgLossUsd < 0d ? avgWinUsd / Math.Abs(avgLossUsd) : null;
        double expectancyUsd = closedCount == 0 ? 0d : totalNetProfit / closedCount;
        double largestWinUsd = winningCount == 0 ? 0d : winTrades.Max(t => t.NetProfit);
        double largestLossUsd = losingCount == 0 ? 0d : lossTrades.Min(t => t.NetProfit);

        var rMultiples = trades.Where(t => t.RMultiple.HasValue).Select(t => t.RMultiple!.Value).ToList();
        double? avgRMultiple = rMultiples.Count > 0 ? rMultiples.Average() : null;

        // Closed-trade equity curve, streaks, and daily aggregation
        double equity = 0d;
        double closedPeak = 0d;
        double maxClosedDrawdown = 0d;
        int currentWinStreak = 0, maxWinStreak = 0;
        int currentLossStreak = 0, maxLossStreak = 0;

        foreach (var trade in trades)
        {
            equity += trade.NetProfit;
            closedPeak = Math.Max(closedPeak, equity);
            maxClosedDrawdown = Math.Max(maxClosedDrawdown, closedPeak - equity);

            if (trade.NetProfit > 0)
            {
                currentWinStreak++;
                currentLossStreak = 0;
                if (currentWinStreak > maxWinStreak) maxWinStreak = currentWinStreak;
            }
            else if (trade.NetProfit < 0)
            {
                currentLossStreak++;
                currentWinStreak = 0;
                if (currentLossStreak > maxLossStreak) maxLossStreak = currentLossStreak;
            }
            else
            {
                currentWinStreak = 0;
                currentLossStreak = 0;
            }
        }

        double maxIntradayTrailingDrawdown = Math.Max(maxClosedDrawdown, trackedIntradayTrailingDrawdown);

        // Daily breakdown and Sharpe / Sortino calculation
        var dailyRecords = new List<DailyPerformanceRecord>();
        double eodCumEquity = 0d;
        double eodPeak = 0d;
        foreach (var dayGroup in trades.GroupBy(t => t.ExitTimeUtc.Date).OrderBy(g => g.Key))
        {
            double dayStartEq = eodCumEquity;
            double dayIntradayPeak = dayStartEq;
            double dayMaxDd = 0d;
            double runningEq = dayStartEq;

            foreach (var tr in dayGroup)
            {
                dayIntradayPeak = Math.Max(dayIntradayPeak, runningEq + tr.MaxFavorableExcursionUsd);
                dayMaxDd = Math.Max(dayMaxDd, tr.PeakToTroughDrawdownUsd);
                runningEq += tr.NetProfit;
                dayIntradayPeak = Math.Max(dayIntradayPeak, runningEq);
            }

            double dayGross = dayGroup.Sum(t => t.GrossProfit);
            double dayComm = dayGroup.Sum(t => t.Commission);
            double dayNet = dayGroup.Sum(t => t.NetProfit);
            eodCumEquity += dayNet;
            eodPeak = Math.Max(eodPeak, eodCumEquity);
            double eodDd = eodPeak - eodCumEquity;

            dailyRecords.Add(new DailyPerformanceRecord(
                dayGroup.Key,
                dayGroup.Count(),
                dayGroup.Count(t => t.NetProfit > 0),
                dayGroup.Count(t => t.NetProfit < 0),
                dayGross,
                dayComm,
                dayNet,
                eodCumEquity,
                dayIntradayPeak,
                dayMaxDd,
                eodDd));
        }

        int tradingDays = dailyRecords.Count;
        int winningDays = dailyRecords.Count(d => d.NetProfit > 0);
        int losingDays = dailyRecords.Count(d => d.NetProfit < 0);
        double dailyWinRate = tradingDays == 0 ? 0d : (double)winningDays / tradingDays;
        double bestDayNet = tradingDays == 0 ? 0d : dailyRecords.Max(d => d.NetProfit);
        double worstDayNet = tradingDays == 0 ? 0d : dailyRecords.Min(d => d.NetProfit);

        double? sharpeRatio = null;
        double? sortinoRatio = null;
        if (dailyRecords.Count >= 2)
        {
            double meanDaily = dailyRecords.Average(d => d.NetProfit);
            double sumSqDiff = dailyRecords.Sum(d => (d.NetProfit - meanDaily) * (d.NetProfit - meanDaily));
            double stdDevDaily = Math.Sqrt(sumSqDiff / (dailyRecords.Count - 1));
            if (stdDevDaily > 1e-9)
                sharpeRatio = (meanDaily / stdDevDaily) * Math.Sqrt(252.0);

            double sumDownsideSq = dailyRecords.Sum(d => d.NetProfit < 0 ? d.NetProfit * d.NetProfit : 0d);
            double downsideDev = Math.Sqrt(sumDownsideSq / dailyRecords.Count);
            if (downsideDev > 1e-9)
                sortinoRatio = (meanDaily / downsideDev) * Math.Sqrt(252.0);
        }

        double? recoveryFactor = maxClosedDrawdown > 1e-9 ? totalNetProfit / maxClosedDrawdown : null;
        double? intradayRecoveryFactor = maxIntradayTrailingDrawdown > 1e-9 ? totalNetProfit / maxIntradayTrailingDrawdown : null;

        double avgDurationMinutes = closedCount == 0 ? 0d : trades.Average(t => t.DurationSeconds) / 60.0;
        double avgMaeUsd = closedCount == 0 ? 0d : trades.Average(t => t.MaxAdverseExcursionUsd);
        double avgMfeUsd = closedCount == 0 ? 0d : trades.Average(t => t.MaxFavorableExcursionUsd);

        var longList = trades.Where(t => string.Equals(t.Side, "Long", StringComparison.OrdinalIgnoreCase)).ToList();
        var shortList = trades.Where(t => string.Equals(t.Side, "Short", StringComparison.OrdinalIgnoreCase)).ToList();
        int longCount = longList.Count;
        double longWinRate = longCount == 0 ? 0d : (double)longList.Count(t => t.NetProfit > 0) / longCount;
        double longNet = longList.Sum(t => t.NetProfit);
        int shortCount = shortList.Count;
        double shortWinRate = shortCount == 0 ? 0d : (double)shortList.Count(t => t.NetProfit > 0) / shortCount;
        double shortNet = shortList.Sum(t => t.NetProfit);

        var summary = new BacktestSummary(
            signals.Count,
            closedCount,
            winningCount,
            losingCount,
            breakevenCount,
            winRate,
            totalGrossProfit,
            closedCommissions,
            totalNetProfit,
            profitFactor,
            maxClosedDrawdown,
            maxIntradayTrailingDrawdown,
            paidCommissions,
            grossWinsUsd,
            grossLossesUsd,
            expectancyUsd,
            avgWinUsd,
            avgLossUsd,
            payoffRatio,
            largestWinUsd,
            largestLossUsd,
            avgRMultiple,
            sharpeRatio,
            sortinoRatio,
            recoveryFactor,
            intradayRecoveryFactor,
            maxWinStreak,
            maxLossStreak,
            avgDurationMinutes,
            avgMaeUsd,
            avgMfeUsd,
            tradingDays,
            winningDays,
            losingDays,
            dailyWinRate,
            bestDayNet,
            worstDayNet,
            longCount,
            longWinRate,
            longNet,
            shortCount,
            shortWinRate,
            shortNet);

        var config = new BacktestConfiguration(
            strategyName,
            request.Instrument.Symbol,
            request.Instrument.TickSize,
            request.Instrument.PointValue,
            request.FastPeriod,
            request.SlowPeriod,
            request.Quantity,
            request.StopLossTicks,
            request.TakeProfitTicks,
            request.CommissionPerContractPerSide,
            request.SlippageTicks,
            executionModel,
            "UTC",
            request.BreakEvenTriggerTicks,
            request.BreakEvenOffsetTicks,
            request.TrailingStopTicks);

        return new BacktestResult(
            config,
            signals.AsReadOnly(),
            trades.AsReadOnly(),
            summary,
            openPosition,
            pending,
            dailyRecords.AsReadOnly());
    }
}
