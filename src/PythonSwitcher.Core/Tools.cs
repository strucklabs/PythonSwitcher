using System.Text.RegularExpressions;

namespace PythonSwitcher.Core;

public enum ToolKind
{
    Python,
    Git,
}

/// <summary>
/// Beschreibt ein verwaltetes Werkzeug: welche Programme es ausmachen,
/// welche PATH-Einträge eine Installation braucht und wie die Version gelesen wird.
/// </summary>
public sealed class ToolDefinition
{
    public required ToolKind Kind { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Hauptprogramm, an dem die aktive Version erkannt wird (z. B. python.exe).</summary>
    public required string MainExecutable { get; init; }

    /// <summary>Alle Programme, die bei einer anderen Installation als Konkurrenz zählen.</summary>
    public required IReadOnlyList<string> Executables { get; init; }

    /// <summary>PATH-Einträge, die eine Installation mit dem angegebenen Stammordner braucht, in dieser Reihenfolge.</summary>
    public required Func<string, IReadOnlyList<string>> RequiredEntries { get; init; }

    /// <summary>Ordner mit dem Hauptprogramm, ausgehend vom Stammordner.</summary>
    public required Func<string, string> MainExecutableDirectory { get; init; }

    /// <summary>Zusätzliche Einträge, die immer als Konkurrenz gelten (z. B. die Store-Weiterleitung).</summary>
    public Func<string, bool> IsAlwaysCompetitor { get; init; } = _ => false;

    /// <summary>Liest die Versionsnummer aus der Ausgabe von "--version"; null, wenn nicht erkennbar.</summary>
    public required Func<string, string?> ParseVersion { get; init; }

    public string MainExecutablePath(string root) => Path.Combine(MainExecutableDirectory(root), MainExecutable);

    public static readonly ToolDefinition Python = new()
    {
        Kind = ToolKind.Python,
        DisplayName = "Python",
        MainExecutable = "python.exe",
        Executables = ["python.exe", "pip.exe"],
        RequiredEntries = root => [Path.Combine(root, "Scripts"), root],
        MainExecutableDirectory = root => root,
        IsAlwaysCompetitor = entry => PathUtil.Normalize(entry)
            .EndsWith(@"\microsoft\windowsapps", StringComparison.Ordinal),
        ParseVersion = output =>
        {
            var m = Regex.Match(output, @"Python\s+(\d+\.\d+\.\d+\S*)");
            return m.Success ? m.Groups[1].Value : null;
        },
    };

    public static readonly ToolDefinition Git = new()
    {
        Kind = ToolKind.Git,
        DisplayName = "Git",
        MainExecutable = "git.exe",
        Executables = ["git.exe"],
        RequiredEntries = root => [Path.Combine(root, "cmd")],
        MainExecutableDirectory = root => Path.Combine(root, "cmd"),
        ParseVersion = output =>
        {
            var m = Regex.Match(output, @"git version\s+(\d+\.\d+\.\d+\S*)");
            return m.Success ? m.Groups[1].Value : null;
        },
    };

    public static IReadOnlyList<ToolDefinition> All { get; } = [Python, Git];

    public static ToolDefinition For(ToolKind kind) => All.First(t => t.Kind == kind);

    /// <summary>
    /// Ermittelt den Stammordner einer Installation aus dem Ordner, in dem das Hauptprogramm gefunden wurde.
    /// Bei Git liegt git.exe z. B. in "cmd" oder "mingw64\bin" – der Stammordner ist der, der "cmd\git.exe" enthält.
    /// </summary>
    public string? FindRoot(string directoryWithExecutable, Func<string, bool> fileExists)
    {
        var dir = PathUtil.Expand(directoryWithExecutable);
        for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
        {
            if (fileExists(MainExecutablePath(dir)))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
