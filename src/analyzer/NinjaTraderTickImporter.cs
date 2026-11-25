using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Futures.Core;

namespace Futures.Analyzer;

/// <summary>
/// Reads NinjaTrader tick exports in 3-column (Timestamp;Price;Volume) or
/// 5-column Level 1 (Timestamp;Last;Bid;Ask;Volume) semicolon format.
/// </summary>
public sealed class NinjaTraderTickImporter
{
    private static readonly string[] TimestampFormats =
    [
        "yyyyMMdd HHmmss fffffff",
        "yyyyMMdd HHmmss fff",
        "yyyyMMdd HHmmss"
    ];

    public IReadOnlyList<Tick> Read(string path, DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
            return ReadDirectory(fullPath, fromUtc, toUtc);
        using var reader = File.OpenText(fullPath);
        return Read(reader, fullPath, fromUtc, toUtc);
    }

    public IReadOnlyList<Tick> Read(TextReader reader, string sourceName = "input", DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ticks = new List<Tick>();
        DateTime? previousTime = null;
        ReadInto(reader, sourceName, fromUtc, toUtc, ticks, ref previousTime);
        if (ticks.Count == 0)
            throw new DataValidationException(sourceName, 1, "The file contains no ticks in the requested range.");
        return ticks.AsReadOnly();
    }

    private IReadOnlyList<Tick> ReadDirectory(string directoryPath, DateTime? fromUtc, DateTime? toUtc)
    {
        var partitions = DiscoverMonthlyPartitions(directoryPath);
        if (partitions.Count == 0)
            throw new DataValidationException(directoryPath, 1, "The directory contains no monthly tick partition files (YYYY-MM.txt).");

        var ticks = new List<Tick>();
        DateTime? previousTime = null;
        foreach (var (year, month, filePath) in partitions)
        {
            var monthStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEndExclusive = monthStart.AddMonths(1);
            if (fromUtc.HasValue && monthEndExclusive <= fromUtc.Value)
                continue;
            if (toUtc.HasValue && monthStart > toUtc.Value)
                break;

            using var reader = File.OpenText(filePath);
            bool reachedEnd = ReadInto(reader, filePath, fromUtc, toUtc, ticks, ref previousTime);
            if (reachedEnd)
                break;
        }

        if (ticks.Count == 0)
            throw new DataValidationException(directoryPath, 1, "The file contains no ticks in the requested range.");
        return ticks.AsReadOnly();
    }

    private static List<(int Year, int Month, string Path)> DiscoverMonthlyPartitions(string directoryPath)
    {
        var result = new List<(int Year, int Month, string Path)>();
        foreach (string file in Directory.EnumerateFiles(directoryPath, "*.txt", SearchOption.AllDirectories))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (TryParseYearMonth(stem, out int year, out int month))
                result.Add((year, month, file));
        }
        result.Sort(static (a, b) =>
        {
            int cmp = a.Year.CompareTo(b.Year);
            if (cmp != 0) return cmp;
            cmp = a.Month.CompareTo(b.Month);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Path, b.Path);
        });
        return result;
    }

    private static bool TryParseYearMonth(string stem, out int year, out int month)
    {
        year = 0;
        month = 0;
        if (stem.Length != 7 || stem[4] != '-')
            return false;
        return int.TryParse(stem.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year)
            && int.TryParse(stem.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month)
            && year is >= 1900 and <= 2999
            && month is >= 1 and <= 12;
    }

    private static bool ReadInto(
        TextReader reader,
        string sourceName,
        DateTime? fromUtc,
        DateTime? toUtc,
        List<Tick> ticks,
        ref DateTime? previousTime)
    {
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split(';');
            if (fields.Length != 3 && fields.Length != 5)
                throw Error("Expected 3 fields (timestamp;price;volume) or 5 fields (timestamp;last;bid;ask;volume).");
            if (!DateTime.TryParseExact(fields[0].Trim(), TimestampFormats,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var time))
                throw Error("Expected UTC timestamp in format 'yyyyMMdd HHmmss [fffffff]'.");
            if (previousTime.HasValue && time < previousTime.Value)
                throw Error("Tick timestamps must be in non-decreasing UTC order.");
            previousTime = time;
            if (fromUtc.HasValue && time < fromUtc.Value)
                continue;
            if (toUtc.HasValue && time > toUtc.Value)
                return true;

            var price = ParsePrice(1);
            var bid = fields.Length == 5 ? ParsePrice(2) : price;
            var ask = fields.Length == 5 ? ParsePrice(3) : price;
            var volumeIndex = fields.Length == 5 ? 4 : 2;
            if (!long.TryParse(fields[volumeIndex].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var volume))
                throw Error("Volume must be a non-negative integer.");

            try { ticks.Add(new Tick(time, price, bid, ask, volume)); }
            catch (ArgumentException ex) { throw Error(ex.Message); }

            DataValidationException Error(string message) => new DataValidationException(sourceName, lineNumber, message);
            double ParsePrice(int field)
            {
                if (!double.TryParse(fields[field].Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                    throw Error($"Field {field + 1} must be a finite invariant decimal price.");
                return value;
            }
        }
        return false;
    }
}
