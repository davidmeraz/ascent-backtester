using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Futures.Core;

namespace Futures.Analyzer;

/// <summary>
/// Executes strategies over minute bars or tick-by-tick streams with Stop-First intrabar resolution,
/// MAE/MFE tracking, session windows, and daily risk limits.
/// </summary>
public sealed class BacktestRunner
{
    public BacktestResult Run(BacktestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Instrument);
        if (!double.IsFinite(request.CommissionPerContractPerSide) || request.CommissionPerContractPerSide < 0)
            throw new ArgumentOutOfRangeException(nameof(request.CommissionPerContractPerSide));
        if (request.SlippageTicks < 0) throw new ArgumentOutOfRangeException(nameof(request.SlippageTicks));

        if (request.Ticks is { Count: > 0 })
            return RunTickByTick(request, cancellationToken);

        ArgumentNullException.ThrowIfNull(request.Bars);
        if (request.Bars.Count == 0) throw new ArgumentException("At least one bar or tick is required.", nameof(request));

        var strategy = request.Strategy ?? new SmaCrossoverStrategy(new StrategyParameters(
            request.FastPeriod, request.SlowPeriod, request.Quantity, request.StopLossTicks, request.TakeProfitTicks));
        strategy.Reset();

        var signals = new List<SignalTrace>(request.Bars.Count);
        var trades = new List<TradeRecord>();
        var position = 0;
        var entryTime = default(DateTime);
        var entryPrice = 0d;
        double? stopPrice = null;
        double? targetPrice = null;
        var currentMaeUsd = 0d;
        var currentMfeUsd = 0d;
        var paidCommissions = 0d;
        PendingDecisionRecord? pending = null;
        DateTime? previousClose = null;
        DateTime currentDay = default;
        var dayRealizedPnl = 0d;
        var dayTrades = 0;

        foreach (var bar in request.Bars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bar is null) throw new ArgumentException("Bars may not contain null entries.", nameof(request));
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
            bool dailyLimitLocked = (request.MaxDailyLossUsd is double maxLoss && dayRealizedPnl <= -maxLoss)
                || (request.MaxTradesPerDay is int maxTrades && dayTrades >= maxTrades);

            // 1. Execute pending market orders at bar open
            if (pending is not null)
            {
                if (pending.TargetPositionQuantity != 0 && (!inSession || dailyLimitLocked))
                {
                    pending = null;
                }
                else
                {
                    var delta = pending.TargetPositionQuantity - position;
                    var fill = bar.Open + Math.Sign(delta) * request.SlippageTicks * request.Instrument.TickSize;
                    var commission = Math.Abs(delta) * request.CommissionPerContractPerSide;
                    paidCommissions += commission;

                    if (position == 0)
                    {
                        position = pending.TargetPositionQuantity;
                        entryTime = barOpenTime;
                        entryPrice = fill;
                        currentMaeUsd = 0d;
                        currentMfeUsd = 0d;
                        var dir = Math.Sign(position);
                        var slTicks = pending.StopLossTicks ?? request.StopLossTicks;
                        var tpTicks = pending.TakeProfitTicks ?? request.TakeProfitTicks;
                        stopPrice = slTicks.HasValue ? entryPrice - dir * slTicks.Value * request.Instrument.TickSize : null;
                        targetPrice = tpTicks.HasValue ? entryPrice + dir * tpTicks.Value * request.Instrument.TickSize : null;
                    }
                    else
                    {
                        CloseActivePosition(barOpenTime, fill, pending.Reason, commission);
                    }
                    strategy.OnExecution(new ExecutionReport(barOpenTime, position, fill));
                    pending = null;
                }
            }

            // 2. Evaluate active position intrabar (Stop-First rule + MAE/MFE tracking)
            if (position != 0)
            {
                var dir = Math.Sign(position);
                var worstPrice = dir > 0 ? bar.Low : bar.High;
                var bestPrice = dir > 0 ? bar.High : bar.Low;

                bool stopHit = stopPrice.HasValue && (dir > 0 ? bar.Low <= stopPrice.Value : bar.High >= stopPrice.Value);
                bool targetHit = targetPrice.HasValue && (dir > 0 ? bar.High >= targetPrice.Value : bar.Low <= targetPrice.Value);

                // Realistic pessimistic order: Stop Loss is evaluated before Take Profit on the same bar
                if (stopHit)
                {
                    var exitFill = stopPrice!.Value - dir * request.SlippageTicks * request.Instrument.TickSize;
                    UpdateExcursion(exitFill, exitFill);
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    CloseActivePosition(bar.CloseTimeUtc, exitFill, "Stop loss", exitComm);
                    strategy.OnExecution(new ExecutionReport(bar.CloseTimeUtc, 0, exitFill));
                }
                else if (targetHit)
                {
                    UpdateExcursion(worstPrice, targetPrice!.Value);
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    CloseActivePosition(bar.CloseTimeUtc, targetPrice.Value, "Take profit", exitComm);
                    strategy.OnExecution(new ExecutionReport(bar.CloseTimeUtc, 0, targetPrice.Value));
                }
                else
                {
                    UpdateExcursion(worstPrice, bestPrice);
                    if (!IsInSession(TimeOnly.FromDateTime(bar.CloseTimeUtc), request.SessionStartUtc, request.SessionEndUtc))
                    {
                        var exitFill = bar.Close - dir * request.SlippageTicks * request.Instrument.TickSize;
                        var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                        paidCommissions += exitComm;
                        CloseActivePosition(bar.CloseTimeUtc, exitFill, "Session close", exitComm);
                        strategy.OnExecution(new ExecutionReport(bar.CloseTimeUtc, 0, exitFill));
                    }
                }
            }

            // 3. Evaluate strategy at bar close
            var snapshot = strategy.OnBarClose(bar, new StrategyContext(position, false));
            signals.Add(new SignalTrace(bar.CloseTimeUtc, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume,
                snapshot.Fast, snapshot.Slow, snapshot.Signal, snapshot.OrderIntent?.TargetPositionQuantity, position, false));
            if (snapshot.OrderIntent is { } order)
                pending = new PendingDecisionRecord(bar.CloseTimeUtc, order.TargetPositionQuantity, order.Reason, order.StopLossTicks, order.TakeProfitTicks);
        }

        var lastClose = request.Bars[request.Bars.Count - 1].Close;
        return BuildResult(request, strategy.Name, "minute-close signal; Stop-First intrabar SL/TP; MAE/MFE tracked", signals, trades, position, entryTime, entryPrice, paidCommissions, lastClose, pending);

        void UpdateExcursion(double worstPrice, double bestPrice)
        {
            var worstPnl = (worstPrice - entryPrice) * position * request.Instrument.PointValue;
            var bestPnl = (bestPrice - entryPrice) * position * request.Instrument.PointValue;
            currentMaeUsd = Math.Max(currentMaeUsd, Math.Max(0d, -worstPnl));
            currentMfeUsd = Math.Max(currentMfeUsd, Math.Max(0d, bestPnl));
        }

        void CloseActivePosition(DateTime exitTime, double exitFill, string reason, double exitCommission)
        {
            var gross = (exitFill - entryPrice) * position * request.Instrument.PointValue;
            var roundTripCommission = Math.Abs(position) * request.CommissionPerContractPerSide + exitCommission;
            var net = gross - roundTripCommission;
            trades.Add(new TradeRecord(trades.Count + 1, position > 0 ? "Long" : "Short", Math.Abs(position),
                entryTime, entryPrice, exitTime, exitFill, gross, roundTripCommission, net, currentMaeUsd, currentMfeUsd, reason));
            dayRealizedPnl += net;
            dayTrades++;
            position = 0;
            stopPrice = null;
            targetPrice = null;
        }
    }

    private BacktestResult RunTickByTick(BacktestRequest request, CancellationToken cancellationToken)
    {
        var strategy = request.Strategy ?? new SmaCrossoverStrategy(new StrategyParameters(
            request.FastPeriod, request.SlowPeriod, request.Quantity, request.StopLossTicks, request.TakeProfitTicks));
        strategy.Reset();

        var signals = new List<SignalTrace>();
        var trades = new List<TradeRecord>();
        var position = 0;
        var entryTime = default(DateTime);
        var entryPrice = 0d;
        double? stopPrice = null;
        double? targetPrice = null;
        var currentMaeUsd = 0d;
        var currentMfeUsd = 0d;
        var paidCommissions = 0d;
        PendingDecisionRecord? pending = null;

        DateTime? bucketCloseTime = null;
        double barOpen = 0, barHigh = 0, barLow = 0, barClose = 0;
        long barVolume = 0;

        foreach (var tick in request.Ticks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tickMinuteClose = new DateTime(
                tick.TimeUtc.Year, tick.TimeUtc.Month, tick.TimeUtc.Day,
                tick.TimeUtc.Hour, tick.TimeUtc.Minute, 0, DateTimeKind.Utc).AddMinutes(1);

            if (bucketCloseTime.HasValue && tickMinuteClose > bucketCloseTime.Value)
            {
                var closedBar = new Bar(bucketCloseTime.Value, barOpen, barHigh, barLow, barClose, barVolume);
                var snapshot = strategy.OnBarClose(closedBar, new StrategyContext(position, false));
                signals.Add(new SignalTrace(closedBar.CloseTimeUtc, closedBar.Open, closedBar.High, closedBar.Low, closedBar.Close, closedBar.Volume,
                    snapshot.Fast, snapshot.Slow, snapshot.Signal, snapshot.OrderIntent?.TargetPositionQuantity, position, false));
                if (snapshot.OrderIntent is { } order)
                    pending = new PendingDecisionRecord(closedBar.CloseTimeUtc, order.TargetPositionQuantity, order.Reason, order.StopLossTicks, order.TakeProfitTicks);
                bucketCloseTime = null;
            }

            if (!bucketCloseTime.HasValue)
            {
                bucketCloseTime = tickMinuteClose;
                barOpen = barHigh = barLow = barClose = tick.Price;
                barVolume = tick.Volume;
            }
            else
            {
                barHigh = Math.Max(barHigh, tick.Price);
                barLow = Math.Min(barLow, tick.Price);
                barClose = tick.Price;
                barVolume += tick.Volume;
            }

            // Execute pending order on first available tick using Bid/Ask + slippage
            if (pending is not null)
            {
                var delta = pending.TargetPositionQuantity - position;
                var rawBookPrice = delta > 0 ? tick.Ask : tick.Bid;
                var fill = rawBookPrice + Math.Sign(delta) * request.SlippageTicks * request.Instrument.TickSize;
                var commission = Math.Abs(delta) * request.CommissionPerContractPerSide;
                paidCommissions += commission;

                if (position == 0)
                {
                    position = pending.TargetPositionQuantity;
                    entryTime = tick.TimeUtc;
                    entryPrice = fill;
                    currentMaeUsd = 0d;
                    currentMfeUsd = 0d;
                    var dir = Math.Sign(position);
                    var slTicks = pending.StopLossTicks ?? request.StopLossTicks;
                    var tpTicks = pending.TakeProfitTicks ?? request.TakeProfitTicks;
                    stopPrice = slTicks.HasValue ? entryPrice - dir * slTicks.Value * request.Instrument.TickSize : null;
                    targetPrice = tpTicks.HasValue ? entryPrice + dir * tpTicks.Value * request.Instrument.TickSize : null;
                }
                else
                {
                    CloseTickPosition(tick.TimeUtc, fill, pending.Reason, commission);
                }
                strategy.OnExecution(new ExecutionReport(tick.TimeUtc, position, fill));
                pending = null;
            }

            // Evaluate active position on exact tick price
            if (position != 0)
            {
                var dir = Math.Sign(position);
                var markPrice = dir > 0 ? tick.Bid : tick.Ask;
                var unrealized = (markPrice - entryPrice) * position * request.Instrument.PointValue;
                currentMaeUsd = Math.Max(currentMaeUsd, Math.Max(0d, -unrealized));
                currentMfeUsd = Math.Max(currentMfeUsd, Math.Max(0d, unrealized));

                if (stopPrice.HasValue && (dir > 0 ? tick.Price <= stopPrice.Value : tick.Price >= stopPrice.Value))
                {
                    var exitFill = (dir > 0 ? tick.Bid : tick.Ask) - dir * request.SlippageTicks * request.Instrument.TickSize;
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    CloseTickPosition(tick.TimeUtc, exitFill, "Stop loss (tick)", exitComm);
                    strategy.OnExecution(new ExecutionReport(tick.TimeUtc, 0, exitFill));
                }
                else if (targetPrice.HasValue && (dir > 0 ? tick.Price >= targetPrice.Value : tick.Price <= targetPrice.Value))
                {
                    var exitFill = targetPrice.Value;
                    var exitComm = Math.Abs(position) * request.CommissionPerContractPerSide;
                    paidCommissions += exitComm;
                    CloseTickPosition(tick.TimeUtc, exitFill, "Take profit (tick)", exitComm);
                    strategy.OnExecution(new ExecutionReport(tick.TimeUtc, 0, exitFill));
                }
            }
        }

        if (bucketCloseTime.HasValue)
        {
            var closedBar = new Bar(bucketCloseTime.Value, barOpen, barHigh, barLow, barClose, barVolume);
            var snapshot = strategy.OnBarClose(closedBar, new StrategyContext(position, false));
            signals.Add(new SignalTrace(closedBar.CloseTimeUtc, closedBar.Open, closedBar.High, closedBar.Low, closedBar.Close, closedBar.Volume,
                snapshot.Fast, snapshot.Slow, snapshot.Signal, snapshot.OrderIntent?.TargetPositionQuantity, position, false));
            if (snapshot.OrderIntent is { } order)
                pending = new PendingDecisionRecord(closedBar.CloseTimeUtc, order.TargetPositionQuantity, order.Reason, order.StopLossTicks, order.TakeProfitTicks);
        }

        var lastPrice = request.Ticks[request.Ticks.Count - 1].Price;
        return BuildResult(request, strategy.Name, "tick-by-tick Bid/Ask execution; exact SL/TP and MAE/MFE", signals, trades, position, entryTime, entryPrice, paidCommissions, lastPrice, pending);

        void CloseTickPosition(DateTime exitTime, double exitFill, string reason, double exitCommission)
        {
            var gross = (exitFill - entryPrice) * position * request.Instrument.PointValue;
            var roundTripCommission = Math.Abs(position) * request.CommissionPerContractPerSide + exitCommission;
            trades.Add(new TradeRecord(trades.Count + 1, position > 0 ? "Long" : "Short", Math.Abs(position),
                entryTime, entryPrice, exitTime, exitFill, gross, roundTripCommission, gross - roundTripCommission, currentMaeUsd, currentMfeUsd, reason));
            position = 0;
            stopPrice = null;
            targetPrice = null;
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
        PendingDecisionRecord? pending)
    {
        OpenPositionRecord? openPosition = position == 0 ? null : new OpenPositionRecord(position, entryTime, entryPrice,
            Math.Abs(position) * request.CommissionPerContractPerSide, lastClose,
            (lastClose - entryPrice) * position * request.Instrument.PointValue);
        var positive = trades.Where(t => t.NetProfit > 0).Sum(t => t.NetProfit);
        var negative = -trades.Where(t => t.NetProfit < 0).Sum(t => t.NetProfit);
        var equity = 0d;
        var closedPeak = 0d;
        var closedDrawdown = 0d;
        var intradayPeak = 0d;
        var intradayTrailingDrawdown = 0d;

        foreach (var trade in trades)
        {
            // Intraday trailing peak rises with unrealized MFE before trade closes
            intradayPeak = Math.Max(intradayPeak, equity + trade.MaxFavorableExcursionUsd);
            var worstDuringTrade = equity - trade.MaxAdverseExcursionUsd;
            intradayTrailingDrawdown = Math.Max(intradayTrailingDrawdown, intradayPeak - worstDuringTrade);

            equity += trade.NetProfit;
            closedPeak = Math.Max(closedPeak, equity);
            closedDrawdown = Math.Max(closedDrawdown, closedPeak - equity);
            intradayPeak = Math.Max(intradayPeak, equity);
            intradayTrailingDrawdown = Math.Max(intradayTrailingDrawdown, intradayPeak - equity);
        }

        var winningTrades = trades.Count(t => t.NetProfit > 0);
        var summary = new BacktestSummary(signals.Count, trades.Count, winningTrades,
            trades.Count(t => t.NetProfit < 0), trades.Count(t => t.NetProfit == 0),
            trades.Count == 0 ? 0 : (double)winningTrades / trades.Count, trades.Sum(t => t.GrossProfit),
            trades.Sum(t => t.Commission), trades.Sum(t => t.NetProfit), negative == 0 ? null : positive / negative,
            closedDrawdown, intradayTrailingDrawdown, paidCommissions);
        var config = new BacktestConfiguration(strategyName, request.Instrument.Symbol,
            request.Instrument.TickSize, request.Instrument.PointValue, request.FastPeriod, request.SlowPeriod,
            request.Quantity, request.StopLossTicks, request.TakeProfitTicks,
            request.CommissionPerContractPerSide, request.SlippageTicks, executionModel, "UTC");
        return new BacktestResult(config, signals.AsReadOnly(), trades.AsReadOnly(), summary, openPosition, pending);
    }
}
