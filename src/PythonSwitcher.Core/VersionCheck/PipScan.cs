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

    public PipScan(Func<string, IReadOnlyList<string>, int, CancellationToken, Task<RunResult?>>? run = null)
    {
        _run = run ?? RunAsync;
    }

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
            return result;
        }

        var old = ParseList(outdated.Out).Where(p => p.Latest is not null).ToList();
        var oldNames = old.Select(p => NormalizeName(p.Name)).ToHashSet();
        var outdatedSorted = old.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int current = installed.Count(p => !oldNames.Contains(NormalizeName(p.Name)));
        result.Add(new CheckItem(CheckCategory.PipPaket, group, SummaryName, $"{installed.Count} installed", null,
            old.Count == 0 ? CheckStatus.Aktuell : CheckStatus.UpdateVerfuegbar,
            old.Count == 0
                ? $"All {installed.Count} packages are up to date."
                : $"{old.Count} of {installed.Count} packages are outdated, {current} up to date. The update command updates all outdated ones.",
            null, inst.Root,
            old.Count == 0 ? null : UpgradeCommand(python, outdatedSorted.Select(p => p.Name)),
            summaryTarget));

        // Alle Pakete alphabetisch: veraltete mit neuer Version, aktuelle mit ihrer installierten Version
        // (pip hat für diese Python-Version nichts Neueres gefunden).
        var latestByName = old.ToDictionary(p => NormalizeName(p.Name), p => p.Latest!);
        foreach (var p in installed.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var target = new CheckTarget(TargetKind.PipPackage, ToolKind.Python, inst.Root, Package: p.Name);
            if (latestByName.TryGetValue(NormalizeName(p.Name), out var latest))
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
        return result;
    }

    public static string GroupFor(Installation inst) => $"pip packages · Python {inst.Version}";

    public static string UpgradeCommand(string python, IEnumerable<string> packages) =>
        $"\"{python}\" -m pip install --upgrade {string.Join(" ", packages)}";

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
        var cmd = UpgradeCommand(python, [package]);
        return previous with { Installed = found.Version, Status = CheckStatus.UpdateVerfuegbar, Hint = "Update: " + cmd, UpdateCommand = cmd };
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
