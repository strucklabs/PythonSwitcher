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
        var group = $"pip packages · Python {inst.Version}";
        CheckItem Info(string text, CheckStatus status = CheckStatus.Hinweis) =>
            new(CheckCategory.PipPaket, group, "pip", null, null, status, text, null, inst.Root);

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
        result.Add(new CheckItem(CheckCategory.PipPaket, group, SummaryName, $"{installed.Count} packages", null,
            old.Count == 0 ? CheckStatus.Aktuell : CheckStatus.UpdateVerfuegbar,
            old.Count == 0 ? "All packages are up to date." : $"{old.Count} of them outdated.", null, inst.Root));
        foreach (var p in old.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(new CheckItem(CheckCategory.PipPaket, group, p.Name, p.Version, p.Latest, CheckStatus.UpdateVerfuegbar,
                $"Update: \"{python}\" -m pip install --upgrade {p.Name}", null, inst.Root));
        }
        return result;
    }

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
