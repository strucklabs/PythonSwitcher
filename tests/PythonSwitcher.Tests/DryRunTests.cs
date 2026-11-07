using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

/// <summary>Trockenlauf: Auswertung des pip-Berichts (--report) – ohne echtes pip.</summary>
public class DryRunTests
{
    private static readonly Dictionary<string, string> Installed = new()
    {
        ["pillow"] = "10.0.0", ["numpy"] = "1.26.4", ["torch"] = "2.5.1", ["torchvideo"] = "0.0.0",
    };

    private const string Report = """
        {"version": "1", "install": [
          {"metadata": {"name": "Pillow", "version": "12.1.0"}, "requested": true},
          {"metadata": {"name": "numpy", "version": "2.1.0"}, "requested": false},
          {"metadata": {"name": "torch", "version": "2.3.0"}, "requested": false},
          {"metadata": {"name": "tqdm", "version": "4.67.0"}, "requested": false}
        ]}
        """;

    [Fact]
    public void Report_shows_upgrades_downgrades_and_new_dependencies()
    {
        var r = PipScan.EvaluateDryRun(Report, "", Installed, new Dictionary<string, string?> { ["Pillow"] = "12.1.0" });
        Assert.Null(r.Error);
        Assert.Empty(r.NotInstallable);
        var byName = r.Changes.ToDictionary(c => c.Package);
        Assert.Equal((PipScan.ChangeKind.Upgrade, true), (byName["Pillow"].Kind, byName["Pillow"].Requested));
        Assert.Equal((PipScan.ChangeKind.Upgrade, false), (byName["numpy"].Kind, byName["numpy"].Requested));
        Assert.Equal(("2.5.1", PipScan.ChangeKind.Downgrade), (byName["torch"].From, byName["torch"].Kind));
        Assert.Equal(PipScan.ChangeKind.New, byName["tqdm"].Kind);

        var text = VersionCheckText(r);
        Assert.Contains("torch: 2.5.1 → 2.3.0   (DOWNGRADE, changed as a dependency)", text);
        Assert.Contains("Nothing was installed", text);
    }

    [Fact]
    public void Replacing_a_cuda_build_is_flagged()
    {
        var installed = new Dictionary<string, string> { ["torch"] = "2.3.0+cu121" };
        var report = """{"install": [{"metadata": {"name": "torch", "version": "2.14.1"}, "requested": true}]}""";
        var text = PipScan.DescribeDryRun(PipScan.EvaluateDryRun(report, "", installed, new Dictionary<string, string?> { ["torch"] = "2.14.1" }));
        Assert.Contains("BUILD CHANGES: +cu121", text);
        Assert.Contains("can remove GPU support", text);
    }

    [Fact]
    public void Wanted_package_missing_from_report_is_explained()
    {
        const string log = "torchvideo 0.0.1 depends on gulpio>=540.66\nCould not fetch URL https://pypi.org/simple/gulpio/: 404 Client Error";
        var r = PipScan.EvaluateDryRun("""{"install": []}""", log, Installed, new Dictionary<string, string?> { ["torchvideo"] = "0.0.1" });
        Assert.Empty(r.Changes);
        Assert.Contains("gulpio", r.NotInstallable["torchvideo"]);
        Assert.Contains("Cannot be updated", VersionCheckText(r));
    }

    [Fact]
    public void Package_already_at_the_latest_version_is_not_reported_as_blocked()
    {
        var installed = new Dictionary<string, string> { ["pip"] = "26.3" };
        var r = PipScan.EvaluateDryRun("""{"install": []}""", "", installed, new Dictionary<string, string?> { ["pip"] = "26.3" });
        Assert.Empty(r.NotInstallable);
        Assert.Contains("No changes", PipScan.DescribeDryRun(r));
    }

    [Fact]
    public async Task Dry_run_uses_report_file_and_deletes_nothing_else()
    {
        string? reportPath = null;
        var pip = new PipScan(
            (_, args, _, _) =>
            {
                var i = args.ToList().IndexOf("--report");
                if (i >= 0) reportPath = args[i + 1];
                return Task.FromResult<RunResult?>(new(0, args.Contains("list") ? """[{"name": "Pillow", "version": "10.0.0"}]""" : "", ""));
            },
            f => f == reportPath ? """{"install": [{"metadata": {"name": "Pillow", "version": "12.1.0"}, "requested": true}]}""" : null);
        var inst = new Installation { Tool = ToolKind.Python, Root = @"C:\Py312", Version = "3.12.7" };
        var r = await pip.DryRunAsync(inst, new Dictionary<string, string?> { ["Pillow"] = "12.1.0" }, default);
        Assert.NotNull(reportPath);
        Assert.Single(r.Changes);
        Assert.Empty(r.NotInstallable);
    }

    /// <summary>Text, wie ihn die Oberfläche anzeigt (gleiche Logik, hier nachgebildet über die Kernfunktion).</summary>
    private static string VersionCheckText(PipScan.DryRunResult r) => PipScan.DescribeDryRun(r);
}
