using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PythonSwitcher.Core.VersionCheck;

public sealed record PipPackage(string Name, string Version, string? Latest);

/// <summary>Ergebnis eines Programmaufrufs; null bedeutet: nicht gestartet oder Zeitlimit überschritten.</summary>
public sealed record RunResult(int ExitCode, string Out, string Err);

/// <summary>Fragt je Python-Installation die pip-Pakete ab (nur lesen, es wird nichts installiert).</summary>
public sealed class PipScan
{
    /// <summary>Name der Übersichtszeile je Python-Version.</summary>
    public const string SummaryName = "Summary";

    private readonly Func<string, IReadOnlyList<string>, int, CancellationToken, Task<RunResult?>> _run;
    private readonly Func<string, string?> _readFile;

    /// <param name="run">Ersatz für den Programmaufruf (Tests).</param>
    /// <param name="readFile">Ersatz für das Lesen von Dateien, hier der Markierung EXTERNALLY-MANAGED (Tests).</param>
    public PipScan(Func<string, IReadOnlyList<string>, int, CancellationToken, Task<RunResult?>>? run = null,
        Func<string, string?>? readFile = null)
    {
        _run = run ?? RunAsync;
        _readFile = readFile ?? (f => File.Exists(f) ? File.ReadAllText(f) : null);
    }

    /// <summary>
    /// Ist diese Python als "extern verwaltet" markiert (PEP 668, z. B. von uv), lehnt pip Änderungen ab.
    /// Liefert dann den Grund aus der Markierungsdatei, sonst null.
    /// </summary>
    public string? ExternallyManaged(string root)
    {
        var text = _readFile(Path.Combine(root, "Lib", "EXTERNALLY-MANAGED"));
        if (text is null)
            return null;
        var reason = text.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("Error", StringComparison.OrdinalIgnoreCase) && l.Contains('='));
        return reason is null ? "This Python installation is marked as externally managed." : reason[(reason.IndexOf('=') + 1)..].Trim();
    }

    private static string ManagedHint(string reason) =>
        $"Not updatable with pip: {reason} Packages of this Python are meant to be changed only by the tool that manages it.";

    public async Task<List<CheckItem>> ScanAsync(Installation inst, CancellationToken ct)
    {
        var python = ToolDefinition.Python.MainExecutablePath(inst.Root);
        var group = GroupFor(inst);
        var summaryTarget = new CheckTarget(TargetKind.PipSummary, ToolKind.Python, inst.Root);
        CheckItem Info(string text, CheckStatus status = CheckStatus.Hinweis) =>
            new(CheckCategory.PipPaket, group, "pip", null, null, status, text, null, inst.Root, Target: summaryTarget);

        var all = await _run(python, ["-m", "pip", "list", "--format=json", "--disable-pip-version-check"], 60_000, ct);
        if (all is null)
            return [Info("Python could not be started or did not respond in time.", CheckStatus.Unbekannt)];
        if (all.ExitCode != 0)
        {
            return all.Err.Contains("No module named pip", StringComparison.OrdinalIgnoreCase)
                ? [Info("pip is not installed for this Python version. To install it: python -m ensurepip --upgrade")]
                : [Info("pip reported an error: " + FirstLine(all.Err), CheckStatus.Unbekannt)];
        }
        var installed = ParseList(all.Out);

        var outdated = await _run(python, ["-m", "pip", "list", "--outdated", "--format=json", "--disable-pip-version-check"], 180_000, ct);
        var result = new List<CheckItem>();
        if (outdated is null || outdated.ExitCode != 0 || LooksOffline(outdated.Err))
        {
            result.Add(Info($"{installed.Count} packages installed. Newer versions could not be checked " +
                            "(no connection to PyPI or time limit exceeded).", CheckStatus.Unbekannt));
            result.AddRange(await DependencyRowsAsync(inst, ct)); // pip check braucht kein Netz
            return result;
        }

        var old = ParseList(outdated.Out).Where(p => p.Latest is not null).ToList();
        var oldNames = old.Select(p => NormalizeName(p.Name)).ToHashSet();
        var outdatedSorted = old.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int current = installed.Count(p => !oldNames.Contains(NormalizeName(p.Name)));
        var managed = ExternallyManaged(inst.Root);
        if (managed is not null)
        {
            // Nur zur Information: Updates per pip sind hier nicht vorgesehen und würden abgelehnt.
            result.Add(new CheckItem(CheckCategory.PipPaket, group, SummaryName, $"{installed.Count} installed", null,
                CheckStatus.Hinweis,
                $"{old.Count} of {installed.Count} packages have newer versions. " + ManagedHint(managed),
                null, inst.Root, Target: summaryTarget));
        }
        else
        {
            result.Add(new CheckItem(CheckCategory.PipPaket, group, SummaryName, $"{installed.Count} installed", null,
                old.Count == 0 ? CheckStatus.Aktuell : CheckStatus.UpdateVerfuegbar,
                old.Count == 0
                    ? $"All {installed.Count} packages are up to date."
                    : $"{old.Count} of {installed.Count} packages are outdated, {current} up to date. The update command updates all outdated ones.",
                null, inst.Root,
                old.Count == 0 ? null : UpgradeCommand(python, outdatedSorted.Select(p => p.Name)),
                summaryTarget));
        }

        result.AddRange(await DependencyRowsAsync(inst, ct));

        // Alle Pakete alphabetisch: veraltete mit neuer Version, aktuelle mit ihrer installierten Version
        // (pip hat für diese Python-Version nichts Neueres gefunden).
        var latestByName = old.ToDictionary(p => NormalizeName(p.Name), p => p.Latest!);
        foreach (var p in installed.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var target = new CheckTarget(TargetKind.PipPackage, ToolKind.Python, inst.Root, Package: p.Name);
            if (latestByName.TryGetValue(NormalizeName(p.Name), out var latest) && managed is not null)
            {
                result.Add(new CheckItem(CheckCategory.PipPaket, group, p.Name, p.Version, latest, CheckStatus.Hinweis,
                    "Newer version available. " + ManagedHint(managed), null, inst.Root, Target: target));
            }
            else if (latest is not null && SpecialBuildNote(p.Version, latest) is { } special)
            {
                // z. B. torch 2.3.0+cu121: ein Update von PyPI würde den Spezial-Build (GPU) ersetzen → kein Befehl
                result.Add(new CheckItem(CheckCategory.PipPaket, group, p.Name, p.Version, latest, CheckStatus.Hinweis,
                    special, null, inst.Root, Target: target));
            }
            else if (latest is not null)
            {
                var cmd = UpgradeCommand(python, [p.Name]);
                result.Add(new CheckItem(CheckCategory.PipPaket, group, p.Name, p.Version, latest, CheckStatus.UpdateVerfuegbar,
                    "Update: " + cmd, null, inst.Root, cmd, target));
            }
            else
            {
                result.Add(new CheckItem(CheckCategory.PipPaket, group, p.Name, p.Version, p.Version, CheckStatus.Aktuell,
                    "", null, inst.Root, Target: target));
            }
        }

        // Übersicht an die tatsächlichen Paketzeilen angleichen (Spezial-Builds zählen nicht als normales Update)
        if (managed is null && result.FindIndex(r => r.Name == SummaryName && r.Target?.Kind == TargetKind.PipSummary) is var si and >= 0)
            result[si] = UpdateSummary(result[si], result);
        return result;
    }

    /// <summary>
    /// Versionen mit Build-Kennung wie "2.3.0+cu121" stammen aus einem eigenen Paketindex (hier CUDA/GPU-Build von PyTorch).
    /// pip vergleicht mit PyPI und würde beim Update den Standard-Build installieren. Liefert dann einen Warnhinweis.
    /// </summary>
    public static string? SpecialBuildNote(string installed, string? latest)
    {
        int plus = installed.IndexOf('+');
        if (plus < 0 || latest is null || latest.Contains(installed[plus..], StringComparison.OrdinalIgnoreCase))
            return null;
        var label = installed[(plus + 1)..];
        return $"Special build (+{label}, for example a CUDA/GPU build) installed from a separate package index. " +
               $"An update from PyPI would replace it with the standard build {latest} and can remove that capability " +
               "(for example GPU support). Update it the way it was installed, e.g. with the index URL from the vendor's install instructions.";
    }

    public static string GroupFor(Installation inst) => $"pip packages · Python {inst.Version}";

    /// <summary>
    /// Befehl zum Aktualisieren. Ohne Leerzeichen im Pfad ohne Anführungszeichen – das funktioniert in PowerShell und cmd.
    /// Mit Leerzeichen PowerShell-Form mit Aufrufoperator (&amp; "…"), da PowerShell ein Programm in Anführungszeichen sonst
    /// nicht startet.
    /// </summary>
    public static string UpgradeCommand(string python, IEnumerable<string> packages) =>
        $"{Exe(python)} -m pip install --upgrade {string.Join(" ", packages)}";

    public static string Exe(string path) => path.Contains(' ') ? $"& \"{path}\"" : path;

    /// <summary>
    /// Einzelprüfung eines Pakets: liest nur die installierte Version (schnell, ohne Netz) und vergleicht sie mit der
    /// zuvor ermittelten neuesten Version, die pip bereits passend zu dieser Python-Version bestimmt hatte.
    /// </summary>
    public async Task<CheckItem> RecheckPackageAsync(CheckItem previous, Installation inst, CancellationToken ct)
    {
        var package = previous.Target?.Package ?? previous.Name;
        var python = ToolDefinition.Python.MainExecutablePath(inst.Root);
        var all = await _run(python, ["-m", "pip", "list", "--format=json", "--disable-pip-version-check"], 60_000, ct);
        if (all is null || all.ExitCode != 0)
            return previous with { Status = CheckStatus.Unbekannt, Hint = "The installed version could not be read (pip did not respond)." };

        var found = ParseList(all.Out).FirstOrDefault(p => NormalizeName(p.Name) == NormalizeName(package));
        if (found is null)
            return previous with { Installed = null, Status = CheckStatus.Hinweis, Hint = "The package is no longer installed.", UpdateCommand = null };

        var cmp = VersionCompare.Compare(found.Version, previous.Latest);
        if (cmp is >= 0)
            return previous with { Installed = found.Version, Status = CheckStatus.Aktuell, Hint = "", UpdateCommand = null };
        if (ExternallyManaged(inst.Root) is { } managed)
            return previous with { Installed = found.Version, Status = CheckStatus.Hinweis, Hint = "Newer version available. " + ManagedHint(managed), UpdateCommand = null };
        if (SpecialBuildNote(found.Version, previous.Latest) is { } special)
            return previous with { Installed = found.Version, Status = CheckStatus.Hinweis, Hint = special, UpdateCommand = null };
        // Immer noch veraltet: würde pip die neue Version überhaupt installieren? (Trockenlauf, ändert nichts)
        var reason = await WhyNotInstallableAsync(python, package, found.Version, previous.Latest, ct);
        if (reason is not null)
            return previous with { Installed = found.Version, Status = CheckStatus.Hinweis, Hint = reason, UpdateCommand = null };
        var cmd = UpgradeCommand(python, [package]);
        return previous with { Installed = found.Version, Status = CheckStatus.UpdateVerfuegbar, Hint = "Update: " + cmd, UpdateCommand = cmd };
    }

    /// <summary>
    /// Prüft per Trockenlauf (pip install --upgrade --dry-run -v), ob pip die neue Version installieren würde.
    /// Liefert den Grund, wenn nicht; null, wenn sie installierbar ist oder sich das nicht feststellen ließ.
    /// Hintergrund: pip verwirft eine nicht auflösbare neue Version stillschweigend, behält die alte und meldet Erfolg.
    /// </summary>
    public async Task<string?> WhyNotInstallableAsync(string python, string package, string installed, string? latest, CancellationToken ct)
    {
        if (latest is null)
            return null;
        var run = await _run(python, ["-m", "pip", "install", "--upgrade", package, "--dry-run", "-vv", "--disable-pip-version-check"], 300_000, ct);
        if (run is null)
            return null;
        return ExplainDryRun(run.Out + "\n" + run.Err, package, installed, latest);
    }

    internal static string? ExplainDryRun(string output, string package, string installed, string latest)
    {
        var lines = output.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();

        // "Would install a-1.0 b-2.0": steht die gewünschte Version darin, ist alles gut.
        foreach (var line in lines.Where(l => l.StartsWith("Would install ", StringComparison.Ordinal)))
        {
            foreach (var token in line["Would install ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int dash = token.LastIndexOf('-');
                if (dash > 0 && NormalizeName(token[..dash]) == NormalizeName(package)
                    && VersionCompare.Compare(token[(dash + 1)..], latest) == 0)
                    return null;
            }
        }

        // Nur Konflikte, die dieses Paket betreffen – als Verursacher ("x 1.0 depends on …") oder als Gefordertes ("… depends on x<2")
        bool Mentions(string line) => System.Text.RegularExpressions.Regex.Matches(line, @"[A-Za-z0-9][A-Za-z0-9._-]*")
            .Any(m => NormalizeName(m.Value) == NormalizeName(package));
        var conflicts = lines
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\S+ \S+ depends on \S+") && Mentions(l))
            .Distinct().ToList();
        // Fehlende Pakete nur nennen, wenn sie in einem dieser Konflikte vorkommen
        var missing = lines
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"Could not fetch URL https?://\S+/simple/([^/\s]+)/: 404"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct()
            .Where(name => conflicts.Any(c => c.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var parts = new List<string>();
        if (conflicts.Count > 0)
            parts.Add(string.Join("; ", conflicts));
        if (missing.Count > 0)
            parts.Add(string.Join(", ", missing) + (missing.Count == 1 ? " is" : " are") + " not available on PyPI");
        var why = parts.Count > 0
            ? string.Join(" – ", parts)
            : "probably another installed package requires an older version (pip did not name it)";
        return $"Version {latest} cannot be installed: {why}. pip keeps {installed} and still reports success, " +
               "so updating has no effect.";
    }

    /// <summary>
    /// Abhängigkeitsprüfung mit "pip check": eine Zeile "Dependencies" und je gefundenem Problem eine eigene Zeile.
    /// Es werden bewusst keine Reparatur-Befehle vorgeschlagen, da diese andere Pakete herunterstufen können.
    /// </summary>
    public async Task<List<CheckItem>> DependencyRowsAsync(Installation inst, CancellationToken ct)
    {
        var python = ToolDefinition.Python.MainExecutablePath(inst.Root);
        var group = GroupFor(inst);
        var target = new CheckTarget(TargetKind.PipCheck, ToolKind.Python, inst.Root);
        var run = await _run(python, ["-m", "pip", "check", "--disable-pip-version-check"], 60_000, ct);
        if (run is null)
        {
            return [new CheckItem(CheckCategory.PipPaket, group, DependenciesName, null, null, CheckStatus.Unbekannt,
                "The dependency check (pip check) did not respond.", null, inst.Root, Target: target)];
        }

        var problems = ParseCheck(run.Out + "\n" + run.Err);
        if (problems.Count == 0 && run.ExitCode == 0)
        {
            return [new CheckItem(CheckCategory.PipPaket, group, DependenciesName, "OK", null, CheckStatus.Aktuell,
                "No broken or missing dependencies (pip check).", null, inst.Root, Target: target)];
        }
        if (problems.Count == 0)
        {
            return [new CheckItem(CheckCategory.PipPaket, group, DependenciesName, null, null, CheckStatus.Unbekannt,
                "pip check reported an error: " + FirstLine(run.Err + run.Out), null, inst.Root, Target: target)];
        }

        var rows = new List<CheckItem>
        {
            new(CheckCategory.PipPaket, group, DependenciesName, $"{problems.Count} problem(s)", null, CheckStatus.Problem,
                $"{problems.Count} broken or missing dependenc{(problems.Count == 1 ? "y" : "ies")} (pip check). Packages may fail to import or " +
                "behave incorrectly. Fixing it can require other versions of the packages involved.",
                null, inst.Root, Target: target),
        };
        rows.AddRange(problems.Select(p => new CheckItem(CheckCategory.PipPaket, group, p.Package, p.Version, null,
            CheckStatus.Problem, p.Text, null, inst.Root, Target: target)));
        return rows;
    }

    public const string DependenciesName = "Dependencies";

    public enum ChangeKind { Upgrade, Downgrade, New, Reinstall }

    /// <summary>Eine Änderung, die pip vornehmen würde.</summary>
    public sealed record PlannedChange(string Package, string? From, string To, ChangeKind Kind, bool Requested);

    /// <summary>Ergebnis eines Trockenlaufs.</summary>
    /// <param name="NotInstallable">Gewünschte Pakete, deren neue Version pip nicht installieren würde, mit Grund.</param>
    public sealed record DryRunResult(IReadOnlyList<PlannedChange> Changes, IReadOnlyDictionary<string, string> NotInstallable, string? Error);

    /// <summary>
    /// Trockenlauf für ein oder mehrere Pakete: "pip install --upgrade … --dry-run --report" – es wird nichts installiert.
    /// Zeigt, welche Pakete pip ändern würde (auch mitgezogene und herabgestufte) und welche Updates nicht gehen.
    /// </summary>
    /// <param name="wanted">Paket → erwartete neue Version (aus dem Versionscheck).</param>
    public async Task<DryRunResult> DryRunAsync(Installation inst, IReadOnlyDictionary<string, string?> wanted, CancellationToken ct)
    {
        var python = ToolDefinition.Python.MainExecutablePath(inst.Root);
        var list = await _run(python, ["-m", "pip", "list", "--format=json", "--disable-pip-version-check"], 60_000, ct);
        if (list is null || list.ExitCode != 0)
            return new DryRunResult([], new Dictionary<string, string>(), "The installed packages could not be read.");
        var installed = ParseList(list.Out).ToDictionary(p => NormalizeName(p.Name), p => p.Version);

        var report = Path.Combine(Path.GetTempPath(), $"PythonSwitcher-dryrun-{Guid.NewGuid():N}.json");
        try
        {
            var args = new List<string> { "-m", "pip", "install", "--upgrade" };
            args.AddRange(wanted.Keys);
            args.AddRange(["--dry-run", "-vv", "--report", report, "--disable-pip-version-check"]);
            var run = await _run(python, args, 600_000, ct);
            if (run is null)
                return new DryRunResult([], new Dictionary<string, string>(), "pip did not respond in time.");
            var json = _readFile(report);
            if (json is null)
                return new DryRunResult([], new Dictionary<string, string>(), "pip failed: " + FirstLine(run.Err + "\n" + run.Out));
            return EvaluateDryRun(json, run.Out + "\n" + run.Err, installed, wanted);
        }
        finally
        {
            try { File.Delete(report); } catch (Exception) { }
        }
    }

    /// <summary>Ergebnis eines Trockenlaufs als lesbarer Text für die Anzeige.</summary>
    public static string DescribeDryRun(DryRunResult r)
    {
        if (r.Error is not null)
            return r.Error;
        var sb = new System.Text.StringBuilder("Nothing was installed. This is what pip would do:\n\n");
        if (r.Changes.Count == 0)
            sb.Append("No changes.\n");
        foreach (var c in r.Changes)
        {
            var what = c.Kind switch
            {
                ChangeKind.New => $"{c.Package}: new, {c.To}",
                ChangeKind.Reinstall => $"{c.Package}: reinstall {c.To}",
                _ => $"{c.Package}: {c.From} → {c.To}",
            };
            var notes = new List<string>();
            if (c.From is not null && SpecialBuildNote(c.From, c.To) is not null)
                notes.Add($"BUILD CHANGES: +{c.From[(c.From.IndexOf('+') + 1)..]} would be replaced by the standard build");
            if (c.Kind == ChangeKind.Downgrade) notes.Add("DOWNGRADE");
            if (!c.Requested) notes.Add("changed as a dependency");
            sb.Append("   ").Append(what).Append(notes.Count > 0 ? $"   ({string.Join(", ", notes)})" : "").Append('\n');
        }
        if (r.NotInstallable.Count > 0)
        {
            sb.Append("\nCannot be updated:\n");
            foreach (var (pkg, why) in r.NotInstallable)
                sb.Append("   ").Append(pkg).Append(": ").Append(why).Append('\n');
        }
        if (r.Changes.Any(c => c.From is not null && SpecialBuildNote(c.From, c.To) is not null))
            sb.Append("\nWARNING: special builds (e.g. CUDA/GPU builds) would be replaced by standard builds from PyPI. " +
                      "This can remove GPU support. Do not run this update unless that is intended.\n");
        if (r.Changes.Any(c => c.Kind == ChangeKind.Downgrade || !c.Requested))
            sb.Append("\nPackages marked as downgrade or dependency would change although you did not select them. " +
                      "Check whether other programs need their current versions.");
        return sb.ToString();
    }

    internal static DryRunResult EvaluateDryRun(string reportJson, string log, IReadOnlyDictionary<string, string> installed,
        IReadOnlyDictionary<string, string?> wanted)
    {
        var changes = new List<PlannedChange>();
        using (var doc = JsonDocument.Parse(reportJson))
        {
            if (doc.RootElement.TryGetProperty("install", out var install))
            {
                foreach (var e in install.EnumerateArray())
                {
                    var meta = e.GetProperty("metadata");
                    var name = meta.GetProperty("name").GetString() ?? "";
                    var version = meta.GetProperty("version").GetString() ?? "";
                    bool requested = e.TryGetProperty("requested", out var r) && r.ValueKind == JsonValueKind.True;
                    installed.TryGetValue(NormalizeName(name), out var from);
                    var cmp = VersionCompare.Compare(version, from);
                    var kind = from is null ? ChangeKind.New : cmp > 0 ? ChangeKind.Upgrade : cmp < 0 ? ChangeKind.Downgrade : ChangeKind.Reinstall;
                    changes.Add(new PlannedChange(name, from, version, kind, requested));
                }
            }
        }

        var notInstallable = new Dictionary<string, string>();
        foreach (var (pkg, latest) in wanted)
        {
            var change = changes.FirstOrDefault(c => NormalizeName(c.Package) == NormalizeName(pkg));
            installed.TryGetValue(NormalizeName(pkg), out var current);
            // Schon auf (mindestens) dieser Version – z. B. inzwischen von Hand aktualisiert: kein Problem
            if (latest is not null && change is null && VersionCompare.Compare(current, latest) >= 0)
                continue;
            if (latest is not null && (change is null || VersionCompare.Compare(change.To, latest) < 0))
            {
                notInstallable[pkg] = ExplainDryRun(log, pkg, current ?? "?", latest) ?? $"pip would not install version {latest}.";
            }
        }
        return new DryRunResult(changes.OrderBy(c => c.Package, StringComparer.OrdinalIgnoreCase).ToList(), notInstallable, null);
    }

    public sealed record CheckProblem(string Package, string? Version, string Text);

    /// <summary>
    /// Liest die Ausgabe von "pip check", z. B.
    /// "torchvision 0.18.0 has requirement torch==2.3.0, but you have torch 2.4.0." oder
    /// "xyz 1.0 requires abc, which is not installed."
    /// </summary>
    internal static List<CheckProblem> ParseCheck(string output)
    {
        var result = new List<CheckProblem>();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\S+) (\S+) (has requirement|requires|is not supported) .+");
            if (m.Success)
                result.Add(new CheckProblem(m.Groups[1].Value, m.Groups[2].Value, line));
        }
        return result;
    }

    /// <summary>
    /// Baut die Übersichtszeile aus den aktuellen Paketzeilen neu auf – nach der Einzelprüfung eines Pakets,
    /// damit Zahlen und Sammelbefehl stimmen, ohne die ganze Python-Version neu zu prüfen.
    /// </summary>
    public static CheckItem UpdateSummary(CheckItem summary, IEnumerable<CheckItem> rows)
    {
        var pkgs = rows.Where(r => r.Target?.Kind == TargetKind.PipPackage && r.Installed is not null).ToList();
        if (summary.Target?.Root is not { } root || pkgs.Any(r => r.Hint.Contains("Not updatable with pip")))
            return summary; // extern verwaltet: Übersicht bleibt ein reiner Hinweis
        var python = ToolDefinition.Python.MainExecutablePath(root);
        var outdated = pkgs.Where(r => r.Status == CheckStatus.UpdateVerfuegbar).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int current = pkgs.Count(r => r.Status == CheckStatus.Aktuell);
        int blocked = pkgs.Count(r => r.Status == CheckStatus.Hinweis && VersionCompare.Compare(r.Installed, r.Latest) < 0);
        int n = pkgs.Count;

        string hint = outdated.Count == 0 && blocked == 0
            ? $"All {n} packages are up to date."
            : $"{outdated.Count} of {n} packages are outdated, {current} up to date" +
              (blocked > 0 ? $", {blocked} need attention (see their rows)" : "") + "." +
              (outdated.Count > 0 ? " The update command updates all outdated ones." : "");
        return summary with
        {
            Installed = $"{n} installed",
            Status = outdated.Count > 0 ? CheckStatus.UpdateVerfuegbar : blocked > 0 ? CheckStatus.Hinweis : CheckStatus.Aktuell,
            Hint = hint,
            UpdateCommand = outdated.Count > 0 ? UpgradeCommand(python, outdated.Select(r => r.Target?.Package ?? r.Name)) : null,
        };
    }

    /// <summary>Paketnamen vergleichen wie pip: Groß/klein sowie "-", "_" und "." gelten als gleich.</summary>
    private static string NormalizeName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[-_.]+", "-");

    internal static List<PipPackage> ParseList(string json)
    {
        var start = json.IndexOf('[');
        if (start < 0)
            return [];
        using var doc = JsonDocument.Parse(json[start..]);
        return doc.RootElement.EnumerateArray()
            .Select(e => new PipPackage(
                e.GetProperty("name").GetString() ?? "",
                e.GetProperty("version").GetString() ?? "",
                e.TryGetProperty("latest_version", out var l) ? l.GetString() : null))
            .ToList();
    }

    private static bool LooksOffline(string err) =>
        err.Contains("Retrying", StringComparison.OrdinalIgnoreCase) ||
        err.Contains("Could not fetch", StringComparison.OrdinalIgnoreCase) ||
        err.Contains("NewConnectionError", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string s) => s.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";

    private static async Task<RunResult?> RunAsync(string exe, IReadOnlyList<string> args, int timeoutMs, CancellationToken ct)
    {
        if (!File.Exists(exe))
            return null;
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        psi.Environment["PIP_NO_INPUT"] = "1";
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
                return null;
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                ct.ThrowIfCancellationRequested();
                return null;
            }
            return new RunResult(p.ExitCode, await o, await e);
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
