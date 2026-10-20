using System;
using System.Collections.Generic;
using Futures.Core;

namespace Futures.Analyzer;

public sealed class BacktestRequest
{
    public IReadOnlyList<Bar> Bars { get; init; } = Array.Empty<Bar>();
    public IReadOnlyList<Tick> Ticks { get; init; } = Array.Empty<Tick>();
    public IStrategy? Strategy { get; init; }
    public InstrumentSpec Instrument { get; init; } = InstrumentSpec.ES;
    public int FastPeriod { get; init; } = 10;
    public int SlowPeriod { get; init; } = 20;
    public int Quantity { get; init; } = 1;
    public int? StopLossTicks { get; init; }
    public int? TakeProfitTicks { get; init; }
    public double CommissionPerContractPerSide { get; init; }
    public int SlippageTicks { get; init; }
    public TimeOnly? SessionStartUtc { get; init; }
    public TimeOnly? SessionEndUtc { get; init; }
    public double? MaxDailyLossUsd { get; init; }
    public int? MaxTradesPerDay { get; init; }
}

public sealed record SignalTrace(
    DateTime TimeUtc, double Open, double High, double Low, double Close, long Volume,
    double? Fast, double? Slow, StrategySignal Signal, int? TargetPositionQuantity,
    int PositionQuantity, bool HasPendingOrder);

public sealed record TradeRecord(
    int TradeNumber, string Side, int Quantity, DateTime EntryTimeUtc, double EntryPrice,
    DateTime ExitTimeUtc, double ExitPrice, double GrossProfit, double Commission,
    double NetProfit, double MaxAdverseExcursionUsd, double MaxFavorableExcursionUsd,
    string ExitReason);

public sealed record OpenPositionRecord(
    int Quantity, DateTime EntryTimeUtc, double EntryPrice, double EntryCommission,
    double LastClose, double UnrealizedGrossProfit);

public sealed record PendingDecisionRecord(
    DateTime SignalTimeUtc,
    int TargetPositionQuantity,
    string Reason,
    int? StopLossTicks = null,
    int? TakeProfitTicks = null);

public sealed record BacktestSummary(
    int Bars, int ClosedTrades, int WinningTrades, int LosingTrades, int BreakevenTrades,
    double WinRate, double GrossProfit, double ClosedTradeCommissions, double NetProfit,
    double? ProfitFactor, double MaxClosedTradeDrawdown, double MaxIntradayTrailingDrawdown,
    double TotalPaidCommissions);

public sealed record BacktestConfiguration(
    string Strategy, string Instrument, double TickSize, double PointValue,
    int FastPeriod, int SlowPeriod, int Quantity, int? StopLossTicks, int? TakeProfitTicks,
    double CommissionPerContractPerSide, int SlippageTicks, string ExecutionModel, string TimeZone);

public sealed record BacktestResult(
    BacktestConfiguration Configuration,
    IReadOnlyList<SignalTrace> Signals,
    IReadOnlyList<TradeRecord> Trades,
    BacktestSummary Summary,
    OpenPositionRecord? OpenPosition,
    PendingDecisionRecord? PendingDecision);
