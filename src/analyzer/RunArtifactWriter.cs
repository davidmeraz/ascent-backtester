using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Futures.Core;

namespace Futures.Analyzer;

public sealed class RunArtifactWriter
{
    public static JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Write(BacktestResult result, string outputDirectory, string sourceDataPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDataPath);
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException($"The output directory must be new or empty: {output}");
        var source = Path.GetFullPath(sourceDataPath);
        var dataHash = FileHash(source);
        var codeHash = FileHash(typeof(SmaCrossoverStrategy).Assembly.Location);
        Directory.CreateDirectory(output);
        WriteSignals(result, Path.Combine(output, "signals.csv"), codeHash);
        WriteTrades(result, Path.Combine(output, "trades.csv"));
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));
        var manifest = new
        {
            SchemaVersion = 2,
            Status = "Completed",
            CreatedUtc = DateTime.UtcNow,
            SourceDataPath = source,
            SourceDataSha256 = dataHash,
            SignalTraceSha256 = FileHash(Path.Combine(output, "signals.csv")),
            StrategyAssemblySha256 = codeHash,
            StrategyAssemblyVersion = typeof(SmaCrossoverStrategy).Assembly.GetName().Version?.ToString(),
            result.Configuration,
            result.Summary,
            HasOpenPosition = result.OpenPosition is not null,
            HasUnfilledFinalDecision = result.PendingDecision is not null
        };
        File.WriteAllText(Path.Combine(output, "run.json"), JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        return output;
    }

    public static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteSignals(BacktestResult result, string path, string codeHash)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("TimeUtc,Open,High,Low,Close,Volume,Fast,Slow,Signal,TargetPositionQuantity,PositionQuantity,HasPendingOrder,PlatformTimeZoneId,FastPeriod,SlowPeriod,Quantity,StrategySourceId");
        foreach (var s in result.Signals)
            writer.WriteLine(string.Join(",", s.TimeUtc.ToString("O", CultureInfo.InvariantCulture),
                Number(s.Open), Number(s.High), Number(s.Low), Number(s.Close), s.Volume.ToString(CultureInfo.InvariantCulture),
                NullableNumber(s.Fast), NullableNumber(s.Slow), s.Signal.ToString(), s.TargetPositionQuantity?.ToString(CultureInfo.InvariantCulture) ?? "",
                s.PositionQuantity.ToString(CultureInfo.InvariantCulture), s.HasPendingOrder ? "true" : "false", "UTC",
                result.Configuration.FastPeriod.ToString(CultureInfo.InvariantCulture), result.Configuration.SlowPeriod.ToString(CultureInfo.InvariantCulture),
                result.Configuration.Quantity.ToString(CultureInfo.InvariantCulture), codeHash));
    }

    private static void WriteTrades(BacktestResult result, string path)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("TradeNumber,Side,Quantity,EntryTimeUtc,EntryPrice,ExitTimeUtc,ExitPrice,GrossProfit,Commission,NetProfit,MAE_USD,MFE_USD,ExitReason");
        foreach (var t in result.Trades)
            writer.WriteLine(string.Join(",", t.TradeNumber.ToString(CultureInfo.InvariantCulture), t.Side, t.Quantity.ToString(CultureInfo.InvariantCulture),
                t.EntryTimeUtc.ToString("O", CultureInfo.InvariantCulture), Number(t.EntryPrice), t.ExitTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                Number(t.ExitPrice), Number(t.GrossProfit), Number(t.Commission), Number(t.NetProfit),
                Number(t.MaxAdverseExcursionUsd), Number(t.MaxFavorableExcursionUsd), Csv(t.ExitReason)));
    }

    private static string Number(double number) => number.ToString("G17", CultureInfo.InvariantCulture);
    private static string NullableNumber(double? number) => number.HasValue ? Number(number.Value) : "";
    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
}
