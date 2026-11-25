using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Futures.Analyzer;
using Futures.App.Commands;
using Futures.Core;

namespace Futures.App;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static int Main(string[] args)
    {
        bool openUi = args.Length == 0 || (args[0] == "ui"
            && !args.Contains("--help", StringComparer.Ordinal)
            && !args.Contains("--json", StringComparer.Ordinal));
        if (openUi)
        {
            var uiArgs = new Arguments(args);
            uiArgs.Allow("workspace");
            return DesktopLauncher.Run(Workspace.Find(uiArgs.Optional("workspace")));
        }

        bool json = args.Contains("--json", StringComparer.Ordinal);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            var input = new Arguments(args);
            string command = string.Join(" ", input.Words);
            if (command == "help" || input.Has("help"))
            {
                Write(new { help = Help }, json, Help);
                return 0;
            }
            if (command == "prop-firms list")
            {
                input.Allow("workspace");
                string root = Workspace.FindPropFirmsDirectory(Workspace.Find(input.Optional("workspace")));
                var firms = PropFirmEvaluator.LoadAll(root);
                var lines = firms.SelectMany(f => f.Plans.Select(p =>
                    $"{p.Id,-18} | {f.Name,-20} | Meta: ${p.ProfitTargetUsd,6:F0} | MaxDD: ${p.MaxDrawdownUsd,5:F0} ({p.DrawdownType}) | DLL: {(p.DailyLossLimitUsd.HasValue ? "$" + p.DailyLossLimitUsd.Value.ToString("F0", CultureInfo.InvariantCulture) : "Sin DLL"),-8}"));
                Write(new { firms }, json, string.Join(Environment.NewLine, lines));
                return 0;
            }
            if (command == "prop-firms evaluate")
            {
                input.Allow("input", "ticks", "adjusted", "from", "to", "plan", "strategy", "seed", "instrument", "fast", "slow", "quantity", "stop-ticks", "target-ticks", "breakeven-ticks", "breakeven-offset", "trailing-ticks", "commission", "slippage", "session-start", "session-end", "max-daily-loss", "max-daily-trades", "workspace");
                string workspace = Workspace.Find(input.Optional("workspace"));
                string sourcePath = ResolveSourcePath(input, workspace);
                string? planId = input.Optional("plan");
                string root = Workspace.FindPropFirmsDirectory(workspace);
                var firms = PropFirmEvaluator.LoadAll(root);
                var backtest = ExecuteBacktest(input, sourcePath, cancellation.Token);

                var evaluator = new PropFirmEvaluator();
                var evaluations = firms
                    .SelectMany(f => f.Plans.Select(p => (Firm: f, Plan: p)))
                    .Where(x => planId is null || string.Equals(x.Plan.Id, planId, StringComparison.OrdinalIgnoreCase))
                    .Select(x => evaluator.Evaluate(backtest, x.Firm, x.Plan))
                    .ToArray();

                if (evaluations.Length == 0)
                    throw new ArgumentException($"No se encontró el plan '{planId}'. Ejecuta 'prop-firms list' para ver los IDs.");

                string text = FormatSummaryReport(backtest.Summary, backtest.Configuration) + Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine, evaluations.Select(e =>
                        $"[{(e.Passed ? "APROBADA" : e.AccountBlown ? "QUEMADA" : "PENDIENTE")}] {e.PlanId,-16} ({e.PlanName}): " +
                        $"PnL={e.FinalNetProfitUsd,9:F2} USD | MaxDD={e.MaxObservedDrawdownUsd,8:F2} USD | Colchón Mín={e.MinRemainingBufferUsd,7:F2} USD | Días={e.TradingDays,3} -> {e.SummaryReason}"));
                Write(new { backtest.Summary, evaluations }, json, text);
                return 0;
            }
            if (command == "strategies list")
            {
                input.Allow();
                Write(new
                {
                    strategies = new object[]
                    {
                        new
                        {
                            id = "RandomEntry",
                            description = "Entradas aleatorias con ratio 1:1 (~50% win rate baseline)",
                            quantity = 1,
                            stopLossTicks = 40,
                            takeProfitTicks = 40,
                            calculate = "OnBarClose",
                            instruments = new[] { "ES", "NQ", "MES", "MNQ" }
                        },
                        new
                        {
                            id = "DubinsSavage",
                            description = "Teoría de Probabilidad Óptima de Dubins-Savage (Bold Play: 1 trade/día en apertura NY con bloques asimétricos 98/102 ticks anti-comisión)",
                            quantity = 2,
                            stopLossTicks = 98,
                            takeProfitTicks = 102,
                            calculate = "OnBarClose",
                            instruments = new[] { "ES", "NQ", "MES", "MNQ" }
                        },
                        new
                        {
                            id = "ReversionPlaybook",
                            description = "El Arte de la Reversión (Curso Hub): Estocástico (20,5,5) en soporte/resistencia + Promediación en 2 balas + Señal de Escape mecánica",
                            quantity = 1,
                            stopLossTicks = 120,
                            takeProfitTicks = 80,
                            calculate = "OnBarClose",
                            instruments = new[] { "NQ", "ES", "GC", "MNQ", "MES" }
                        },
                        new
                        {
                            id = "SmaCrossover",
                            fastPeriod = 10,
                            slowPeriod = 20,
                            quantity = 1,
                            calculate = "OnBarClose",
                            instruments = new[] { "ES", "NQ", "MES", "MNQ" },
                            platforms = new[] { "NinjaTrader", "TradingView" }
                        }
                    }
                }, json,
                "Estrategias disponibles:\n• RandomEntry (por defecto): Entradas aleatorias con ratio 1:1 (~50% win rate baseline)\n• DubinsSavage: Teoría de Probabilidad Óptima (Bold Play · 1 trade/día en apertura NY · bloques 100 ticks)\n• ReversionPlaybook: El Arte de la Reversión (Estocástico 20,5,5 + 2 Balas DCA + Señal de Escape)\n• SmaCrossover: Cruce de SMA rápida 10 / lenta 20");
                return 0;
            }
            if (command == "backtest run")
            {
                input.Allow("input", "ticks", "adjusted", "from", "to", "strategy", "seed", "instrument", "fast", "slow", "quantity", "stop-ticks", "target-ticks", "breakeven-ticks", "breakeven-offset", "trailing-ticks", "commission", "slippage", "session-start", "session-end", "max-daily-loss", "max-daily-trades", "output", "workspace");
                string workspace = Workspace.Find(input.Optional("workspace"));
                string sourcePath = ResolveSourcePath(input, workspace);
                var result = ExecuteBacktest(input, sourcePath, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                string output = input.Optional("output") is string outputPath
                    ? Path.GetFullPath(outputPath)
                    : Path.Combine(
                        workspace,
                        "runs",
                        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string written = new RunArtifactWriter().Write(result, output, sourcePath);
                Write(
                    new { status = "completed", output = written, result.Configuration, result.Summary, result.OpenPosition, result.PendingDecision },
                    json,
                    FormatSummaryReport(result.Summary, result.Configuration) + $"\nArchivos generados en: {written}");
                return 0;
            }
            if (command == "traces compare")
            {
                input.Allow("expected", "actual", "context", "tolerance");
                var comparison = new SignalTraceComparer().CompareCsv(
                    input.Required("expected"),
                    input.Required("actual"),
                    input.Has("context"),
                    input.Number("tolerance", 1e-9));
                Write(comparison, json, JsonSerializer.Serialize(comparison, JsonOptions));
                return comparison.Matches ? 0 : 1;
            }
            throw new ArgumentException("Comando no disponible: " + command + ". Ejecuta help para ver los comandos disponibles.");
        }
        catch (OperationCanceledException)
        {
            Write(new { status = "cancelled", error = "Prueba cancelada." }, json, "Prueba cancelada.");
            return 130;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = error.Message }, JsonOptions));
            else Console.Error.WriteLine("Error: " + error.Message);
            return 2;
        }
    }

    private static string FormatSummaryReport(BacktestSummary s, BacktestConfiguration c)
    {
        string pf = s.ProfitFactor.HasValue ? s.ProfitFactor.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A";
        string payoff = s.PayoffRatio.HasValue ? s.PayoffRatio.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A";
        string sharpe = s.SharpeRatio.HasValue ? s.SharpeRatio.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A";
        string sortino = s.SortinoRatio.HasValue ? s.SortinoRatio.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A";
        string rec = s.RecoveryFactor.HasValue ? s.RecoveryFactor.Value.ToString("F2", CultureInfo.InvariantCulture) : "N/A";
        string rMult = s.AverageRMultiple.HasValue ? s.AverageRMultiple.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "R" : "N/A";

        return
            $"=== ANALIZADOR PROFESIONAL: {c.Strategy} ({c.Instrument} · x{c.Quantity}) ===\n" +
            $"Velas evaluadas: {s.Bars:N0} | Operaciones cerradas: {s.ClosedTrades:N0} (Ganadoras: {s.WinningTrades}, Perdedoras: {s.LosingTrades}, BE: {s.BreakevenTrades})\n" +
            $"Win Rate: {s.WinRate * 100:F2}% | Profit Factor: {pf} | Ratio Ganancia/Pérdida (Payoff): {payoff} | Esperanza/Trade: {s.ExpectancyUsd:+0.00;-0.00;0.00} USD ({rMult})\n" +
            $"P&L Bruto: {s.GrossProfit:+0.00;-0.00;0.00} USD | Comisiones: {s.ClosedTradeCommissions:F2} USD | P&L Neto: {s.NetProfit:+0.00;-0.00;0.00} USD\n" +
            $"Ganancia Media: {s.AverageWinUsd:+0.00;-0.00;0.00} USD (Máx: {s.LargestWinUsd:+0.00;-0.00;0.00}) | Pérdida Media: {s.AverageLossUsd:+0.00;-0.00;0.00} USD (Máx: {s.LargestLossUsd:+0.00;-0.00;0.00})\n" +
            $"DD Cerrado Máx: {s.MaxClosedTradeDrawdown:F2} USD | DD Intradía Trailing Máx: {s.MaxIntradayTrailingDrawdown:F2} USD | MAE Medio: {s.AverageMaeUsd:F2} USD | MFE Medio: {s.AverageMfeUsd:F2} USD\n" +
            $"Sharpe (Anual): {sharpe} | Sortino (Anual): {sortino} | Recovery Factor: {rec} | Rachas: +{s.MaxConsecutiveWins} / -{s.MaxConsecutiveLosses} | Duración Media: {s.AverageTradeDurationMinutes:F1} min\n" +
            $"Días Operados: {s.TradingDays} (Positivos: {s.WinningDays} [{s.DailyWinRate * 100:F1}%], Negativos: {s.LosingDays}) | Mejor Día: {s.BestDayNetProfitUsd:+0.00;-0.00;0.00} USD | Peor Día: {s.WorstDayNetProfitUsd:+0.00;-0.00;0.00} USD\n" +
            $"Largos: {s.LongTrades} ({s.LongWinRate * 100:F1}% WR, {s.LongNetProfitUsd:+0.00;-0.00;0.00} USD) | Cortos: {s.ShortTrades} ({s.ShortWinRate * 100:F1}% WR, {s.ShortNetProfitUsd:+0.00;-0.00;0.00} USD)";
    }

    private static string ResolveSourcePath(Arguments input, string workspace)
    {
        if (input.Optional("input") is string specified)
            return Path.GetFullPath(specified);
        string instrument = (input.Optional("instrument") ?? "NQ").Trim().ToUpperInvariant();
        if (input.Has("ticks"))
        {
            if (Workspace.FindDatabentoTbboFile(workspace, instrument) is string tbboPath)
                return tbboPath;
            throw new ArgumentException($"Falta --input y no se encontró la serie local de ticks TBBO para {instrument}. Consulta data/databento/README.md.");
        }
        bool adjusted = input.Has("adjusted");
        if (Workspace.FindDatabentoMinuteFile(workspace, instrument, adjusted: adjusted) is string defaultPath)
            return defaultPath;
        throw new ArgumentException($"Falta --input y no se encontró la serie local ohlcv-1m para {instrument} en data/databento/{instrument}/ohlcv-1m/.");
    }

    private static DateTime? ParseDateBoundaryUtc(string? text, bool inclusiveEndOfDay)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime day))
        {
            return inclusiveEndOfDay ? day.AddDays(1) : day;
        }
        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime dt))
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }
        throw new ArgumentException($"Fecha UTC no válida: '{text}'. Usa formato AAAA-MM-DD.");
    }

    private static BacktestResult ExecuteBacktest(Arguments input, string sourcePath, CancellationToken cancellationToken)
    {
        string defaultInstrument = Workspace.InferInstrumentFromPath(sourcePath);
        string instrumentSymbol = (input.Optional("instrument") ?? defaultInstrument).ToUpperInvariant();
        InstrumentSpec spec = InstrumentSpec.FromSymbol(instrumentSymbol);

        DateTime? fromUtc = ParseDateBoundaryUtc(input.Optional("from"), inclusiveEndOfDay: false);
        DateTime? toUtc = ParseDateBoundaryUtc(input.Optional("to"), inclusiveEndOfDay: true);
        if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value > toUtc.Value)
            throw new ArgumentException("--from no puede ser posterior a --to.");

        bool useTicks = input.Has("ticks");
        var bars = useTicks ? Array.Empty<Bar>() : new NinjaTraderMinuteImporter().Read(sourcePath, fromUtc, toUtc);
        var ticks = useTicks ? new NinjaTraderTickImporter().Read(sourcePath, fromUtc, toUtc) : Array.Empty<Tick>();

        int fastPeriod = input.Integer("fast", 10);
        int slowPeriod = input.Integer("slow", 20);
        int quantity = input.Integer("quantity", 1);
        int? stopLossTicks = input.Optional("stop-ticks") is { } sl ? int.Parse(sl, CultureInfo.InvariantCulture) : null;
        int? takeProfitTicks = input.Optional("target-ticks") is { } tp ? int.Parse(tp, CultureInfo.InvariantCulture) : null;
        int? breakEvenTriggerTicks = input.Optional("breakeven-ticks") is { } be ? int.Parse(be, CultureInfo.InvariantCulture) : null;
        int breakEvenOffsetTicks = input.Integer("breakeven-offset", 0);
        int? trailingStopTicks = input.Optional("trailing-ticks") is { } tr ? int.Parse(tr, CultureInfo.InvariantCulture) : null;
        TimeOnly? sessionStart = input.Optional("session-start") is { } ss ? TimeOnly.ParseExact(ss, "HH:mm", CultureInfo.InvariantCulture) : null;
        TimeOnly? sessionEnd = input.Optional("session-end") is { } se ? TimeOnly.ParseExact(se, "HH:mm", CultureInfo.InvariantCulture) : null;

        string strategyName = input.Optional("strategy") ?? "RandomEntry";
        int? seed = input.Optional("seed") is { } sd ? int.Parse(sd, CultureInfo.InvariantCulture) : (int?)null;
        IStrategy strategy = strategyName.Trim().ToUpperInvariant() switch
        {
            "RANDOM" or "RANDOMENTRY" or "RANDOM-ENTRY" => new RandomEntryStrategy(new StrategyParameters(
                fastPeriod, slowPeriod, quantity, stopLossTicks ?? 40, takeProfitTicks ?? 40, breakEvenTriggerTicks, breakEvenOffsetTicks, trailingStopTicks), seed),
            "DUBINSSAVAGE" or "DUBINS-SAVAGE" or "BOLDPLAY" => new DubinsSavageStrategy(new StrategyParameters(
                fastPeriod, slowPeriod, quantity, stopLossTicks ?? 98, takeProfitTicks ?? 102, breakEvenTriggerTicks, breakEvenOffsetTicks, trailingStopTicks), seed),
            "REVERSION" or "REVERSIONPLAYBOOK" or "REVERSION-PLAYBOOK" or "STOCHASTIC" or "ELARTEDELAREVERSION" => new ReversionPlaybookStrategy(new StrategyParameters(
                fastPeriod > 1 ? fastPeriod : 20, slowPeriod > 1 ? slowPeriod : 5, quantity, stopLossTicks, takeProfitTicks, breakEvenTriggerTicks, breakEvenOffsetTicks, trailingStopTicks)),
            "SMACROSSOVER" or "SMA" => new SmaCrossoverStrategy(new StrategyParameters(
                fastPeriod, slowPeriod, quantity, stopLossTicks, takeProfitTicks, breakEvenTriggerTicks, breakEvenOffsetTicks, trailingStopTicks)),
            _ => throw new ArgumentException($"Estrategia no reconocida: '{strategyName}'. Usa RandomEntry, DubinsSavage, ReversionPlaybook o SmaCrossover.")
        };

        return new BacktestRunner().Run(new BacktestRequest
        {
            Bars = bars,
            Ticks = ticks,
            Strategy = strategy,
            Instrument = spec,
            FastPeriod = fastPeriod,
            SlowPeriod = slowPeriod,
            Quantity = quantity,
            StopLossTicks = stopLossTicks,
            TakeProfitTicks = takeProfitTicks,
            BreakEvenTriggerTicks = breakEvenTriggerTicks,
            BreakEvenOffsetTicks = breakEvenOffsetTicks,
            TrailingStopTicks = trailingStopTicks,
            CommissionPerContractPerSide = input.Number("commission", 0),
            SlippageTicks = input.Integer("slippage", 0),
            SessionStartUtc = sessionStart,
            SessionEndUtc = sessionEnd,
            MaxDailyLossUsd = input.Optional("max-daily-loss") is { } dl ? double.Parse(dl, CultureInfo.InvariantCulture) : null,
            MaxTradesPerDay = input.Optional("max-daily-trades") is { } mt ? int.Parse(mt, CultureInfo.InvariantCulture) : null
        }, cancellationToken);
    }

    private static void Write(object value, bool json, string human) =>
        Console.WriteLine(json ? JsonSerializer.Serialize(value, JsonOptions) : human);

    private const string Help = """
        Ascent Backtester · Motor C# de Estrategias y Empresas de Fondeo (ES/NQ/MES/MNQ)

        Futures help [--json]
        Futures prop-firms list [--workspace <carpeta>] [--json]
        Futures prop-firms evaluate [--input <datos.txt>] [--ticks] [--adjusted] [--plan <id-plan>]
            [--strategy RandomEntry|DubinsSavage|ReversionPlaybook|SmaCrossover]
            [--from 2024-01-01] [--to 2026-10-30]
            [--instrument ES|NQ|MES|MNQ] [--fast 10] [--slow 20] [--quantity 1]
            [--stop-ticks 40] [--target-ticks 40]
            [--breakeven-ticks 16] [--breakeven-offset 2] [--trailing-ticks 20]
            [--session-start 13:30] [--session-end 20:59]
            [--max-daily-loss 1000] [--max-daily-trades 5]
            [--commission 2.5] [--slippage 1] [--workspace <carpeta>] [--json]
        Futures strategies list [--json]
        Futures backtest run [--input <datos.txt>] [--ticks] [--adjusted]
            [--strategy RandomEntry|DubinsSavage|ReversionPlaybook|SmaCrossover] [--instrument ES|NQ|MES|MNQ]
            [--from 2024-01-01] [--to 2026-10-30]
            [--fast 10] [--slow 20] [--quantity 1]
            [--stop-ticks 20] [--target-ticks 40]
            [--breakeven-ticks 16] [--breakeven-offset 2] [--trailing-ticks 20]
            [--session-start 13:30] [--session-end 20:59]
            [--max-daily-loss 1000] [--max-daily-trades 5]
            [--commission 2.5] [--slippage 1]
            [--output <carpeta-nueva>] [--workspace <carpeta>] [--json]
        Futures traces compare --expected <signals.csv> --actual <signals.csv>
            [--context] [--tolerance 0.000000001] [--json]
        """;
}
