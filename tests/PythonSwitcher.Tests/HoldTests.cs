using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

/// <summary>Festhalten von Einträgen: pip-Pakete, Python/Git-Installationen und Werkzeuge.</summary>
public class HoldTests
{
    private const string Py310 = @"C:\Py310";
    private static readonly Installation Py = new() { Tool = ToolKind.Python, Root = Py310, Version = "3.10.11" };

    private static HeldPackage PipHold(string pkg, string version, string? reason = null) => new()
    {
        Kind = HeldPackage.KindPip, Root = Py310, Package = pkg, Version = version, Reason = reason, Since = new DateTime(2026, 11, 8),
    };

    private static PipScan Fake(string list, string outdated, List<HeldPackage> holds, Func<IReadOnlyList<string>, string>? report = null)
    {
        var reports = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        return new PipScan(
            (_, args, _, _) =>
            {
                if (args.Contains("--dry-run"))
                {
                    var l = args.ToList();
                    int ir = l.IndexOf("--report");
                    if (ir >= 0 && report is not null)
                    {
                        int iu = l.IndexOf("--upgrade"), idr = l.IndexOf("--dry-run");
                        reports[l[ir + 1]] = report(l.Skip(iu + 1).Take(idr - iu - 1).ToList());
                    }
                    return Task.FromResult<RunResult?>(new(0, "", ""));
                }
                return Task.FromResult<RunResult?>(new(0,
                    args.Contains("-c") ? """{"breaks": [], "ranges": {}}"""
                    : args.Contains("check") ? "No broken requirements found."
                    : args.Contains("--outdated") ? outdated : list, ""));
            },
            f => reports.TryGetValue(f, out var j) ? j : null,
            () => holds);
    }

    private const string List = """[{"name": "numpy", "version": "1.26.4"}, {"name": "torchdata", "version": "0.8.0"}, {"name": "Pillow", "version": "10.0.0"}, {"name": "requests", "version": "2.31.0"}]""";
    private const string Outdated = """[{"name": "numpy", "version": "1.26.4", "latest_version": "2.2.6"}, {"name": "torchdata", "version": "0.8.0", "latest_version": "0.11.0"}, {"name": "Pillow", "version": "10.0.0", "latest_version": "12.1.0"}]""";

    [Fact]
    public async Task Held_packages_are_not_offered_and_left_out_of_the_summary()
    {
        var holds = new List<HeldPackage> { PipHold("numpy", "1.26.4", "torch 2.3 needs numpy 1.x"), PipHold("torchdata", "0.8.0") };
        var items = await Fake(List, Outdated, holds).ScanAsync(Py, default);

        var numpy = items.Single(i => i.Name == "numpy");
        Assert.Equal(CheckStatus.Hinweis, numpy.Status);
        Assert.Null(numpy.UpdateCommand);
        Assert.StartsWith("Held by you at 1.26.4 since 2026-11-08: torch 2.3 needs numpy 1.x.", numpy.Hint);

        var summary = items.Single(i => i.Name == PipScan.SummaryName);
        Assert.Equal(@"C:\Py310\python.exe -m pip install --upgrade Pillow", summary.UpdateCommand);
        Assert.Contains("2 held by you", summary.Hint);
    }

    [Fact]
    public async Task Held_up_to_date_package_gets_a_note()
    {
        var items = await Fake(List, Outdated, [PipHold("requests", "2.31.0")]).ScanAsync(Py, default);
        var requests = items.Single(i => i.Name == "requests");
        Assert.Equal(CheckStatus.Aktuell, requests.Status);
        Assert.StartsWith("Held by you at 2.31.0", requests.Note);
    }

    [Fact]
    public async Task Update_that_would_change_a_held_package_is_blocked()
    {
        // Pillow-Update zieht numpy 2 nach – numpy ist festgehalten
        var items = await Fake(List, Outdated, [PipHold("numpy", "1.26.4")],
            report: pkgs => """{"install": [{"metadata": {"name": "Pillow", "version": "12.1.0"}, "requested": true}, {"metadata": {"name": "numpy", "version": "2.2.6"}, "requested": false}]}""")
            .ScanAsync(Py, default);
        var pillow = items.Single(i => i.Name == "Pillow");
        Assert.Equal(CheckStatus.Hinweis, pillow.Status);
        Assert.Contains("it would change numpy 1.26.4 → 2.2.6, which you hold", pillow.Hint);
    }

    [Fact]
    public async Task Recheck_respects_the_hold()
    {
        var row = new CheckItem(CheckCategory.PipPaket, "g", "numpy", "1.26.4", "2.2.6", CheckStatus.UpdateVerfuegbar, "", null, Py310,
            "x", new CheckTarget(TargetKind.PipPackage, ToolKind.Python, Py310, Package: "numpy"));
        var again = await Fake(List, Outdated, [PipHold("numpy", "1.26.4")]).RecheckPackageAsync(row, Py, default);
        Assert.Equal(CheckStatus.Hinweis, again.Status);
        Assert.Null(again.UpdateCommand);
    }

    [Fact]
    public void Holds_apply_to_runtimes_and_tools_too()
    {
        var git = new CheckItem(CheckCategory.Git, "Git", "Git", "2.53.0.windows.2", "2.56.0.4", CheckStatus.UpdateVerfuegbar, "Update: …",
            null, @"C:\Program Files\Git", "winget upgrade --id Git.Git", new CheckTarget(TargetKind.Runtime, ToolKind.Git, @"C:\Program Files\Git"));
        var npp = new CheckItem(CheckCategory.Editor, "Editors and IDEs", "Notepad++", "8.9.9", "8.9.9", CheckStatus.Aktuell, "",
            null, "Notepad++ (64-bit x64)", null, new CheckTarget(TargetKind.Tool, CatalogName: "Notepad++"));
        var holds = new List<HeldPackage>
        {
            new() { Kind = HeldPackage.KindRuntime, Root = @"C:\Program Files\Git", Package = "Git", Version = "2.53.0.windows.2" },
            new() { Kind = HeldPackage.KindTool, Root = "Notepad++ (64-bit x64)", Package = "Notepad++", Version = "8.9.9" },
        };

        var heldGit = HoldRules.Apply(git, holds);
        Assert.Equal(CheckStatus.Hinweis, heldGit.Status);
        Assert.Null(heldGit.UpdateCommand);
        Assert.StartsWith("Held by you at 2.53.0.windows.2", heldGit.Hint);

        var heldNpp = HoldRules.Apply(npp, holds);
        Assert.Equal(CheckStatus.Aktuell, heldNpp.Status);
        Assert.StartsWith("Held by you at 8.9.9", heldNpp.Note);
    }

    [Fact]
    public void Rows_without_an_installed_thing_cannot_be_held()
    {
        var summary = new CheckItem(CheckCategory.PipPaket, "g", PipScan.SummaryName, "3 installed", null, CheckStatus.Aktuell, "",
            null, Py310, null, new CheckTarget(TargetKind.PipSummary, ToolKind.Python, Py310));
        var majorHint = new CheckItem(CheckCategory.Python, "Python", "Python 3.15", null, "3.15.2", CheckStatus.Hinweis, "");
        Assert.Null(HoldRules.KeyFor(summary));
        Assert.Null(HoldRules.KeyFor(majorHint));
    }

    [Fact]
    public void Hold_and_release_are_saved()
    {
        using var tmp = new TempDir();
        var store = new FakePathStore();
        var sw = new Switcher(new DataFolders(tmp.Path), store, new FakeSystemWriter(store, true));
        sw.Hold(HeldPackage.KindPip, Py310, "NumPy", "1.26.4", "  torch needs it  ");
        var loaded = new SettingsStore(sw.Folders).Load();
        var h = Assert.Single(loaded.Holds);
        Assert.Equal(("NumPy", "torch needs it"), (h.Package, h.Reason));
        Assert.NotNull(loaded.FindHold(HeldPackage.KindPip, Py310 + @"\", "numpy")); // Schreibweise egal

        Assert.True(sw.Release(HeldPackage.KindPip, Py310, "numpy"));
        Assert.Empty(new SettingsStore(sw.Folders).Load().Holds);
        Assert.False(sw.Release(HeldPackage.KindPip, Py310, "numpy"));
    }
}
