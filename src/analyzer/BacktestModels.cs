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
    public int? BreakEvenTriggerTicks { get; init; }
    public int BreakEvenOffsetTicks { get; init; }
    public int? TrailingStopTicks { get; init; }
    public double CommissionPerContractPerSide { get; init; }
    public int SlippageTicks { get; init; }
    public TimeOnly? SessionStartUtc { get; init; }
    public TimeOnly? SessionEndUtc { get; init; }
    public double? MaxDailyLossUsd { get; init; }
    public int? MaxTradesPerDay { get; init; }
    public bool LiquidateOnDailyLossBreach { get; init; } = true;
    public IProgress<double>? Progress { get; init; }
}

public sealed record SignalTrace(
    DateTime TimeUtc, double Open, double High, double Low, double Close, long Volume,
    double? Fast, double? Slow, StrategySignal Signal, int? TargetPositionQuantity,
    int PositionQuantity, bool HasPendingOrder);

public sealed record TradeRecord(
    int TradeNumber,
    string Side,
    int Quantity,
    DateTime EntryTimeUtc,
    double EntryPrice,
    DateTime ExitTimeUtc,
    double ExitPrice,
    double GrossProfit,
    double Commission,
    double NetProfit,
    double MaxAdverseExcursionUsd,
    double MaxFavorableExcursionUsd,
    string ExitReason,
    double Points = 0,
    double Ticks = 0,
    double DurationSeconds = 0,
    double? InitialRiskUsd = null,
    double? RMultiple = null,
    double? MfeCaptureRatio = null,
    double PeakToTroughDrawdownUsd = 0,
    double CumulativeNetProfitUsd = 0);

public sealed record DailyPerformanceRecord(
    DateTime DateUtc,
    int Trades,
    int WinningTrades,
    int LosingTrades,
    double GrossProfit,
    double Commission,
    double NetProfit,
    double CumulativeNetProfit,
    double IntradayPeakEquity,
    double MaxIntradayDrawdown,
    double EndOfDayDrawdown);

public sealed record OpenPositionRecord(
    int Quantity, DateTime EntryTimeUtc, double EntryPrice, double EntryCommission,
    double LastClose, double UnrealizedGrossProfit);

public sealed record PendingDecisionRecord(
    DateTime SignalTimeUtc,
    int TargetPositionQuantity,
    string Reason,
    int? StopLossTicks = null,
    int? TakeProfitTicks = null,
    int? BreakEvenTriggerTicks = null,
    int BreakEvenOffsetTicks = 0,
    int? TrailingStopTicks = null);

public sealed record BacktestSummary(
    int Bars,
    int ClosedTrades,
    int WinningTrades,
    int LosingTrades,
    int BreakevenTrades,
    double WinRate,
    double GrossProfit,
    double ClosedTradeCommissions,
    double NetProfit,
    double? ProfitFactor,
    double MaxClosedTradeDrawdown,
    double MaxIntradayTrailingDrawdown,
    double TotalPaidCommissions,
    double GrossWinsUsd = 0,
    double GrossLossesUsd = 0,
    double ExpectancyUsd = 0,
    double AverageWinUsd = 0,
    double AverageLossUsd = 0,
    double? PayoffRatio = null,
    double LargestWinUsd = 0,
    double LargestLossUsd = 0,
    double? AverageRMultiple = null,
    double? SharpeRatio = null,
    double? SortinoRatio = null,
    double? RecoveryFactor = null,
    double? IntradayRecoveryFactor = null,
    int MaxConsecutiveWins = 0,
    int MaxConsecutiveLosses = 0,
    double AverageTradeDurationMinutes = 0,
    double AverageMaeUsd = 0,
    double AverageMfeUsd = 0,
    int TradingDays = 0,
    int WinningDays = 0,
    int LosingDays = 0,
    double DailyWinRate = 0,
    double BestDayNetProfitUsd = 0,
    double WorstDayNetProfitUsd = 0,
    int LongTrades = 0,
    double LongWinRate = 0,
    double LongNetProfitUsd = 0,
    int ShortTrades = 0,
    double ShortWinRate = 0,
    double ShortNetProfitUsd = 0);

public sealed record BacktestConfiguration(
    string Strategy,
    string Instrument,
    double TickSize,
    double PointValue,
    int FastPeriod,
    int SlowPeriod,
    int Quantity,
    int? StopLossTicks,
    int? TakeProfitTicks,
    double CommissionPerContractPerSide,
    int SlippageTicks,
    string ExecutionModel,
    string TimeZone,
    int? BreakEvenTriggerTicks = null,
    int BreakEvenOffsetTicks = 0,
    int? TrailingStopTicks = null);

public sealed record BacktestResult(
    BacktestConfiguration Configuration,
    IReadOnlyList<SignalTrace> Signals,
    IReadOnlyList<TradeRecord> Trades,
    BacktestSummary Summary,
    OpenPositionRecord? OpenPosition,
    PendingDecisionRecord? PendingDecision,
    IReadOnlyList<DailyPerformanceRecord>? DailyPerformance = null);
