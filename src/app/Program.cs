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
        bool json = args.Contains("--json", StringComparer.Ordinal);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            var input = new Arguments(args);
            string command = string.Join(" ", input.Words);
            if (command.Length == 0 || command == "help" || input.Has("help"))
            {
                Write(new { help = Help }, json, Help);
                return 0;
            }
            if (command == "prop-firms list")
            {
                input.Allow("workspace");
                string root = Path.Combine(Workspace.Find(input.Optional("workspace")), "prop-firms");
                var firms = PropFirmEvaluator.LoadAll(root);
                var lines = firms.SelectMany(f => f.Plans.Select(p =>
                    $"{p.Id,-18} | {f.Name,-20} | Meta: ${p.ProfitTargetUsd,6:F0} | MaxDD: ${p.MaxDrawdownUsd,5:F0} ({p.DrawdownType}) | DLL: {(p.DailyLossLimitUsd.HasValue ? "$" + p.DailyLossLimitUsd.Value.ToString("F0", CultureInfo.InvariantCulture) : "Sin DLL"),-8}"));
                Write(new { firms }, json, string.Join(Environment.NewLine, lines));
                return 0;
            }
            if (command == "prop-firms evaluate")
            {
                input.Allow("input", "ticks", "plan", "instrument", "fast", "slow", "quantity", "stop-ticks", "target-ticks", "commission", "slippage", "session-start", "session-end", "max-daily-loss", "max-daily-trades", "workspace");
                string sourcePath = Path.GetFullPath(input.Required("input"));
                string? planId = input.Optional("plan");
                string root = Path.Combine(Workspace.Find(input.Optional("workspace")), "prop-firms");
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

                string text = string.Join(Environment.NewLine, evaluations.Select(e =>
                    $"[{(e.Passed ? "APROBADA" : e.AccountBlown ? "QUEMADA" : "PENDIENTE")}] {e.PlanId} ({e.PlanName}): " +
                    $"PnL={e.FinalNetProfitUsd:F2} USD, MaxDD={e.MaxObservedDrawdownUsd:F2} USD, Días={e.TradingDays} -> {e.SummaryReason}"));
                Write(new { backtest.Summary, evaluations }, json, text);
                return 0;
            }
            if (command == "strategies list")
            {
                input.Allow();
                Write(new
                {
                    strategies = new[]
                    {
                        new
                        {
                            id = "SmaCrossover",
                            fastPeriod = 10,
                            slowPeriod = 20,
                            quantity = 1,
                            calculate = "OnBarClose",
                            instruments = new[] { "ES", "NQ" },
                            platforms = new[] { "NinjaTrader", "TradingView" }
                        }
                    }
                }, json, "SmaCrossover · SMA rápida 10 / lenta 20 · 1 contrato · ES/NQ · NinjaTrader & TradingView");
                return 0;
            }
            if (command == "backtest run")
            {
                input.Allow("input", "ticks", "instrument", "fast", "slow", "quantity", "stop-ticks", "target-ticks", "commission", "slippage", "session-start", "session-end", "max-daily-loss", "max-daily-trades", "output", "workspace");
                string sourcePath = Path.GetFullPath(input.Required("input"));
                var result = ExecuteBacktest(input, sourcePath, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                string output = input.Optional("output") is string outputPath
                    ? Path.GetFullPath(outputPath)
                    : Path.Combine(
                        Workspace.Find(input.Optional("workspace")),
                        "runs",
                        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string written = new RunArtifactWriter().Write(result, output, sourcePath);
                Write(
                    new { status = "completed", output = written, result.Configuration, result.Summary, result.OpenPosition, result.PendingDecision },
                    json,
                    $"Prueba completada: {result.Summary.Bars} velas, {result.Summary.ClosedTrades} operaciones cerradas.\n" +
                    $"P&L neto: {result.Summary.NetProfit:F2} USD | DD Cerrado: {result.Summary.MaxClosedTradeDrawdown:F2} USD | DD Intradía Trailing: {result.Summary.MaxIntradayTrailingDrawdown:F2} USD.\n" +
                    $"Resultados: {written}");
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

    private static BacktestResult ExecuteBacktest(Arguments input, string sourcePath, CancellationToken cancellationToken)
    {
        string instrument = (input.Optional("instrument") ?? "ES").ToUpperInvariant();
        InstrumentSpec spec = instrument switch
        {
            "ES" => InstrumentSpec.ES,
            "NQ" => InstrumentSpec.NQ,
            _ => throw new ArgumentException("Instrumento no admitido: usa ES o NQ.")
        };

        bool useTicks = input.Has("ticks");
        var bars = useTicks ? Array.Empty<Bar>() : new NinjaTraderMinuteImporter().Read(sourcePath);
        var ticks = useTicks ? new NinjaTraderTickImporter().Read(sourcePath) : Array.Empty<Tick>();

        return new BacktestRunner().Run(new BacktestRequest
        {
            Bars = bars,
            Ticks = ticks,
            Instrument = spec,
            FastPeriod = input.Integer("fast", 10),
            SlowPeriod = input.Integer("slow", 20),
            Quantity = input.Integer("quantity", 1),
            StopLossTicks = input.Optional("stop-ticks") is { } sl ? int.Parse(sl, CultureInfo.InvariantCulture) : null,
            TakeProfitTicks = input.Optional("target-ticks") is { } tp ? int.Parse(tp, CultureInfo.InvariantCulture) : null,
            CommissionPerContractPerSide = input.Number("commission", 0),
            SlippageTicks = input.Integer("slippage", 0),
            SessionStartUtc = input.Optional("session-start") is { } ss ? TimeOnly.ParseExact(ss, "HH:mm", CultureInfo.InvariantCulture) : null,
            SessionEndUtc = input.Optional("session-end") is { } se ? TimeOnly.ParseExact(se, "HH:mm", CultureInfo.InvariantCulture) : null,
            MaxDailyLossUsd = input.Optional("max-daily-loss") is { } dl ? double.Parse(dl, CultureInfo.InvariantCulture) : null,
            MaxTradesPerDay = input.Optional("max-daily-trades") is { } mt ? int.Parse(mt, CultureInfo.InvariantCulture) : null
        }, cancellationToken);
    }

    private static void Write(object value, bool json, string human) =>
        Console.WriteLine(json ? JsonSerializer.Serialize(value, JsonOptions) : human);

    private const string Help = """
        Ascent Backtester · Motor C# de Estrategias y Empresas de Fondeo (ES/NQ)

        Futures help [--json]
        Futures prop-firms list [--workspace <carpeta>] [--json]
        Futures prop-firms evaluate --input <datos.txt> [--ticks] [--plan <id-plan>]
            [--instrument ES|NQ] [--fast 10] [--slow 20] [--quantity 1]
            [--stop-ticks 20] [--target-ticks 40]
            [--session-start 13:30] [--session-end 20:59]
            [--max-daily-loss 1000] [--max-daily-trades 5]
            [--commission 2.5] [--slippage 1] [--workspace <carpeta>] [--json]
        Futures strategies list [--json]
        Futures backtest run --input <datos.txt> [--ticks] [--instrument ES|NQ]
            [--fast 10] [--slow 20] [--quantity 1]
            [--stop-ticks 20] [--target-ticks 40]
            [--session-start 13:30] [--session-end 20:59]
            [--max-daily-loss 1000] [--max-daily-trades 5]
            [--commission 2.5] [--slippage 1]
            [--output <carpeta-nueva>] [--workspace <carpeta>] [--json]
        Futures traces compare --expected <signals.csv> --actual <signals.csv>
            [--context] [--tolerance 0.000000001] [--json]
        """;
}
