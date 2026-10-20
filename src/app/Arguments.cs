using System;
using System.Collections.Generic;
using System.Globalization;

namespace Futures.App.Commands;

internal sealed class Arguments
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> switches = new(StringComparer.Ordinal);
    public List<string> Words { get; } = new();
    public bool Json => switches.Contains("json");

    public Arguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                Words.Add(arg);
                continue;
            }
            string name = arg.Substring(2);
            if (values.ContainsKey(name) || switches.Contains(name))
                throw new ArgumentException("Opción repetida: --" + name);
            if (name is "json" or "draft" or "context" or "help" or "ticks")
                switches.Add(name);
            else
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Falta valor para --" + name);
                values.Add(name, args[i]);
            }
        }
    }

    public string? Optional(string name) => values.TryGetValue(name, out string? value) ? value : null;
    public string Required(string name) => Optional(name) ?? throw new ArgumentException("Falta --" + name);
    public bool Has(string name) => switches.Contains(name);
    public int Integer(string name, int fallback) => Optional(name) is string text
        ? int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) : fallback;
    public double Number(string name, double fallback) => Optional(name) is string text
        ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) : fallback;

    public void Allow(params string[] names)
    {
        var allowed = new HashSet<string>(names, StringComparer.Ordinal) { "json", "workspace", "help" };
        foreach (string name in values.Keys)
            if (!allowed.Contains(name)) throw new ArgumentException("Opción desconocida: --" + name);
        foreach (string name in switches)
            if (!allowed.Contains(name)) throw new ArgumentException("Opción desconocida: --" + name);
    }
}
