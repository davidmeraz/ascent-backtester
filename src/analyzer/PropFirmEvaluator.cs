using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Futures.Analyzer;

public enum DrawdownType
{
    IntradayTrailing,
    EndOfDayTrailing,
    Static
}

public enum DailyLossType
{
    None,
    SoftBreach,
    HardBreach
}

public sealed record PropFirmGeneralRules(
    string SessionCloseTimeEt,
    bool NewsTradingAllowed,
    bool OvernightAllowed,
    double? EvaluationConsistencyPercent,
    double? FundedConsistencyPercent,
    int MinTradingDaysEvaluation,
    IReadOnlyList<string> Notes);

public sealed record PropFirmPlan(
    string Id,
    string Name,
    double AccountSizeUsd,
    double ProfitTargetUsd,
    double MaxDrawdownUsd,
    DrawdownType DrawdownType,
    double? DailyLossLimitUsd,
    DailyLossType DailyLossType,
    double SafetyNetBufferUsd,
    int MaxMiniContracts,
    int MaxMicroContracts,
    int FundedInitialMaxMiniContracts);

public sealed record PropFirmDefinition(
    string Id,
    string Name,
    string UpdatedAt,
    PropFirmGeneralRules GeneralRules,
    IReadOnlyList<PropFirmPlan> Plans);

public sealed record PropFirmEvaluationResult(
    string FirmId,
    string PlanId,
    string PlanName,
    bool Passed,
    bool AccountBlown,
    bool DailyLossViolated,
    bool ConsistencySatisfied,
    int TradingDays,
    double FinalNetProfitUsd,
    double MaxObservedDrawdownUsd,
    double WorstDailyLossUsd,
    double BestDayProfitSharePercent,
    string SummaryReason);

public sealed class PropFirmEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<PropFirmDefinition> LoadAll(string propFirmsRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propFirmsRootDirectory);
        if (!Directory.Exists(propFirmsRootDirectory)) return Array.Empty<PropFirmDefinition>();
        var files = Directory.GetFiles(propFirmsRootDirectory, "plans.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        var list = new List<PropFirmDefinition>(files.Length);
        foreach (var file in files)
        {
            var def = JsonSerializer.Deserialize<PropFirmDefinition>(File.ReadAllText(file), JsonOptions);
            if (def is not null) list.Add(def);
        }
        return list.AsReadOnly();
    }

    public PropFirmEvaluationResult Evaluate(BacktestResult backtest, PropFirmDefinition firm, PropFirmPlan plan)
    {
        ArgumentNullException.ThrowIfNull(backtest);
        ArgumentNullException.ThrowIfNull(firm);
        ArgumentNullException.ThrowIfNull(plan);

        var dailyPnl = new Dictionary<DateTime, double>();
        double equity = 0d;
        double peakEquity = 0d;
        double maxDrawdown = 0d;
        bool accountBlown = false;
        bool dailyLossViolated = false;

        foreach (var trade in backtest.Trades)
        {
            var day = trade.ExitTimeUtc.Date;
            var dayBeforeTrade = dailyPnl.GetValueOrDefault(day);

            if (plan.DrawdownType == DrawdownType.IntradayTrailing)
            {
                // Peak rises with unrealized MFE during the trade (Apex / TPT PRO rule)
                peakEquity = Math.Max(peakEquity, equity + trade.MaxFavorableExcursionUsd);
                var worstEquityDuringTrade = equity - trade.MaxAdverseExcursionUsd;
                var intrabarDd = peakEquity - worstEquityDuringTrade;
                maxDrawdown = Math.Max(maxDrawdown, intrabarDd);
                if (intrabarDd >= plan.MaxDrawdownUsd) accountBlown = true;
            }
            else if (plan.DrawdownType == DrawdownType.Static)
            {
                var worstEquityDuringTrade = equity - trade.MaxAdverseExcursionUsd;
                var staticDd = Math.Max(0d, -worstEquityDuringTrade);
                maxDrawdown = Math.Max(maxDrawdown, staticDd);
                if (staticDd >= plan.MaxDrawdownUsd) accountBlown = true;
            }

            if (plan.DailyLossLimitUsd is double dllIntraday
                && (dayBeforeTrade - trade.MaxAdverseExcursionUsd) <= -dllIntraday)
            {
                dailyLossViolated = true;
                if (plan.DailyLossType == DailyLossType.HardBreach) accountBlown = true;
            }

            dailyPnl[day] = dayBeforeTrade + trade.NetProfit;
            equity += trade.NetProfit;

            if (plan.DrawdownType == DrawdownType.IntradayTrailing)
            {
                peakEquity = Math.Max(peakEquity, equity);
                double closedDd = peakEquity - equity;
                maxDrawdown = Math.Max(maxDrawdown, closedDd);
                if (closedDd >= plan.MaxDrawdownUsd) accountBlown = true;
            }

            if (plan.DailyLossLimitUsd is double dll && dailyPnl[day] <= -dll)
            {
                dailyLossViolated = true;
                if (plan.DailyLossType == DailyLossType.HardBreach) accountBlown = true;
            }
        }

        if (plan.DrawdownType == DrawdownType.EndOfDayTrailing)
        {
            double eodEquity = 0d;
            double eodPeak = 0d;
            foreach (var day in dailyPnl.Keys.OrderBy(d => d))
            {
                eodEquity += dailyPnl[day];
                eodPeak = Math.Max(eodPeak, eodEquity);
                double currentDd = eodPeak - eodEquity;
                maxDrawdown = Math.Max(maxDrawdown, currentDd);
                if (currentDd >= plan.MaxDrawdownUsd) accountBlown = true;
            }
        }

        int tradingDays = dailyPnl.Count;
        double worstDailyLoss = dailyPnl.Count == 0 ? 0d : Math.Min(0d, dailyPnl.Values.Min());
        double bestDayProfit = dailyPnl.Count == 0 ? 0d : Math.Max(0d, dailyPnl.Values.Max());
        double bestDayShare = equity > 0d ? (bestDayProfit / equity) * 100d : 0d;

        bool consistencyOk = firm.GeneralRules.EvaluationConsistencyPercent is not double maxShare
            || (equity > 0d && bestDayShare <= maxShare);
        bool minDaysOk = tradingDays >= firm.GeneralRules.MinTradingDaysEvaluation;
        bool targetReached = equity >= plan.ProfitTargetUsd;
        bool passed = !accountBlown && targetReached && minDaysOk && consistencyOk;

        string reason = accountBlown ? "Cuenta quemada por límite de drawdown o pérdida diaria (Hard Breach)."
            : !targetReached ? $"Objetivo no alcanzado ({equity:F2} / {plan.ProfitTargetUsd:F2} USD)."
            : !minDaysOk ? $"Días operados insuficientes ({tradingDays} / {firm.GeneralRules.MinTradingDaysEvaluation})."
            : !consistencyOk ? $"Regla de consistencia incumplida (mejor día {bestDayShare:F1}% > {firm.GeneralRules.EvaluationConsistencyPercent:F1}%)."
            : "Prueba de fondeo aprobada cumpliendo todas las reglas.";

        return new PropFirmEvaluationResult(
            firm.Id,
            plan.Id,
            plan.Name,
            passed,
            accountBlown,
            dailyLossViolated,
            consistencyOk,
            tradingDays,
            equity,
            maxDrawdown,
            worstDailyLoss,
            bestDayShare,
            reason);
    }
}
