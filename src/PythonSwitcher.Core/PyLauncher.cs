using System.Text.RegularExpressions;

namespace PythonSwitcher.Core;

/// <summary>Welche Python-Version der py-Launcher ohne weitere Angabe startet, und woher diese Vorgabe kommt.</summary>
/// <param name="Tag">Vorgabe wie "3.13" oder null, wenn keine gesetzt ist.</param>
/// <param name="Source">Herkunft für die Anzeige.</param>
/// <param name="Installation">Passende Installation aus der Liste, falls gefunden.</param>
public sealed record PyDefault(string? Tag, string Source, Installation? Installation);

public static class PyLauncher
{
    /// <summary>
    /// Ermittelt die Vorgabe in der Reihenfolge des Launchers: Umgebungsvariable PY_PYTHON,
    /// py.ini in %LOCALAPPDATA%, py.ini neben der benutzten py.exe. Ohne Vorgabe: neueste Version.
    /// </summary>
    /// <param name="pythons">Python-Installationen aus der Liste.</param>
    /// <param name="envVar">Liest eine Umgebungsvariable so, wie ein neues Terminal sie sieht.</param>
    /// <param name="iniFiles">Mögliche py.ini-Dateien in Prüfreihenfolge.</param>
    /// <param name="readFile">Liest eine Datei; null, wenn es sie nicht gibt.</param>
    public static PyDefault Resolve(IEnumerable<Installation> pythons, Func<string, string?> envVar,
        IEnumerable<string> iniFiles, Func<string, string?> readFile)
    {
        var list = pythons.Where(p => p.Tool == ToolKind.Python).ToList();

        var env = envVar("PY_PYTHON");
        if (!string.IsNullOrWhiteSpace(env))
            return new PyDefault(env.Trim(), "environment variable PY_PYTHON", Match(list, env));

        foreach (var ini in iniFiles)
        {
            var text = readFile(ini);
            if (text is null)
                continue;
            var tag = ReadDefault(text);
            if (tag is not null)
                return new PyDefault(tag, $"{ini} (python={tag})", Match(list, tag));
        }

        var newest = list.OrderByDescending(p => Switcher.VersionKey(p.Version)).FirstOrDefault();
        return new PyDefault(null, "no default set, so the newest installed version is used", newest);
    }

    /// <summary>Liest "python=…" aus dem Abschnitt [defaults] einer py.ini.</summary>
    internal static string? ReadDefault(string ini)
    {
        bool inDefaults = false;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inDefaults = line.Equals("[defaults]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inDefaults || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            var m = Regex.Match(line, @"^python\s*=\s*(\S+)", RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>"3.13", "3.13-64" oder "3" → neueste passende Installation.</summary>
    internal static Installation? Match(IReadOnlyList<Installation> pythons, string tag)
    {
        var m = Regex.Match(tag.Trim(), @"^(\d+)(?:\.(\d+))?");
        if (!m.Success)
            return null;
        int major = int.Parse(m.Groups[1].Value);
        int? minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null;
        return pythons
            .Where(p =>
            {
                var v = Switcher.VersionKey(p.Version);
                return v.Major == major && (minor is null || v.Minor == minor);
            })
            .OrderByDescending(p => Switcher.VersionKey(p.Version))
            .FirstOrDefault();
    }
}
