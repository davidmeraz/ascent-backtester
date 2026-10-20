using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Futures.Core;

namespace Futures.Analyzer;

public sealed record TraceDifference(int Row, string Field, string Expected, string Actual);
/// <summary>
/// Coverage counts describe the expected trace. Sufficiency requires equality and observed long and
/// short signals after warmup; it does not certify a native NinjaTrader import or compilation.
/// Source identifiers are reported independently because exporters and assemblies hash different inputs.
/// </summary>
public sealed record TraceComparisonResult(bool Matches, int ExpectedRows, int ActualRows, int DifferenceCount,
    bool ComparedOrderContext, double AbsoluteTolerance, IReadOnlyList<TraceDifference> Differences,
    int ReadyRows, int LongSignals, int ShortSignals, bool SufficientForPhaseOneSignalCheck,
    string ExpectedStrategySourceId, string ActualStrategySourceId);

public sealed class SignalTraceComparer
{
    public TraceComparisonResult CompareCsv(string expectedPath, string actualPath,
        bool compareOrderContext = false, double tolerance = 1e-9)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        var expected = Read(expectedPath);
        var actual = Read(actualPath);
        var differences = new List<TraceDifference>();
        var differenceCount = 0;
        void Difference(int row, string field, object? left, object? right)
        {
            differenceCount++;
            if (differences.Count < 100)
                differences.Add(new TraceDifference(row, field, Convert.ToString(left, CultureInfo.InvariantCulture) ?? "",
                    Convert.ToString(right, CultureInfo.InvariantCulture) ?? ""));
        }
        if (expected.FastPeriod != actual.FastPeriod) Difference(0, "FastPeriod", expected.FastPeriod, actual.FastPeriod);
        if (expected.SlowPeriod != actual.SlowPeriod) Difference(0, "SlowPeriod", expected.SlowPeriod, actual.SlowPeriod);
        if (expected.Quantity != actual.Quantity) Difference(0, "Quantity", expected.Quantity, actual.Quantity);
        if (expected.Rows.Count != actual.Rows.Count) Difference(0, "RowCount", expected.Rows.Count, actual.Rows.Count);
        for (var i = 0; i < Math.Min(expected.Rows.Count, actual.Rows.Count); i++)
        {
            var e = expected.Rows[i];
            var a = actual.Rows[i];
            var row = i + 2;
            if (e.TimeUtc != a.TimeUtc) Difference(row, "TimeUtc", e.TimeUtc.ToString("O"), a.TimeUtc.ToString("O"));
            CompareNumber("Open", e.Open, a.Open);
            CompareNumber("High", e.High, a.High);
            CompareNumber("Low", e.Low, a.Low);
            CompareNumber("Close", e.Close, a.Close);
            if (e.Volume != a.Volume) Difference(row, "Volume", e.Volume, a.Volume);
            CompareNumber("Fast", e.Fast, a.Fast);
            CompareNumber("Slow", e.Slow, a.Slow);
            if (e.Signal != a.Signal) Difference(row, "Signal", e.Signal, a.Signal);
            if (compareOrderContext)
            {
                if (e.TargetPositionQuantity != a.TargetPositionQuantity) Difference(row, "TargetPositionQuantity", e.TargetPositionQuantity, a.TargetPositionQuantity);
                if (e.PositionQuantity != a.PositionQuantity) Difference(row, "PositionQuantity", e.PositionQuantity, a.PositionQuantity);
                if (e.HasPendingOrder != a.HasPendingOrder) Difference(row, "HasPendingOrder", e.HasPendingOrder, a.HasPendingOrder);
            }
            void CompareNumber(string field, double? left, double? right)
            {
                if (left.HasValue != right.HasValue || (left.HasValue && Math.Abs(left.Value - right!.Value) > tolerance))
                    Difference(row, field, left?.ToString("G17", CultureInfo.InvariantCulture), right?.ToString("G17", CultureInfo.InvariantCulture));
            }
        }
        var readyRows = expected.Rows.Count(row => row.Fast.HasValue && row.Slow.HasValue);
        var longSignals = expected.Rows.Count(row => row.Fast.HasValue && row.Slow.HasValue && row.Signal == StrategySignal.Long);
        var shortSignals = expected.Rows.Count(row => row.Fast.HasValue && row.Slow.HasValue && row.Signal == StrategySignal.Short);
        var matches = differenceCount == 0;
        return new TraceComparisonResult(matches, expected.Rows.Count, actual.Rows.Count, differenceCount,
            compareOrderContext, tolerance, differences.AsReadOnly(), readyRows, longSignals, shortSignals,
            matches && readyRows > 0 && longSignals > 0 && shortSignals > 0,
            expected.StrategySourceId, actual.StrategySourceId);
    }

    private sealed record TraceDocument(IReadOnlyList<SignalTrace> Rows, int FastPeriod, int SlowPeriod,
        int Quantity, string StrategySourceId);

    private static TraceDocument Read(string path)
    {
        using var reader = File.OpenText(path);
        var headerLine = reader.ReadLine() ?? throw new DataValidationException(path, 1, "Missing trace CSV header.");
        var headers = ParseCsvLine(headerLine);
        if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Count)
            throw new DataValidationException(path, 1, "Duplicate trace CSV columns.");
        var columns = headers.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
        var required = new[] { "TimeUtc", "Open", "High", "Low", "Close", "Volume", "Fast", "Slow", "Signal", "TargetPositionQuantity", "PositionQuantity", "HasPendingOrder", "FastPeriod", "SlowPeriod", "Quantity", "StrategySourceId" };
        foreach (var column in required)
            if (!columns.ContainsKey(column)) throw new DataValidationException(path, 1, $"Missing trace CSV column: {column}.");
        var result = new List<SignalTrace>();
        StrategyParameters? parameters = null;
        string? sourceId = null;
        string? platformTimeZoneId = null;
        var lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            try
            {
                var values = ParseCsvLine(line);
                if (values.Count != headers.Count) throw new FormatException("CSV field count differs from header.");
                string Field(string name) => values[columns[name]];
                var rowParameters = new StrategyParameters(
                    int.Parse(Field("FastPeriod"), NumberStyles.None, CultureInfo.InvariantCulture),
                    int.Parse(Field("SlowPeriod"), NumberStyles.None, CultureInfo.InvariantCulture),
                    int.Parse(Field("Quantity"), NumberStyles.None, CultureInfo.InvariantCulture));
                if (parameters is not null && (parameters.FastPeriod != rowParameters.FastPeriod
                    || parameters.SlowPeriod != rowParameters.SlowPeriod || parameters.Quantity != rowParameters.Quantity))
                    throw new FormatException("FastPeriod, SlowPeriod and Quantity must remain constant within a trace.");
                parameters ??= rowParameters;
                var rowSourceId = Field("StrategySourceId");
                if (string.IsNullOrWhiteSpace(rowSourceId) || (sourceId is not null && sourceId != rowSourceId))
                    throw new FormatException("StrategySourceId must be non-empty and constant within a trace.");
                sourceId ??= rowSourceId;
                if (columns.ContainsKey("PlatformTimeZoneId"))
                {
                    var rowTimeZone = Field("PlatformTimeZoneId");
                    if (string.IsNullOrWhiteSpace(rowTimeZone) || (platformTimeZoneId is not null && platformTimeZoneId != rowTimeZone))
                        throw new FormatException("PlatformTimeZoneId must be non-empty and constant within a trace.");
                    platformTimeZoneId ??= rowTimeZone;
                }
                double Number(string name)
                {
                    var number = double.Parse(Field(name), NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(number)) throw new FormatException($"{name} must be finite.");
                    return number;
                }
                double? NullableNumber(string name) => Field(name).Length == 0 ? null : Number(name);
                var time = DateTime.ParseExact(Field("TimeUtc"), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (time.Kind != DateTimeKind.Utc) throw new FormatException("TimeUtc must use the UTC Z suffix.");
                if (result.Count > 0 && time <= result[result.Count - 1].TimeUtc)
                    throw new FormatException("Trace times must increase strictly.");
                if (!Enum.TryParse<StrategySignal>(Field("Signal"), false, out var signal) || signal.ToString() != Field("Signal") || !Enum.IsDefined(signal))
                    throw new FormatException("Signal must be None, Long or Short.");
                var volume = long.Parse(Field("Volume"), NumberStyles.None, CultureInfo.InvariantCulture);
                var bar = new Bar(time, Number("Open"), Number("High"), Number("Low"), Number("Close"), volume);
                result.Add(new SignalTrace(time, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume,
                    NullableNumber("Fast"), NullableNumber("Slow"), signal,
                    Field("TargetPositionQuantity").Length == 0 ? null : int.Parse(Field("TargetPositionQuantity"), CultureInfo.InvariantCulture),
                    int.Parse(Field("PositionQuantity"), CultureInfo.InvariantCulture), bool.Parse(Field("HasPendingOrder"))));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new DataValidationException(path, lineNumber, ex.Message);
            }
        }
        if (result.Count == 0) throw new DataValidationException(path, 2, "Trace contains no bars.");
        return new TraceDocument(result.AsReadOnly(), parameters!.FastPeriod, parameters.SlowPeriod,
            parameters.Quantity, sourceId!);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var afterQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; afterQuote = true; }
                }
                else field.Append(c);
            }
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); afterQuote = false; }
            else if (c == '"' && field.Length == 0 && !afterQuote) quoted = true;
            else
            {
                if (afterQuote || c == '"') throw new FormatException("Invalid quoting in trace CSV.");
                field.Append(c);
            }
        }
        if (quoted) throw new FormatException("Unterminated quoted CSV field.");
        fields.Add(field.ToString());
        return fields;
    }
}
