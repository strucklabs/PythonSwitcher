using System.Diagnostics;
using System.Text.Json;

namespace PythonSwitcher.Core.VersionCheck;

public enum CheckStatus
{
    Aktuell,
    UpdateVerfuegbar,
    Unbekannt,
    Hinweis,
}

/// <summary>Eine Zeile im Versionscheck.</summary>
public sealed record CheckItem(
    CheckCategory Category,
    string Group,
    string Name,
    string? Installed,
    string? Latest,
    CheckStatus Status,
    string Hint,
    string? Note = null,
    string? Detail = null);

public static class VersionCompare
{
    /// <summary>Vergleicht die Zahlenanteile zweier Versionen. Null, wenn eine davon nicht lesbar ist.</summary>
    public static int? Compare(string? a, string? b)
    {
        var pa = Parts(a);
        var pb = Parts(b);
        if (pa is null || pb is null)
            return null;
        for (int i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            long x = i < pa.Count ? pa[i] : 0, y = i < pb.Count ? pb[i] : 0;
            if (x != y)
                return x.CompareTo(y);
        }
        return 0;
    }

    private static List<long>? Parts(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return null;
        // Git für Windows: "2.56.0.windows.4" entspricht "2.56.0.4"
        v = System.Text.RegularExpressions.Regex.Replace(v, @"\.windows\.", ".", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var m = System.Text.RegularExpressions.Regex.Match(v, @"\d+(?:\.\d+)*");
        if (!m.Success)
            return null;
        return m.Value.Split('.').Select(s => long.TryParse(s, out var n) ? n : 0).ToList();
    }
}

/// <summary>Werkzeuge, Editoren und Sprachen aus dem Katalog erkennen und bewerten.</summary>
public sealed class ToolScan
{
    private readonly Func<string, string?> _findCommand;
    private readonly Func<string, string, string?> _runVersion;
    private readonly Func<string, CancellationToken, Task<string?>> _pypiLatest;
    private readonly IReadOnlyList<string> _pythonRoots;

    /// <param name="findCommand">Sucht ein Programm im PATH (wie ein neu geöffnetes Terminal) und liefert den vollen Pfad.</param>
    /// <param name="runVersion">Startet Programm + Argumente und liefert die Ausgabe.</param>
    /// <param name="pypiLatest">Neueste Version eines Pakets auf PyPI.</param>
    /// <param name="pythonRoots">Ordner der Python-Installationen; dort liegende Werkzeuge zählen zu den pip-Paketen.</param>
    public ToolScan(Func<string, string?> findCommand, Func<string, string, string?> runVersion,
        Func<string, CancellationToken, Task<string?>> pypiLatest, IReadOnlyList<string> pythonRoots)
    {
        _findCommand = findCommand;
        _runVersion = runVersion;
        _pypiLatest = pypiLatest;
        _pythonRoots = pythonRoots;
    }

    public static string GroupName(CheckCategory c) => c switch
    {
        CheckCategory.Python => "Python",
        CheckCategory.Git => "Git",
        CheckCategory.PythonWerkzeug => "Python-Werkzeuge",
        CheckCategory.Editor => "Editoren und Entwicklungsumgebungen",
        CheckCategory.Sprache => "Sprachen und Laufzeitumgebungen",
        CheckCategory.PipPaket => "pip-Pakete",
        _ => c.ToString(),
    };

    public async Task<List<CheckItem>> ScanAsync(IReadOnlyList<WingetPackage>? winget, IEnumerable<CatalogEntry> catalog, CancellationToken ct)
    {
        var result = new List<CheckItem>();
        foreach (var entry in catalog)
        {
            ct.ThrowIfCancellationRequested();
            var group = GroupName(entry.Category);
            var matches = (winget ?? [])
                .Where(p => entry.MatchesId(p.Id) || (p.Source is null && entry.MatchesName(p.Name)))
                .GroupBy(p => (p.Id, p.Version)).Select(g => g.First())
                .ToList();

            if (matches.Count > 0)
            {
                foreach (var p in matches)
                    result.Add(FromWinget(entry, group, p));
                continue;
            }

            if (entry.Command is null)
                continue;
            var path = _findCommand(entry.Command);
            if (path is null || _pythonRoots.Any(r => PathUtil.IsUnder(Path.GetDirectoryName(path)!, r)))
                continue; // nicht vorhanden, oder nur in einer Python-Version installiert (→ pip-Pakete)

            var installed = Catalog.ExtractVersion(_runVersion(path, entry.VersionArgs) ?? "");
            string? latest = entry.PyPiName is null ? null : await _pypiLatest(entry.PyPiName, ct);
            var cmp = VersionCompare.Compare(installed, latest);
            var status = cmp is null ? CheckStatus.Unbekannt : cmp < 0 ? CheckStatus.UpdateVerfuegbar : CheckStatus.Aktuell;
            var hint = status switch
            {
                CheckStatus.UpdateVerfuegbar => "Aktualisieren: " + (entry.UpdateCommand ?? "über die Website des Herstellers"),
                CheckStatus.Aktuell => "",
                _ => installed is null
                    ? "Gefunden, aber die Versionsnummer ließ sich nicht lesen."
                    : "Neueste Version nicht ermittelbar." + (entry.UpdateCommand is null ? "" : " Selbst prüfen: " + entry.UpdateCommand),
            };
            result.Add(new CheckItem(entry.Category, group, entry.Name, installed, latest, status, hint, entry.Note, path));
        }
        return result;
    }

    private static CheckItem FromWinget(CatalogEntry entry, string group, WingetPackage p)
    {
        CheckStatus status;
        string hint;
        string? latest;
        if (p.Uncertain)
        {
            status = CheckStatus.Unbekannt;
            latest = null;
            hint = "Die Angaben von winget ließen sich nicht sicher zuordnen.";
        }
        else if (p.Available is not null)
        {
            status = CheckStatus.UpdateVerfuegbar;
            latest = p.Available;
            hint = $"Aktualisieren: winget upgrade --id {p.Id}" + (entry.UpdateCommand is null ? "" : $" (oder: {entry.UpdateCommand})");
        }
        else if (p.Source is not null)
        {
            status = CheckStatus.Aktuell;
            latest = p.Version;
            hint = "";
        }
        else
        {
            status = CheckStatus.Unbekannt;
            latest = null;
            hint = "Nicht über winget bekannt; neueste Version beim Hersteller prüfen.";
        }
        return new CheckItem(entry.Category, group, entry.Name, p.Version, latest, status, hint, entry.Note, p.Name);
    }
}

/// <summary>Echte Umgebung: PATH wie ein neu geöffnetes Terminal, Programme starten, PyPI abfragen.</summary>
public sealed class SystemEnvironment
{
    private readonly IPathStore _paths;

    public SystemEnvironment(IPathStore paths)
    {
        _paths = paths;
    }

    /// <summary>PATH so, wie ihn ein neu gestartetes Programm bekommt: System, dann Benutzer.</summary>
    public string FreshPath() =>
        Environment.ExpandEnvironmentVariables(_paths.Read(PathScope.System) + ";" + _paths.Read(PathScope.Benutzer));

    public string? FindCommand(string name)
    {
        foreach (var entry in PathUtil.Split(FreshPath()))
        {
            var dir = PathUtil.Expand(entry);
            foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
            {
                var candidate = Path.Combine(dir, name + ext);
                try
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception)
                {
                }
            }
        }
        return null;
    }

    /// <summary>Startet ein Programm mit dem frischen PATH, liefert stdout+stderr oder null (Zeitlimit 15 s).</summary>
    public string? Run(string exe, string args, int timeoutMs = 15000)
    {
        try
        {
            bool script = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            var psi = script
                ? new ProcessStartInfo("cmd.exe", $"/d /c \"\"{exe}\" {args}\"")
                : new ProcessStartInfo(exe, args);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetTempPath();
            psi.Environment["PATH"] = FreshPath();
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            using var p = Process.Start(psi);
            if (p is null)
                return null;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }
            return o.Result + "\n" + e.Result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task<string?> PyPiLatestAsync(string package, CancellationToken ct)
    {
        try
        {
            var json = await ReleaseCatalog.Client.GetStringAsync($"https://pypi.org/pypi/{Uri.EscapeDataString(package)}/json", ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("info").GetProperty("version").GetString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
