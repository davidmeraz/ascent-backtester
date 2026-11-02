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
    string SummaryReason,
    double PeakEquityUsd = 0,
    double MinRemainingBufferUsd = 0,
    bool ContractLimitViolated = false);

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

        bool isMicroInstrument = backtest.Configuration.Instrument.StartsWith("M", StringComparison.OrdinalIgnoreCase);
        int maxAllowedContracts = isMicroInstrument ? plan.MaxMicroContracts : plan.MaxMiniContracts;
        bool contractLimitViolated = backtest.Trades.Any(t => t.Quantity > maxAllowedContracts);

        var dailyPnl = new Dictionary<DateTime, double>();
        double equity = 0d;
        double peakEquity = 0d;
        double maxDrawdown = 0d;
        double liquidationThreshold = -plan.MaxDrawdownUsd;
        double? maxLockedThreshold = plan.SafetyNetBufferUsd > plan.MaxDrawdownUsd
            ? plan.SafetyNetBufferUsd - plan.MaxDrawdownUsd
            : null;
        double minRemainingBuffer = plan.MaxDrawdownUsd;
        bool accountBlown = contractLimitViolated;
        bool dailyLossViolated = false;

        void UpdateTrailingThreshold(double currentPeak)
        {
            double candidate = currentPeak - plan.MaxDrawdownUsd;
            if (maxLockedThreshold.HasValue)
                candidate = Math.Min(candidate, maxLockedThreshold.Value);
            if (candidate > liquidationThreshold)
                liquidationThreshold = candidate;
        }

        foreach (var trade in backtest.Trades)
        {
            var day = trade.ExitTimeUtc.Date;
            var dayBeforeTrade = dailyPnl.GetValueOrDefault(day);
            double entryComm = trade.Commission * 0.5;
            double worstFloatingEquityDuringTrade = equity - trade.MaxAdverseExcursionUsd - entryComm;
            double bestFloatingEquityDuringTrade = equity + trade.MaxFavorableExcursionUsd - entryComm;

            if (plan.DrawdownType == DrawdownType.IntradayTrailing)
            {
                // 1. Check adverse excursion against liquidation threshold prior to new highs in this trade
                double bufferBeforePeak = worstFloatingEquityDuringTrade - liquidationThreshold;
                minRemainingBuffer = Math.Min(minRemainingBuffer, Math.Max(0d, bufferBeforePeak));
                if (bufferBeforePeak <= 0d)
                    accountBlown = true;

                // 2. Update trailing peak and liquidation threshold with MFE reached during trade
                double ddFromPriorPeak = Math.Max(0d, peakEquity - worstFloatingEquityDuringTrade);
                double ddWithinTrade = trade.PeakToTroughDrawdownUsd > 0d
                    ? trade.PeakToTroughDrawdownUsd
                    : ddFromPriorPeak;
                double tradeEffectiveDd = Math.Max(ddFromPriorPeak, ddWithinTrade);
                maxDrawdown = Math.Max(maxDrawdown, tradeEffectiveDd);

                bool thresholdStillTrailing = !maxLockedThreshold.HasValue || liquidationThreshold < maxLockedThreshold.Value;
                if (thresholdStillTrailing && ddWithinTrade >= plan.MaxDrawdownUsd)
                {
                    accountBlown = true;
                    minRemainingBuffer = 0d;
                }

                peakEquity = Math.Max(peakEquity, bestFloatingEquityDuringTrade);
                UpdateTrailingThreshold(peakEquity);
            }
            else if (plan.DrawdownType == DrawdownType.Static)
            {
                var staticDd = Math.Max(0d, -worstFloatingEquityDuringTrade);
                maxDrawdown = Math.Max(maxDrawdown, staticDd);
                double buffer = plan.MaxDrawdownUsd - staticDd;
                minRemainingBuffer = Math.Min(minRemainingBuffer, Math.Max(0d, buffer));
                if (staticDd >= plan.MaxDrawdownUsd)
                    accountBlown = true;
            }

            if (plan.DailyLossLimitUsd is double dllIntraday
                && (dayBeforeTrade - trade.MaxAdverseExcursionUsd - entryComm) <= -dllIntraday)
            {
                dailyLossViolated = true;
                if (plan.DailyLossType == DailyLossType.HardBreach)
                    accountBlown = true;
            }

            dailyPnl[day] = dayBeforeTrade + trade.NetProfit;
            equity += trade.NetProfit;
            peakEquity = Math.Max(peakEquity, equity);

            if (plan.DrawdownType == DrawdownType.IntradayTrailing)
            {
                UpdateTrailingThreshold(peakEquity);
                double closedDd = peakEquity - equity;
                maxDrawdown = Math.Max(maxDrawdown, closedDd);
                double bufferAtClose = equity - liquidationThreshold;
                minRemainingBuffer = Math.Min(minRemainingBuffer, Math.Max(0d, bufferAtClose));
                if (bufferAtClose <= 0d)
                    accountBlown = true;
            }
            else if (plan.DrawdownType == DrawdownType.Static)
            {
                double closedStaticDd = Math.Max(0d, -equity);
                maxDrawdown = Math.Max(maxDrawdown, closedStaticDd);
                double buffer = plan.MaxDrawdownUsd - closedStaticDd;
                minRemainingBuffer = Math.Min(minRemainingBuffer, Math.Max(0d, buffer));
                if (closedStaticDd >= plan.MaxDrawdownUsd)
                    accountBlown = true;
            }

            if (plan.DailyLossLimitUsd is double dll && dailyPnl[day] <= -dll)
            {
                dailyLossViolated = true;
                if (plan.DailyLossType == DailyLossType.HardBreach)
                    accountBlown = true;
            }
        }

        if (plan.DrawdownType == DrawdownType.EndOfDayTrailing)
        {
            double eodEquity = 0d;
            double eodPeak = 0d;
            double eodThreshold = -plan.MaxDrawdownUsd;
            foreach (var day in dailyPnl.Keys.OrderBy(d => d))
            {
                eodEquity += dailyPnl[day];
                eodPeak = Math.Max(eodPeak, eodEquity);
                double candidate = eodPeak - plan.MaxDrawdownUsd;
                if (maxLockedThreshold.HasValue)
                    candidate = Math.Min(candidate, maxLockedThreshold.Value);
                if (candidate > eodThreshold)
                    eodThreshold = candidate;

                double currentDd = eodPeak - eodEquity;
                maxDrawdown = Math.Max(maxDrawdown, currentDd);
                double buffer = eodEquity - eodThreshold;
                minRemainingBuffer = Math.Min(minRemainingBuffer, Math.Max(0d, buffer));
                if (buffer <= 0d)
                    accountBlown = true;
            }
            peakEquity = Math.Max(peakEquity, eodPeak);
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

        string reason = contractLimitViolated
            ? $"Límite de contratos excedido (máx. {maxAllowedContracts} para {backtest.Configuration.Instrument})."
            : accountBlown ? "Cuenta quemada por límite de drawdown o pérdida diaria (Hard Breach)."
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
            reason,
            peakEquity,
            minRemainingBuffer,
            contractLimitViolated);
    }
}
