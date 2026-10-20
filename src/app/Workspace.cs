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
}
