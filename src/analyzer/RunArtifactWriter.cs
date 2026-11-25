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
        WriteDaily(result, Path.Combine(output, "daily.csv"));
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
        if (Directory.Exists(path))
            return DirectoryHash(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string DirectoryHash(string directoryPath)
    {
        string? parentDir = Path.GetDirectoryName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parentDir))
        {
            string catalogPath = Path.Combine(parentDir, "catalog.json");
            if (File.Exists(catalogPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(catalogPath));
                    if (doc.RootElement.TryGetProperty("combined_ticks_sha256", out var combined)
                        && combined.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(combined.GetString()))
                    {
                        return combined.GetString()!.Trim().ToLowerInvariant();
                    }
                }
                catch (JsonException) { }
                using var catStream = File.OpenRead(catalogPath);
                return Convert.ToHexString(SHA256.HashData(catStream)).ToLowerInvariant();
            }
        }

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        foreach (string file in Directory.EnumerateFiles(directoryPath, "*.txt", SearchOption.AllDirectories)
            .OrderBy(static p => p, StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hasher.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
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
        writer.WriteLine("TradeNumber,Side,Quantity,EntryTimeUtc,EntryPrice,ExitTimeUtc,ExitPrice,GrossProfit,Commission,NetProfit,MAE_USD,MFE_USD,ExitReason,Points,Ticks,DurationSeconds,InitialRiskUsd,RMultiple,MfeCaptureRatio,PeakToTroughDD_USD,CumulativeNetProfit");
        foreach (var t in result.Trades)
            writer.WriteLine(string.Join(",",
                t.TradeNumber.ToString(CultureInfo.InvariantCulture),
                t.Side,
                t.Quantity.ToString(CultureInfo.InvariantCulture),
                t.EntryTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                Number(t.EntryPrice),
                t.ExitTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                Number(t.ExitPrice),
                Number(t.GrossProfit),
                Number(t.Commission),
                Number(t.NetProfit),
                Number(t.MaxAdverseExcursionUsd),
                Number(t.MaxFavorableExcursionUsd),
                Csv(t.ExitReason),
                Number(t.Points),
                Number(t.Ticks),
                Number(t.DurationSeconds),
                NullableNumber(t.InitialRiskUsd),
                NullableNumber(t.RMultiple),
                NullableNumber(t.MfeCaptureRatio),
                Number(t.PeakToTroughDrawdownUsd),
                Number(t.CumulativeNetProfitUsd)));
    }

    private static void WriteDaily(BacktestResult result, string path)
    {
        if (result.DailyPerformance is null) return;
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("DateUtc,Trades,WinningTrades,LosingTrades,GrossProfit,Commission,NetProfit,CumulativeNetProfit,IntradayPeakEquity,MaxIntradayDrawdown,EndOfDayDrawdown");
        foreach (var d in result.DailyPerformance)
            writer.WriteLine(string.Join(",",
                d.DateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                d.Trades.ToString(CultureInfo.InvariantCulture),
                d.WinningTrades.ToString(CultureInfo.InvariantCulture),
                d.LosingTrades.ToString(CultureInfo.InvariantCulture),
                Number(d.GrossProfit),
                Number(d.Commission),
                Number(d.NetProfit),
                Number(d.CumulativeNetProfit),
                Number(d.IntradayPeakEquity),
                Number(d.MaxIntradayDrawdown),
                Number(d.EndOfDayDrawdown)));
    }

    private static string Number(double number) => number.ToString("G17", CultureInfo.InvariantCulture);
    private static string NullableNumber(double? number) => number.HasValue ? Number(number.Value) : "";
    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
}
