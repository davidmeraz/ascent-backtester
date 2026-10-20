using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Futures.Core;

namespace Futures.Analyzer;

public sealed class DataValidationException : FormatException
{
    public string SourceName { get; }
    public int LineNumber { get; }

    public DataValidationException(string sourceName, int lineNumber, string message)
        : base($"{sourceName}:{lineNumber}: {message}")
    {
        SourceName = sourceName;
        LineNumber = lineNumber;
    }
}

/// <summary>Reads NinjaTrader minute exports: UTC end-of-bar timestamps, six semicolon fields.</summary>
public sealed class NinjaTraderMinuteImporter
{
    public IReadOnlyList<Bar> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = File.OpenText(path);
        return Read(reader, Path.GetFullPath(path));
    }

    public IReadOnlyList<Bar> Read(TextReader reader, string sourceName = "input")
    {
        ArgumentNullException.ThrowIfNull(reader);
        var bars = new List<Bar>();
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var fields = line.Split(';');
            if (fields.Length != 6)
                throw Error("Expected timestamp;open;high;low;close;volume (six fields).");
            if (fields[0].Length != 15 || !DateTime.TryParseExact(fields[0], "yyyyMMdd HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var time) || time.Second != 0)
                throw Error("Expected an exact UTC minute-close timestamp yyyyMMdd HHmmss with seconds 00.");
            if (bars.Count > 0 && time <= bars[bars.Count - 1].CloseTimeUtc)
                throw Error("Timestamps must increase strictly; duplicates and unsorted rows are not accepted.");
            var open = ParsePrice(1);
            var high = ParsePrice(2);
            var low = ParsePrice(3);
            var close = ParsePrice(4);
            if (!long.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var volume))
                throw Error("Volume must be a non-negative integer.");
            try { bars.Add(new Bar(time, open, high, low, close, volume)); }
            catch (ArgumentException ex) { throw Error(ex.Message); }

            DataValidationException Error(string message) => new DataValidationException(sourceName, lineNumber, message);
            double ParsePrice(int field)
            {
                if (fields[field].Length == 0 || fields[field] != fields[field].Trim()
                    || !double.TryParse(fields[field], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var price) || !double.IsFinite(price))
                    throw Error($"Field {field + 1} must be a finite invariant decimal price.");
                return price;
            }
        }
        if (bars.Count == 0)
            throw new DataValidationException(sourceName, 1, "The file contains no minute bars.");
        return bars.AsReadOnly();
    }
}
