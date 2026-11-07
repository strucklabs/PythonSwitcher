using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

public class PipScanTests
{
    private static readonly Installation Py = new() { Tool = ToolKind.Python, Root = @"C:\Py312", Version = "3.12.7" };

    private static readonly RunResult CheckOk = new(0, "No broken requirements found.", "");

    /// <summary>Simuliertes pip: list, list --outdated, check und install --dry-run.</summary>
    private static PipScan Fake(RunResult? list, RunResult? outdated, RunResult? check = null, RunResult? dryRun = null) =>
        new((_, args, _, _) => Task.FromResult(
            args.Contains("check") ? check ?? CheckOk
            : args.Contains("--dry-run") ? dryRun
            : args.Contains("--outdated") ? outdated
            : list));

    private const string List = """[{"name": "pip", "version": "24.2"}, {"name": "requests", "version": "2.31.0"}, {"name": "Pillow", "version": "10.0.0"}]""";
    private const string Outdated = """[{"name": "pip", "version": "24.2", "latest_version": "26.2.2", "latest_filetype": "wheel"}, {"name": "Pillow", "version": "10.0.0", "latest_version": "12.1.0", "latest_filetype": "wheel"}]""";

    [Fact]
    public async Task Outdated_packages_are_listed_with_old_and_new_version()
    {
        var items = await Fake(new(0, List, ""), new(0, Outdated, "")).ScanAsync(Py, default);

        var summary = items.Single(i => i.Name == PipScan.SummaryName);
        Assert.Equal(("3 installed", CheckStatus.UpdateVerfuegbar), (summary.Installed, summary.Status));
        Assert.Contains("2 of 3 packages are outdated, 1 up to date", summary.Hint);
        Assert.Equal(@"C:\Py312\python.exe -m pip install --upgrade Pillow pip", summary.UpdateCommand);

        var pillow = items.Single(i => i.Name == "Pillow");
        Assert.Equal(("10.0.0", "12.1.0"), (pillow.Installed, pillow.Latest));
        Assert.Contains(@"C:\Py312\python.exe -m pip install --upgrade Pillow", pillow.Hint);
        Assert.All(items, i => Assert.Equal("pip packages · Python 3.12.7", i.Group));
    }

    [Theory]
    [InlineData(@"C:\Py312\python.exe", @"C:\Py312\python.exe -m pip install --upgrade a b")]
    [InlineData(@"C:\Program Files\Python\python.exe", @"& ""C:\Program Files\Python\python.exe"" -m pip install --upgrade a b")]
    public void Commands_work_in_powershell(string python, string expected)
        => Assert.Equal(expected, PipScan.UpgradeCommand(python, ["a", "b"]));

    private static PipScan Managed(RunResult list, RunResult outdated) => new(
        (_, args, _, _) => Task.FromResult<RunResult?>(args.Contains("check") ? CheckOk : args.Contains("--outdated") ? outdated : list),
        f => f.EndsWith(@"Lib\EXTERNALLY-MANAGED")
            ? "[externally-managed]\nError=This Python installation is managed by uv and should not be modified.\n"
            : null);

    [Fact]
    public async Task Externally_managed_python_gets_no_pip_commands()
    {
        var items = await Managed(new(0, List, ""), new(0, Outdated, "")).ScanAsync(Py, default);
        Assert.All(items, i => Assert.Null(i.UpdateCommand));
        Assert.DoesNotContain(items, i => i.Status == CheckStatus.UpdateVerfuegbar); // zählt nicht als Update
        var pillow = items.Single(i => i.Name == "Pillow");
        Assert.Equal(("12.1.0", CheckStatus.Hinweis), (pillow.Latest, pillow.Status));
        Assert.Contains("managed by uv and should not be modified", pillow.Hint);
        Assert.Contains("2 of 3 packages have newer versions", items[0].Hint);
    }

    [Fact]
    public async Task Recheck_in_externally_managed_python_gives_no_command()
    {
        var scan = Managed(new(0, List, ""), new(0, Outdated, ""));
        var pillow = (await scan.ScanAsync(Py, default)).Single(i => i.Name == "Pillow");
        var again = await scan.RecheckPackageAsync(pillow, Py, default);
        Assert.Equal(CheckStatus.Hinweis, again.Status);
        Assert.Null(again.UpdateCommand);
    }

    [Fact]
    public async Task Up_to_date_packages_are_listed_too()
    {
        var items = await Fake(new(0, List, ""), new(0, Outdated, "")).ScanAsync(Py, default);
        // Übersicht, Abhängigkeitsprüfung, dann alle 3 Pakete alphabetisch (Groß/klein egal)
        Assert.Equal(["Summary", "Dependencies", "Pillow", "pip", "requests"], items.Select(i => i.Name));
        var requests = items.Single(i => i.Name == "requests");
        Assert.Equal(("2.31.0", "2.31.0", CheckStatus.Aktuell), (requests.Installed, requests.Latest, requests.Status));
        Assert.Null(requests.UpdateCommand);
        Assert.Equal(TargetKind.PipPackage, requests.Target?.Kind); // auch einzeln neu prüfbar
    }

    [Fact]
    public async Task All_current_lists_every_package_as_up_to_date()
    {
        var items = await Fake(new(0, List, ""), new(0, "[]", "")).ScanAsync(Py, default);
        Assert.Equal(5, items.Count);
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
        var items = await Fake(new(0, List, ""), offline).ScanAsync(Py, default);
        var item = items.First();
        Assert.Equal(CheckStatus.Unbekannt, item.Status);
        Assert.Contains("3 packages", item.Hint);
        Assert.Contains("no connection", item.Hint);
        // Die Abhängigkeitsprüfung braucht kein Netz und läuft trotzdem
        Assert.Equal(PipScan.DependenciesName, items[1].Name);
    }

    // ---------- Installierbarkeit (Trockenlauf) ----------

    private const string TorchvideoDryRun = """
        Requirement already satisfied: torchvideo in c:\py310\lib\site-packages (0.0.0)
        Collecting torchvideo
          Using cached torchvideo-0.0.1.tar.gz (48 kB)
        Could not fetch URL https://pypi.org/simple/gulpio/: 404 Client Error: Not Found for url: https://pypi.org/simple/gulpio/ - skipping
        Will try a different candidate, due to conflict:
        torchvideo 0.0.1 depends on gulpio>=540.66
        """;

    [Fact]
    public void Dry_run_explains_why_an_update_cannot_be_installed()
    {
        var why = PipScan.ExplainDryRun(TorchvideoDryRun, "torchvideo", "0.0.0", "0.0.1");
        Assert.NotNull(why);
        Assert.Contains("torchvideo 0.0.1 depends on gulpio>=540.66", why);
        Assert.Contains("gulpio is not available on PyPI", why);
        Assert.Contains("keeps 0.0.0", why);
    }

    [Theory]
    [InlineData("Would install Pillow-12.1.0")]
    [InlineData("Would install numpy-2.0.0 pillow-12.1.0 setuptools-80.0")]
    public void Dry_run_that_installs_the_new_version_is_fine(string output)
        => Assert.Null(PipScan.ExplainDryRun(output, "Pillow", "10.0.0", "12.1.0"));

    [Fact]
    public async Task Recheck_of_package_that_cannot_be_installed_shows_the_reason()
    {
        var list = """[{"name": "torchvideo", "version": "0.0.0"}]""";
        var scan = Fake(new(0, list, ""), new(0, """[{"name": "torchvideo", "version": "0.0.0", "latest_version": "0.0.1"}]""", ""),
            dryRun: new(0, TorchvideoDryRun, ""));
        var row = (await scan.ScanAsync(Py, default)).Single(i => i.Name == "torchvideo");
        Assert.Equal(CheckStatus.UpdateVerfuegbar, row.Status);

        var again = await scan.RecheckPackageAsync(row, Py, default);
        Assert.Equal(CheckStatus.Hinweis, again.Status);   // zählt nicht mehr als Update
        Assert.Null(again.UpdateCommand);
        Assert.Contains("gulpio", again.Hint);
    }

    [Fact]
    public void Reason_is_only_taken_from_conflicts_concerning_the_package()
    {
        // mpmath bleibt alt, weil sympy es begrenzt; der torchvideo/gulpio-Konflikt gehört nicht dazu
        var log = TorchvideoDryRun + "\nsympy 1.13.1 depends on mpmath<1.4,>=1.1.0\n";
        var why = PipScan.ExplainDryRun(log, "mpmath", "1.3.0", "1.4.1");
        Assert.Contains("sympy 1.13.1 depends on mpmath<1.4", why);
        Assert.DoesNotContain("gulpio", why);

        var noReason = PipScan.ExplainDryRun("Collecting x", "mpmath", "1.3.0", "1.4.1");
        Assert.Contains("another installed package requires an older version", noReason);
    }

    // ---------- Spezial-Builds (z. B. CUDA) ----------

    [Fact]
    public async Task Special_cuda_build_gets_a_warning_instead_of_an_update_command()
    {
        var list = """[{"name": "torch", "version": "2.3.0+cu121"}, {"name": "Pillow", "version": "10.0.0"}]""";
        var outdated = """[{"name": "torch", "version": "2.3.0+cu121", "latest_version": "2.14.1"}, {"name": "Pillow", "version": "10.0.0", "latest_version": "12.1.0"}]""";
        var items = await Fake(new(0, list, ""), new(0, outdated, "")).ScanAsync(Py, default);
        var torch = items.Single(i => i.Name == "torch");
        Assert.Equal(CheckStatus.Hinweis, torch.Status);
        Assert.Null(torch.UpdateCommand);
        Assert.Contains("+cu121", torch.Hint);
        Assert.Contains("GPU", torch.Hint);
        // Der Sammelbefehl der Übersicht enthält torch nicht
        var summary = items.Single(i => i.Name == PipScan.SummaryName);
        Assert.Equal(@"C:\Py312\python.exe -m pip install --upgrade Pillow", summary.UpdateCommand);
        Assert.Contains("1 need attention", summary.Hint);
    }

    [Theory]
    [InlineData("2.3.0+cu121", "2.14.1", true)]
    [InlineData("2.3.0+cu121", "2.4.0+cu121", false)]
    [InlineData("2.3.0", "2.14.1", false)]
    public void Special_build_is_detected(string installed, string latest, bool expected)
        => Assert.Equal(expected, PipScan.SpecialBuildNote(installed, latest) is not null);

    // ---------- Übersicht nach Einzelprüfung ----------

    [Fact]
    public async Task Summary_is_rebuilt_from_the_current_rows()
    {
        var items = await Fake(new(0, List, ""), new(0, Outdated, "")).ScanAsync(Py, default);
        // Pillow wurde inzwischen aktualisiert, pip lässt sich nicht installieren
        var rows = items.Select(i => i.Name switch
        {
            "Pillow" => i with { Installed = "12.1.0", Status = CheckStatus.Aktuell, UpdateCommand = null },
            "pip" => i with { Status = CheckStatus.Hinweis, UpdateCommand = null },
            _ => i,
        }).ToList();
        var summary = PipScan.UpdateSummary(rows[0], rows);
        Assert.Equal("0 of 3 packages are outdated, 2 up to date, 1 need attention (see their rows).", summary.Hint);
        Assert.Equal(CheckStatus.Hinweis, summary.Status);
        Assert.Null(summary.UpdateCommand);

        var onlyPillowDone = items.Select(i => i.Name == "Pillow" ? i with { Installed = "12.1.0", Status = CheckStatus.Aktuell } : i).ToList();
        var s2 = PipScan.UpdateSummary(onlyPillowDone[0], onlyPillowDone);
        Assert.Equal(CheckStatus.UpdateVerfuegbar, s2.Status);
        Assert.Equal(@"C:\Py312\python.exe -m pip install --upgrade pip", s2.UpdateCommand);
        Assert.StartsWith("1 of 3 packages are outdated, 2 up to date.", s2.Hint);
    }

    // ---------- Abhängigkeiten (pip check) ----------

    [Fact]
    public async Task Pip_check_problems_become_conflict_rows()
    {
        var check = new RunResult(1,
            "torchvision 0.18.0+cu121 has requirement torch==2.3.0, but you have torch 2.5.1.\n" +
            "xformers 0.0.26 requires triton, which is not installed.\n", "");
        var items = await Fake(new(0, List, ""), new(0, "[]", ""), check).ScanAsync(Py, default);
        var deps = items.Single(i => i.Name == PipScan.DependenciesName);
        Assert.Equal(("2 problem(s)", CheckStatus.Problem), (deps.Installed, deps.Status));
        var tv = items.Single(i => i.Name == "torchvision");
        Assert.Equal(("0.18.0+cu121", CheckStatus.Problem), (tv.Installed, tv.Status));
        Assert.Contains("has requirement torch==2.3.0", tv.Hint);
        Assert.Null(tv.UpdateCommand); // bewusst kein Reparatur-Befehl
        Assert.All(items.Where(i => i.Status == CheckStatus.Problem), i => Assert.Equal(TargetKind.PipCheck, i.Target?.Kind));
    }

    [Fact]
    public async Task Clean_pip_check_gives_one_ok_row()
    {
        var items = await Fake(new(0, List, ""), new(0, "[]", "")).ScanAsync(Py, default);
        var deps = items.Single(i => i.Name == PipScan.DependenciesName);
        Assert.Equal(("OK", CheckStatus.Aktuell), (deps.Installed, deps.Status));
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
