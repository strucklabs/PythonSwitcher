using System.Text.RegularExpressions;

namespace PythonSwitcher.Core.VersionCheck;

public enum CheckCategory
{
    Python,
    Git,
    PythonWerkzeug,
    Editor,
    Sprache,
    PipPaket,
}

/// <summary>Ein bekanntes Programm, nach dem gesucht wird.</summary>
public sealed class CatalogEntry
{
    public required CheckCategory Category { get; init; }
    public required string Name { get; init; }

    /// <summary>winget-IDs; ein "*" am Ende steht für beliebige Fortsetzung (z. B. "EclipseAdoptium.Temurin.*").</summary>
    public IReadOnlyList<string> WingetIds { get; init; } = [];

    /// <summary>Muster für den Anzeigenamen, falls winget das Programm nur aus der Windows-Programmliste kennt.</summary>
    public IReadOnlyList<string> NamePatterns { get; init; } = [];

    /// <summary>Programmname im PATH (ohne .exe) für die Erkennung ohne winget, z. B. "pipx".</summary>
    public string? Command { get; init; }
    public string VersionArgs { get; init; } = "--version";

    /// <summary>Paketname auf PyPI, um die neueste Version zu ermitteln (für Werkzeuge ohne winget).</summary>
    public string? PyPiName { get; init; }

    /// <summary>Eigener Befehl zum Aktualisieren, falls nicht über winget.</summary>
    public string? UpdateCommand { get; init; }

    /// <summary>Hinweis, z. B. wenn das Werkzeug selbst Python-Versionen oder den PATH verwaltet.</summary>
    public string? Note { get; init; }

    /// <summary>winget-IDs, die trotz passendem Muster nicht zu diesem Eintrag gehören.</summary>
    public IReadOnlyList<string> ExcludeIds { get; init; } = [];

    public bool MatchesId(string id)
    {
        if (ExcludeIds.Any(x => MatchPattern(x, id)))
            return false;
        return WingetIds.Any(p => MatchPattern(p, id));
    }

    public bool MatchesName(string name) => NamePatterns.Any(p => Regex.IsMatch(name, p, RegexOptions.IgnoreCase));

    private static bool MatchPattern(string pattern, string id) => pattern.EndsWith('*')
        ? id.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
        : id.Equals(pattern, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Liste der bekannten Programme. Erweiterbar: einfach Einträge ergänzen.</summary>
public static class Catalog
{
    private const string ManagesPython =
        "Verwaltet selbst Python-Versionen bzw. den PATH und kann sich mit dem Umsortieren durch dieses Programm überschneiden.";

    public static IReadOnlyList<CatalogEntry> Entries { get; } =
    [
        // ---------- Python-Werkzeuge ----------
        new() { Category = CheckCategory.PythonWerkzeug, Name = "Python Launcher (py)", WingetIds = ["Python.Launcher"],
            NamePatterns = [@"^Python Launcher$"] },
        new() { Category = CheckCategory.PythonWerkzeug, Name = "uv", WingetIds = ["astral-sh.uv"], Command = "uv",
            PyPiName = "uv", UpdateCommand = "uv self update", Note = ManagesPython },
        new() { Category = CheckCategory.PythonWerkzeug, Name = "pipx", Command = "pipx", PyPiName = "pipx",
            UpdateCommand = "python -m pip install --upgrade pipx" },
        new() { Category = CheckCategory.PythonWerkzeug, Name = "Poetry", Command = "poetry", PyPiName = "poetry",
            UpdateCommand = "poetry self update" },
        new() { Category = CheckCategory.PythonWerkzeug, Name = "Conda / Miniforge",
            WingetIds = ["Anaconda.Anaconda3", "Anaconda.Miniconda3", "CondaForge.Miniforge3", "CondaForge.Mambaforge"],
            NamePatterns = [@"^(Anaconda|Miniconda|Miniforge|Mambaforge)\d"], Command = "conda",
            UpdateCommand = "conda update conda", Note = ManagesPython },
        new() { Category = CheckCategory.PythonWerkzeug, Name = "pyenv-win", Command = "pyenv", Note = ManagesPython },
        new() { Category = CheckCategory.PythonWerkzeug, Name = "ruff", WingetIds = ["astral-sh.ruff"], Command = "ruff",
            PyPiName = "ruff" },
        new() { Category = CheckCategory.PythonWerkzeug, Name = "pre-commit", Command = "pre-commit", PyPiName = "pre-commit" },

        // ---------- Editoren und Entwicklungsumgebungen ----------
        new() { Category = CheckCategory.Editor, Name = "Visual Studio Code",
            WingetIds = ["Microsoft.VisualStudioCode", "Microsoft.VisualStudioCode.*"], NamePatterns = [@"^Microsoft Visual Studio Code"] },
        new() { Category = CheckCategory.Editor, Name = "Notepad++", WingetIds = ["Notepad++.Notepad++"], NamePatterns = [@"^Notepad\+\+"] },
        new() { Category = CheckCategory.Editor, Name = "PyCharm", WingetIds = ["JetBrains.PyCharm*"], NamePatterns = [@"^PyCharm"] },
        new() { Category = CheckCategory.Editor, Name = "Sublime Text", WingetIds = ["SublimeHQ.SublimeText*"], NamePatterns = [@"^Sublime Text"] },
        new() { Category = CheckCategory.Editor, Name = "Visual Studio",
            WingetIds = ["Microsoft.VisualStudio.20*"], ExcludeIds = ["Microsoft.VisualStudio.20*.BuildTools"],
            NamePatterns = [@"^Visual Studio (Community|Professional|Enterprise) 20\d\d"] },
        new() { Category = CheckCategory.Editor, Name = "Cursor", WingetIds = ["Anysphere.Cursor"], NamePatterns = [@"^Cursor\b"] },
        new() { Category = CheckCategory.Editor, Name = "Spyder", WingetIds = ["Spyder.Spyder"], NamePatterns = [@"^Spyder"] },
        new() { Category = CheckCategory.Editor, Name = "Thonny", WingetIds = ["AivarAnnamaa.Thonny"], NamePatterns = [@"^Thonny"] },
        new() { Category = CheckCategory.Editor, Name = "Vim", WingetIds = ["vim.vim"], NamePatterns = [@"^Vim \d"] },

        // ---------- Sprachen und Laufzeitumgebungen ----------
        new() { Category = CheckCategory.Sprache, Name = "Node.js", WingetIds = ["OpenJS.NodeJS", "OpenJS.NodeJS.*"],
            NamePatterns = [@"^Node\.js"], Command = "node" },
        new() { Category = CheckCategory.Sprache, Name = "Java",
            WingetIds = ["Oracle.JavaRuntimeEnvironment", "Oracle.JDK.*", "EclipseAdoptium.Temurin.*", "Microsoft.OpenJDK.*", "Azul.Zulu.*", "Amazon.Corretto.*"],
            NamePatterns = [@"^Java \d+ Update", @"^Java\(TM\) SE", @"^Eclipse Temurin", @"^Microsoft Build of OpenJDK"] },
        new() { Category = CheckCategory.Sprache, Name = ".NET SDK", WingetIds = ["Microsoft.DotNet.SDK.*"], NamePatterns = [@"^Microsoft \.NET SDK"] },
        new() { Category = CheckCategory.Sprache, Name = "Rust", WingetIds = ["Rustlang.Rustup", "Rustlang.Rust.*"],
            Command = "rustc", UpdateCommand = "rustup update" },
        new() { Category = CheckCategory.Sprache, Name = "Go", WingetIds = ["GoLang.Go"], NamePatterns = [@"^Go Programming Language"], Command = "go", VersionArgs = "version" },
        new() { Category = CheckCategory.Sprache, Name = "R", WingetIds = ["RProject.R"], NamePatterns = [@"^R for Windows"] },
        new() { Category = CheckCategory.Sprache, Name = "Julia", WingetIds = ["Julialang.Julia", "Julialang.Juliaup"], NamePatterns = [@"^Julia"], Command = "julia" },
        new() { Category = CheckCategory.Sprache, Name = "Lua", WingetIds = ["DEVCOM.Lua"], Command = "lua", VersionArgs = "-v" },
        new() { Category = CheckCategory.Sprache, Name = "Perl", WingetIds = ["StrawberryPerl.StrawberryPerl"], NamePatterns = [@"^Strawberry Perl"], Command = "perl", VersionArgs = "-v" },
        new() { Category = CheckCategory.Sprache, Name = "PHP", WingetIds = ["PHP.PHP.*"], Command = "php", VersionArgs = "-v" },
        new() { Category = CheckCategory.Sprache, Name = "Ruby", WingetIds = ["RubyInstallerTeam.Ruby*"], NamePatterns = [@"^Ruby \d"], Command = "ruby" },
        new() { Category = CheckCategory.Sprache, Name = "C++ Build Tools", WingetIds = ["Microsoft.VisualStudio.20*.BuildTools"],
            NamePatterns = [@"^Visual Studio Build Tools"] },
    ];

    /// <summary>Holt eine Versionsnummer aus beliebigem Text ("v22.1.0", "go version go1.23.4 windows/amd64" …).</summary>
    public static string? ExtractVersion(string text)
    {
        var m = Regex.Match(text, @"(?<![\w.])v?(?:go)?(\d+\.\d+(?:\.\d+)*)");
        return m.Success ? m.Groups[1].Value : null;
    }
}
