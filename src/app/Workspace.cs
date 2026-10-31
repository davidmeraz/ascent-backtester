using System;
using System.IO;

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

    public static string? FindDatabentoMinuteFile(string workspace, bool adjusted = true)
    {
        string fileName = adjusted ? "NQ_1min_UTC_cierre_ajustado.txt" : "NQ_1min_UTC_cierre.txt";
        foreach (string candidate in new[]
        {
            Path.Combine(workspace, "data", "databento", "ohlcv-1m", fileName),
            Path.Combine(workspace, "ascent-backtester", "data", "databento", "ohlcv-1m", fileName),
            Path.GetFullPath(Path.Combine(workspace, "..", "ascent-backtester", "data", "databento", "ohlcv-1m", fileName))
        })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
