using System.Collections.Concurrent;
using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

/// <summary>Trockenlauf je Update im kompletten Check: was zieht die neue Version selbst nach?</summary>
public class UpdateAnalysisTests
{
    private static readonly Installation Py = new() { Tool = ToolKind.Python, Root = @"C:\Py310", Version = "3.10.11" };

    /// <summary>
    /// Simuliertes pip: pro Trockenlauf wird anhand der angefragten Pakete ein Bericht gewählt und unter dem
    /// --report-Pfad abgelegt (threadsicher, da Trockenläufe parallel laufen).
    /// </summary>
    private sealed class FakePip
    {
        public string List = "[]";
        public string Outdated = "[]";
        public Func<IReadOnlyList<string>, string> ReportFor = _ => """{"install": []}""";
        public int DryRuns;
        private readonly ConcurrentDictionary<string, string> _reports = new();

        public PipScan Scan() => new(
            (_, args, _, _) =>
            {
                if (args.Contains("--dry-run"))
                {
                    Interlocked.Increment(ref DryRuns);
                    var list = args.ToList();
                    int iu = list.IndexOf("--upgrade"), idr = list.IndexOf("--dry-run");
                    var packages = list.Skip(iu + 1).Take(idr - iu - 1).ToList();
                    int ir = list.IndexOf("--report");
                    if (ir >= 0) _reports[list[ir + 1]] = ReportFor(packages);
                    return Task.FromResult<RunResult?>(new(0, "", ""));
                }
                return Task.FromResult<RunResult?>(new(0,
                    args.Contains("-c") ? """{"breaks": [], "ranges": {}}"""
                    : args.Contains("check") ? "No broken requirements found."
                    : args.Contains("--outdated") ? Outdated : List, ""));
            },
            f => _reports.TryGetValue(f, out var json) ? json : null);
    }

    private static string Install(params (string Name, string Version, bool Requested)[] items) =>
        "{\"install\": [" + string.Join(",", items.Select(i =>
            $"{{\"metadata\": {{\"name\": \"{i.Name}\", \"version\": \"{i.Version}\"}}, \"requested\": {(i.Requested ? "true" : "false")}}}")) + "]}";

    private static FakePip XformersCase() => new()
    {
        List = """[{"name": "torch", "version": "2.3.0+cu121"}, {"name": "xformers", "version": "0.0.26"}, {"name": "Pillow", "version": "10.0.0"}]""",
        Outdated = """[{"name": "torch", "version": "2.3.0+cu121", "latest_version": "2.14.1"}, {"name": "xformers", "version": "0.0.26", "latest_version": "0.0.35"}, {"name": "Pillow", "version": "10.0.0", "latest_version": "12.1.0"}]""",
        ReportFor = pkgs => pkgs.Contains("xformers")
            ? Install(("xformers", "0.0.35", true), ("torch", "2.14.1", false))
            : Install(("Pillow", "12.1.0", true)),
    };

    [Fact]
    public async Task Update_that_pulls_in_a_standard_torch_is_flagged_and_left_out_of_the_summary()
    {
        var items = await XformersCase().Scan().ScanAsync(Py, default);

        var xformers = items.Single(i => i.Name == "xformers");
        Assert.Equal(CheckStatus.Hinweis, xformers.Status);
        Assert.Null(xformers.UpdateCommand);
        Assert.Contains("replace the special build torch 2.3.0+cu121 with the standard build 2.14.1", xformers.Hint);

        var pillow = items.Single(i => i.Name == "Pillow");
        Assert.Equal(CheckStatus.UpdateVerfuegbar, pillow.Status);

        var summary = items.Single(i => i.Name == PipScan.SummaryName);
        Assert.Equal(@"C:\Py310\python.exe -m pip install --upgrade Pillow", summary.UpdateCommand);
    }

    [Fact]
    public async Task Single_recheck_also_runs_the_analysis()
    {
        var pip = XformersCase();
        pip.List = """[{"name": "torch", "version": "2.3.0+cu121"}, {"name": "xformers", "version": "0.0.26"}]""";
        var row = new CheckItem(CheckCategory.PipPaket, "g", "xformers", "0.0.26", "0.0.35", CheckStatus.UpdateVerfuegbar, "", null, Py.Root,
            "x", new CheckTarget(TargetKind.PipPackage, ToolKind.Python, Py.Root, Package: "xformers"));
        // Trockenlauf für die Installierbarkeit sieht "Would install" nicht → würde als nicht installierbar gelten;
        // daher hier über den Bericht prüfen: der erste Trockenlauf (ohne --report) liefert "Would install".
        var scan = new PipScan(
            (_, args, _, _) => Task.FromResult<RunResult?>(new(0,
                args.Contains("--dry-run") && !args.Contains("--report") ? "Would install xformers-0.0.35 torch-2.14.1"
                : args.Contains("--dry-run") ? ""
                : args.Contains("-c") ? """{"breaks": [], "ranges": {}}"""
                : pip.List, "")),
            f => f.Contains("dryrun") ? Install(("xformers", "0.0.35", true), ("torch", "2.14.1", false)) : null);
        var again = await scan.RecheckPackageAsync(row, Py, default);
        Assert.Equal(CheckStatus.Hinweis, again.Status);
        Assert.Contains("special build torch 2.3.0+cu121", again.Hint);
    }

    [Fact]
    public async Task Problem_only_in_the_combined_update_removes_the_summary_command()
    {
        var pip = new FakePip
        {
            List = """[{"name": "a", "version": "1.0"}, {"name": "b", "version": "1.0"}, {"name": "c", "version": "5.0"}]""",
            Outdated = """[{"name": "a", "version": "1.0", "latest_version": "2.0"}, {"name": "b", "version": "1.0", "latest_version": "2.0"}]""",
            // Einzeln harmlos, zusammen wird c herabgestuft
            ReportFor = pkgs => pkgs.Count == 1
                ? Install((pkgs[0], "2.0", true))
                : Install(("a", "2.0", true), ("b", "2.0", true), ("c", "4.0", false)),
        };
        var items = await pip.Scan().ScanAsync(Py, default);
        Assert.Equal(2, items.Count(i => i.Status == CheckStatus.UpdateVerfuegbar && i.Target?.Kind == TargetKind.PipPackage));
        var summary = items.Single(i => i.Name == PipScan.SummaryName);
        Assert.Null(summary.UpdateCommand);
        Assert.Contains("Updating them all at once would cause problems: it would downgrade c 5.0 → 4.0", summary.Hint);
        Assert.Equal(3, pip.DryRuns); // 2 einzeln + 1 gemeinsam
    }

    [Fact]
    public async Task Too_many_outdated_packages_are_not_tested_one_by_one()
    {
        var names = Enumerable.Range(1, PipScan.MaxDryRunsPerCheck + 1).Select(i => $"p{i}").ToList();
        var pip = new FakePip
        {
            List = "[" + string.Join(",", names.Select(n => $"{{\"name\": \"{n}\", \"version\": \"1.0\"}}")) + "]",
            Outdated = "[" + string.Join(",", names.Select(n => $"{{\"name\": \"{n}\", \"version\": \"1.0\", \"latest_version\": \"2.0\"}}")) + "]",
        };
        var items = await pip.Scan().ScanAsync(Py, default);
        Assert.Equal(0, pip.DryRuns);
        Assert.Contains("were not each tested with a dry run", items.Single(i => i.Name == PipScan.SummaryName).Hint);
    }

    [Fact]
    public async Task Clean_updates_stay_updates()
    {
        var pip = new FakePip
        {
            List = """[{"name": "Pillow", "version": "10.0.0"}]""",
            Outdated = """[{"name": "Pillow", "version": "10.0.0", "latest_version": "12.1.0"}]""",
            ReportFor = _ => Install(("Pillow", "12.1.0", true)),
        };
        var items = await pip.Scan().ScanAsync(Py, default);
        Assert.Equal(CheckStatus.UpdateVerfuegbar, items.Single(i => i.Name == "Pillow").Status);
        Assert.NotNull(items.Single(i => i.Name == PipScan.SummaryName).UpdateCommand);
    }
}
