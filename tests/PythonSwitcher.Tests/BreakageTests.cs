using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

/// <summary>Vorab-Prüfung: würde ein Update installierte Pakete brechen, und welcher Bereich passt?</summary>
public class BreakageTests
{
    private static readonly Installation Py = new() { Tool = ToolKind.Python, Root = @"C:\Py310", Version = "3.10.11" };

    /// <summary>Ausgabe des Prüfskripts für den Fall mpmath 1.4.1 / sympy 1.14.0.</summary>
    private const string MpmathBreaks = """
        {"breaks": [{"requirer": "sympy", "version": "1.14.0", "requirement": "mpmath<1.4,>=1.1.0", "package": "mpmath", "new": "1.4.1"}],
         "ranges": {"mpmath": {"spec": "<1.4,>=1.1.0", "best": "1.3.0"}}}
        """;

    private static string OneLine(string s) => s.Replace("\r", "").Replace("\n", " ");

    [Fact]
    public void Script_output_is_parsed()
    {
        var c = PipScan.ParseBreakages(OneLine(MpmathBreaks));
        var b = Assert.Single(c.Breaks);
        Assert.Equal("sympy 1.14.0 requires mpmath<1.4,>=1.1.0, but the update installs mpmath 1.4.1", b.ToString());
        var r = PipScan.RangeFor(c, "mpmath");
        Assert.Equal(("<1.4,>=1.1.0", "1.3.0"), (r?.Specifier, r?.Best));
    }

    [Theory]
    [InlineData("1.3.0", " Compatible range: <1.4,>=1.1.0 – newest fitting version 1.3.0 is already installed.")]
    [InlineData("1.2.0", " Compatible range: <1.4,>=1.1.0 – newest fitting version: 1.3.0.")]
    public void Range_text_says_whether_the_best_fitting_version_is_installed(string installed, string expected)
        => Assert.Equal(expected, PipScan.RangeText(new PipScan.CompatibleRange("mpmath", "<1.4,>=1.1.0", "1.3.0"), installed));

    [Fact]
    public void Range_command_updates_only_within_the_range()
    {
        var range = new PipScan.CompatibleRange("mpmath", "<1.4,>=1.1.0", "1.3.0");
        Assert.Equal(@"C:\Py310\python.exe -m pip install --upgrade ""mpmath<1.4,>=1.1.0""",
            PipScan.RangeCommand(@"C:\Py310\python.exe", range, "1.2.0"));
        Assert.Null(PipScan.RangeCommand(@"C:\Py310\python.exe", range, "1.3.0")); // bringt nichts
    }

    /// <summary>Simuliertes pip inklusive Prüfskript (Aufruf mit "-c").</summary>
    private static PipScan Fake(string list, string outdated, string breakOutput, string dryRun = "Would install mpmath-1.4.1") =>
        new((_, args, _, _) => Task.FromResult<RunResult?>(new(0,
            args.Contains("-c") ? breakOutput
            : args.Contains("check") ? "No broken requirements found."
            : args.Contains("--dry-run") ? dryRun
            : args.Contains("--outdated") ? outdated : list, "")));

    private const string List = """[{"name": "mpmath", "version": "1.3.0"}, {"name": "sympy", "version": "1.14.0"}, {"name": "Pillow", "version": "10.0.0"}]""";
    private const string Outdated = """[{"name": "mpmath", "version": "1.3.0", "latest_version": "1.4.1"}, {"name": "Pillow", "version": "10.0.0", "latest_version": "12.1.0"}]""";

    [Fact]
    public async Task Full_check_marks_breaking_update_and_leaves_it_out_of_the_summary_command()
    {
        var items = await Fake(List, Outdated, OneLine(MpmathBreaks)).ScanAsync(Py, default);
        var mpmath = items.Single(i => i.Name == "mpmath");
        Assert.Equal(CheckStatus.Hinweis, mpmath.Status);
        Assert.Contains("sympy 1.14.0 requires mpmath<1.4,>=1.1.0", mpmath.Hint);
        Assert.Contains("Compatible range: <1.4,>=1.1.0", mpmath.Hint);
        Assert.Contains("1.3.0 is already installed", mpmath.Hint);
        Assert.Null(mpmath.UpdateCommand);

        var summary = items.Single(i => i.Name == PipScan.SummaryName);
        Assert.Equal(@"C:\Py310\python.exe -m pip install --upgrade Pillow", summary.UpdateCommand);
    }

    [Fact]
    public async Task Recheck_of_breaking_update_explains_it()
    {
        var scan = Fake(List, Outdated, OneLine(MpmathBreaks));
        var row = new CheckItem(CheckCategory.PipPaket, "g", "mpmath", "1.3.0", "1.4.1", CheckStatus.UpdateVerfuegbar, "", null, Py.Root,
            "x", new CheckTarget(TargetKind.PipPackage, ToolKind.Python, Py.Root, Package: "mpmath"));
        var again = await scan.RecheckPackageAsync(row, Py, default);
        Assert.Equal(CheckStatus.Hinweis, again.Status);
        Assert.Contains("Would break installed packages: sympy 1.14.0", again.Hint);
    }

    [Fact]
    public async Task Held_back_package_names_the_limiting_package()
    {
        var scan = Fake(List, Outdated, OneLine(MpmathBreaks), dryRun: "Collecting mpmath\nRequirement already satisfied: mpmath");
        var why = await scan.WhyNotInstallableAsync(@"C:\Py310\python.exe", "mpmath", "1.3.0", "1.4.1", default);
        Assert.Contains("held back by installed packages – sympy 1.14.0 requires mpmath<1.4,>=1.1.0", why);
        Assert.DoesNotContain("did not name it", why);
    }

    [Fact]
    public void Dry_run_description_lists_breaks_with_range()
    {
        var check = PipScan.ParseBreakages(OneLine(MpmathBreaks));
        var r = new PipScan.DryRunResult(
            [new PipScan.PlannedChange("mpmath", "1.3.0", "1.4.1", PipScan.ChangeKind.Upgrade, true)],
            new Dictionary<string, string>(), null, check.Breaks, check.Ranges);
        var text = PipScan.DescribeDryRun(r);
        Assert.Contains("Would break installed packages", text);
        Assert.Contains("sympy 1.14.0 requires mpmath<1.4,>=1.1.0, but the update installs mpmath 1.4.1", text);
        Assert.Contains("mpmath: Compatible range: <1.4,>=1.1.0 – newest fitting version 1.3.0 is already installed.", text);
    }

    [Fact]
    public void Broken_script_output_is_ignored()
    {
        Assert.Empty(PipScan.ParseBreakages("Traceback (most recent call last): …").Breaks);
        Assert.Empty(PipScan.ParseBreakages("").Breaks);
    }
}
