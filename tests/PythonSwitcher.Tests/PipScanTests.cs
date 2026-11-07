using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

public class PipScanTests
{
    private static readonly Installation Py = new() { Tool = ToolKind.Python, Root = @"C:\Py312", Version = "3.12.7" };

    private static PipScan Fake(RunResult? list, RunResult? outdated) => new((_, args, _, _) =>
        Task.FromResult(args.Contains("--outdated") ? outdated : list));

    private const string List = """[{"name": "pip", "version": "24.2"}, {"name": "requests", "version": "2.31.0"}, {"name": "Pillow", "version": "10.0.0"}]""";
    private const string Outdated = """[{"name": "pip", "version": "24.2", "latest_version": "26.2.2", "latest_filetype": "wheel"}, {"name": "Pillow", "version": "10.0.0", "latest_version": "12.1.0", "latest_filetype": "wheel"}]""";

    [Fact]
    public async Task Outdated_packages_are_listed_with_old_and_new_version()
    {
        var items = await Fake(new(0, List, ""), new(0, Outdated, "")).ScanAsync(Py, default);

        var summary = items.Single(i => i.Name == PipScan.SummaryName);
        Assert.Equal(("3 installed", CheckStatus.UpdateVerfuegbar), (summary.Installed, summary.Status));
        Assert.Contains("2 of 3 packages are outdated, 1 up to date", summary.Hint);
        Assert.Equal(@"""C:\Py312\python.exe"" -m pip install --upgrade Pillow pip", summary.UpdateCommand);

        var pillow = items.Single(i => i.Name == "Pillow");
        Assert.Equal(("10.0.0", "12.1.0"), (pillow.Installed, pillow.Latest));
        Assert.Contains(@"""C:\Py312\python.exe"" -m pip install --upgrade Pillow", pillow.Hint);
        Assert.All(items, i => Assert.Equal("pip packages · Python 3.12.7", i.Group));
    }

    [Fact]
    public async Task Up_to_date_packages_are_listed_too()
    {
        var items = await Fake(new(0, List, ""), new(0, Outdated, "")).ScanAsync(Py, default);
        // Übersicht + alle 3 Pakete; Summe der Zeilen passt zur Zahl in der Übersicht
        Assert.Equal(["Summary", "Pillow", "pip", "requests"], items.Select(i => i.Name)); // alphabetisch, Groß/klein egal
        var requests = items.Single(i => i.Name == "requests");
        Assert.Equal(("2.31.0", "2.31.0", CheckStatus.Aktuell), (requests.Installed, requests.Latest, requests.Status));
        Assert.Null(requests.UpdateCommand);
        Assert.Equal(TargetKind.PipPackage, requests.Target?.Kind); // auch einzeln neu prüfbar
    }

    [Fact]
    public async Task All_current_lists_every_package_as_up_to_date()
    {
        var items = await Fake(new(0, List, ""), new(0, "[]", "")).ScanAsync(Py, default);
        Assert.Equal(4, items.Count);
        Assert.All(items, i => Assert.Equal(CheckStatus.Aktuell, i.Status));
        Assert.Equal("All 3 packages are up to date.", items[0].Hint);
    }

    [Fact]
    public async Task Missing_pip_gives_understandable_hint()
    {
        var items = await Fake(new(1, "", "C:\\Py312\\python.exe: No module named pip"), null).ScanAsync(Py, default);
        Assert.Contains("ensurepip", items.Single().Hint);
    }

    [Fact]
    public async Task Offline_gives_hint_instead_of_error()
    {
        var offline = new RunResult(0, "[]", "WARNING: Retrying (Retry(total=4...)) after connection broken by 'NewConnectionError'");
        var item = (await Fake(new(0, List, ""), offline).ScanAsync(Py, default)).Single();
        Assert.Equal(CheckStatus.Unbekannt, item.Status);
        Assert.Contains("3 packages", item.Hint);
        Assert.Contains("no connection", item.Hint);
    }

    [Fact]
    public async Task Python_that_does_not_start_gives_hint()
    {
        var item = (await Fake(null, null).ScanAsync(Py, default)).Single();
        Assert.Equal(CheckStatus.Unbekannt, item.Status);
    }

    [Fact]
    public void Leading_warning_text_before_json_is_ignored()
    {
        var list = PipScan.ParseList("WARNUNG irgendwas\n" + List);
        Assert.Equal(3, list.Count);
    }
}
