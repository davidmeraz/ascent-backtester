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

    public IReadOnlyList<Tick> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = File.OpenText(path);
        return Read(reader, Path.GetFullPath(path));
    }

    public IReadOnlyList<Tick> Read(TextReader reader, string sourceName = "input")
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ticks = new List<Tick>();
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
            if (ticks.Count > 0 && time < ticks[ticks.Count - 1].TimeUtc)
                throw Error("Tick timestamps must be in non-decreasing UTC order.");

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
        if (ticks.Count == 0)
            throw new DataValidationException(sourceName, 1, "The file contains no ticks.");
        return ticks.AsReadOnly();
    }
}
