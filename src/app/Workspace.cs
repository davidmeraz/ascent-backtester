using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Futures.App.Commands;

internal static class Workspace
{
    public static string Find(string? specified)
    {
        if (specified != null)
        {
            string path = Path.GetFullPath(specified);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            return path;
        }
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory != null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "prop-firms"))
                    || File.Exists(Path.Combine(directory.FullName, "ascent-backtester", "FuturesLab.slnx"))
                    || File.Exists(Path.Combine(directory.FullName, "FuturesLab.slnx")))
                    return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("No se encontró el proyecto. Indica --workspace <carpeta>.");
    }

    public static string FindPropFirmsDirectory(string workspace)
    {
        foreach (string candidate in new[]
        {
            Path.Combine(workspace, "prop-firms"),
            Path.GetFullPath(Path.Combine(workspace, "..", "prop-firms"))
        })
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(workspace, "prop-firms");
    }

    public static bool DatasetExists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    public static string InferInstrumentFromPath(string path)
    {
        string name = Path.GetFileName(path);
        foreach (string symbol in new[] { "MNQ", "MES", "NQ", "ES" })
        {
            if (name.StartsWith(symbol + "_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, symbol, StringComparison.OrdinalIgnoreCase))
                return symbol;
        }
        for (DirectoryInfo? dir = new DirectoryInfo(Path.GetFullPath(path)).Parent; dir != null; dir = dir.Parent)
        {
            foreach (string symbol in new[] { "MNQ", "MES", "NQ", "ES" })
            {
                if (string.Equals(dir.Name, symbol, StringComparison.OrdinalIgnoreCase))
                    return symbol;
            }
        }
        return name.StartsWith("NQ", StringComparison.OrdinalIgnoreCase) ? "NQ" : "ES";
    }

    public static string? FindDatabentoMinuteFile(string workspace, bool adjusted = false) =>
        FindDatabentoMinuteFile(workspace, "NQ", adjusted);

    public static string? FindDatabentoMinuteFile(string workspace, string instrument, bool adjusted = false)
    {
        string suffix = adjusted ? "_1min_UTC_cierre_ajustado.txt" : "_1min_UTC_cierre.txt";
        foreach (string root in GetDatabentoRoots(workspace))
        {
            foreach (string inst in GetInstrumentCandidates(instrument))
            {
                string fileName = inst + suffix;
                string perInstrument = Path.Combine(root, inst, "ohlcv-1m", fileName);
                if (File.Exists(perInstrument)) return perInstrument;
                string legacy = Path.Combine(root, "ohlcv-1m", fileName);
                if (File.Exists(legacy)) return legacy;
            }
        }
        return null;
    }

    public static string? FindDatabentoRolloversFile(string workspace) =>
        FindDatabentoRolloversFile(workspace, "NQ");

    public static string? FindDatabentoRolloversFile(string workspace, string instrument)
    {
        const string fileName = "rollovers.json";
        foreach (string root in GetDatabentoRoots(workspace))
        {
            foreach (string inst in GetInstrumentCandidates(instrument))
            {
                string perInstrument = Path.Combine(root, inst, "ohlcv-1m", fileName);
                if (File.Exists(perInstrument)) return perInstrument;
                if (string.Equals(inst, "NQ", StringComparison.OrdinalIgnoreCase))
                {
                    string legacy = Path.Combine(root, "ohlcv-1m", fileName);
                    if (File.Exists(legacy)) return legacy;
                }
            }
        }
        return null;
    }

    public static string? FindDatabentoTbboFile(string workspace) =>
        FindDatabentoTbboFile(workspace, "NQ");

    public static string? FindDatabentoTbboFile(string workspace, string instrument)
    {
        foreach (string root in GetDatabentoRoots(workspace))
        {
            foreach (string inst in GetInstrumentCandidates(instrument))
            {
                string ticksDir = Path.Combine(root, inst, "tbbo", "ticks");
                if (HasPartitionFiles(ticksDir)) return ticksDir;

                string flatNew = Path.Combine(root, inst, "tbbo", $"{inst}_ticks_UTC_L1.txt");
                if (File.Exists(flatNew)) return flatNew;

                if (string.Equals(inst, "NQ", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string legacySubdir in new[] { "03_historia_completa", "historia-20100607-20190513", "" })
                    {
                        string legacy = Path.Combine(root, "tbbo", legacySubdir, "NQ_ticks_UTC_L1.txt");
                        if (File.Exists(legacy)) return legacy;
                    }
                }
            }
        }
        return null;
    }

    public static string? FindDatabentoTbboMinuteFile(string workspace) =>
        FindDatabentoTbboMinuteFile(workspace, "NQ");

    public static string? FindDatabentoTbboMinuteFile(string workspace, string instrument)
    {
        foreach (string root in GetDatabentoRoots(workspace))
        {
            foreach (string inst in GetInstrumentCandidates(instrument))
            {
                string perInstrument = Path.Combine(root, inst, "tbbo", $"{inst}_1min_UTC_cierre.txt");
                if (File.Exists(perInstrument)) return perInstrument;

                if (string.Equals(inst, "NQ", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string legacySubdir in new[] { "03_historia_completa", "historia-20100607-20190513", "" })
                    {
                        string legacy = Path.Combine(root, "tbbo", legacySubdir, "NQ_1min_UTC_cierre.txt");
                        if (File.Exists(legacy)) return legacy;
                    }
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> GetDatabentoRoots(string workspace) =>
    [
        Path.Combine(workspace, "data", "databento"),
        Path.Combine(workspace, "ascent-backtester", "data", "databento"),
        Path.GetFullPath(Path.Combine(workspace, "..", "ascent-backtester", "data", "databento"))
    ];

    private static IReadOnlyList<string> GetInstrumentCandidates(string instrument)
    {
        string normalized = string.IsNullOrWhiteSpace(instrument) ? "NQ" : instrument.Trim().ToUpperInvariant();
        return normalized switch
        {
            "MNQ" => ["MNQ", "NQ"],
            "MES" => ["MES", "ES"],
            _ => [normalized]
        };
    }

    private static bool HasPartitionFiles(string directory) =>
        Directory.Exists(directory)
        && Directory.EnumerateFiles(directory, "*.txt", SearchOption.AllDirectories).Any();
}
