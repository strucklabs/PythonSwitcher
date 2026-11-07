using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

/// <summary>Frisch ausgelesene Versionen, Einzelprüfung und Update-Befehle – ohne Netz und ohne echte Programme.</summary>
public class RecheckTests
{
    private const string GitRoot = @"C:\Program Files\Git";
    private const string Py312 = @"C:\Py312";

    private static AvailableRelease GitRel(string v, Version k) => new(ToolKind.Git, v, k, "u", "f", 1, "x", null);

    private static CheckContext Catalogs() => new()
    {
        PyInstallers = [new(3, 15, 2), new(3, 12, 10), new(3, 10, 11)],
        PyAll = [new(3, 15, 2), new(3, 12, 16), new(3, 10, 22)],
        GitReleases = [GitRel("2.56.0.4", new Version(2, 56, 0, 4))],
    };

    private static List<Installation> Installed() =>
    [
        new() { Tool = ToolKind.Git, Root = GitRoot, Version = "2.53.0.windows.2", Source = InstallSource.Gefunden },
        new() { Tool = ToolKind.Python, Root = Py312, Version = "3.12.7", Source = InstallSource.Gefunden },
    ];

    /// <summary>Baut einen Prüfer mit veränderbarem "Rechnerzustand".</summary>
    private sealed class Machine
    {
        public Dictionary<string, string?> Versions = new(StringComparer.OrdinalIgnoreCase)
            { [GitRoot] = "2.56.0.windows.4", [Py312] = "3.12.7" };
        public List<WingetPackage> Winget = [];
        public string PipList = """[{"name": "Pillow", "version": "10.0.0"}]""";
        public string PipOutdated = """[{"name": "Pillow", "version": "10.0.0", "latest_version": "12.1.0"}]""";

        public VersionChecker Checker() => new(new FakePathStore(),
            winget: _ => Task.FromResult<List<WingetPackage>?>(Winget.ToList()),
            probe: (_, root) => Versions.GetValueOrDefault(root),
            pip: new PipScan((_, args, _, _) => Task.FromResult<RunResult?>(new(0, args.Contains("--outdated") ? PipOutdated : PipList, ""))),
            toolScan: roots => new ToolScan(_ => null, (_, _) => null, (_, _) => Task.FromResult<string?>(null), roots),
            catalogs: _ => Task.FromResult(Catalogs()),
            fileExists: _ => true);
    }

    private static Task<List<CheckItem>> Run(VersionChecker c) => c.RunAsync(Installed(), new Progress<string>(), default);

    [Fact]
    public async Task Full_check_reads_versions_fresh_instead_of_using_the_stored_one()
    {
        // Gespeichert ist noch 2.53, tatsächlich installiert (z. B. per winget aktualisiert) ist 2.56
        var items = await Run(new Machine().Checker());
        var git = items.Single(i => i.Category == CheckCategory.Git);
        Assert.Equal(("2.56.0.windows.4", CheckStatus.Aktuell), (git.Installed, git.Status));
    }

    [Fact]
    public async Task Recheck_of_python_after_update_shows_up_to_date()
    {
        var m = new Machine();
        var checker = m.Checker();
        var py = (await Run(checker)).Single(i => i.Name == "Python 3.12");
        Assert.Equal(CheckStatus.UpdateVerfuegbar, py.Status);

        m.Versions[Py312] = "3.12.10"; // Update von außen
        var after = (await checker.RecheckAsync(py, Installed(), default)).Single();
        Assert.Equal(("3.12.10", CheckStatus.Aktuell), (after.Installed, after.Status));
        Assert.Null(after.UpdateCommand);
    }

    [Fact]
    public async Task Recheck_of_uninstalled_runtime_says_so()
    {
        var m = new Machine();
        var checker = m.Checker();
        var git = (await Run(checker)).Single(i => i.Category == CheckCategory.Git);
        m.Versions[GitRoot] = null;
        var after = (await checker.RecheckAsync(git, Installed(), default)).Single();
        Assert.Equal(CheckStatus.Unbekannt, after.Status);
        Assert.Contains("could not be started", after.Hint);
    }

    [Fact]
    public async Task Recheck_of_pip_package_after_update_and_after_removal()
    {
        var m = new Machine();
        var checker = m.Checker();
        var pillow = (await Run(checker)).Single(i => i.Name == "Pillow");
        Assert.Equal(CheckStatus.UpdateVerfuegbar, pillow.Status);

        m.PipList = """[{"name": "pillow", "version": "12.1.0"}]"""; // andere Schreibweise wie bei pip üblich
        var updated = (await checker.RecheckAsync(pillow, Installed(), default)).Single();
        Assert.Equal(("12.1.0", CheckStatus.Aktuell), (updated.Installed, updated.Status));

        m.PipList = "[]";
        var removed = (await checker.RecheckAsync(pillow, Installed(), default)).Single();
        Assert.Equal(CheckStatus.Hinweis, removed.Status);
        Assert.Contains("no longer installed", removed.Hint);
    }

    [Fact]
    public async Task Recheck_of_pip_summary_scans_all_packages_again()
    {
        var m = new Machine();
        var checker = m.Checker();
        var summary = (await Run(checker)).Single(i => i.Name == PipScan.SummaryName);
        m.PipOutdated = "[]";
        var rows = await checker.RecheckAsync(summary, Installed(), default);
        Assert.All(rows, r => Assert.Equal(CheckStatus.Aktuell, r.Status));
        Assert.Equal([PipScan.SummaryName, "Pillow"], rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Recheck_of_tool_uses_fresh_winget_list()
    {
        var m = new Machine
        {
            Winget = [new WingetPackage("Notepad++ (64-bit x64)", "Notepad++.Notepad++", "8.7.5", "8.9.9", "winget", false)],
        };
        var checker = m.Checker();
        var npp = (await Run(checker)).Single(i => i.Name == "Notepad++");
        Assert.Equal("winget upgrade --id Notepad++.Notepad++", npp.UpdateCommand);

        m.Winget = [new WingetPackage("Notepad++ (64-bit x64)", "Notepad++.Notepad++", "8.9.9", null, "winget", false)];
        var updated = (await checker.RecheckAsync(npp, Installed(), default)).Single();
        Assert.Equal(("8.9.9", CheckStatus.Aktuell), (updated.Installed, updated.Status));

        m.Winget = [];
        var gone = (await checker.RecheckAsync(npp, Installed(), default)).Single();
        Assert.Contains("No longer found", gone.Hint);
    }

    [Fact]
    public async Task Update_commands_are_plain_commands()
    {
        var m = new Machine
        {
            Winget = [new WingetPackage("Python 3.12.7 (64-bit)", "Python.Python.3.12", "3.12.7", "3.12.10", "winget", false)],
        };
        var items = await Run(m.Checker());
        Assert.Equal("winget upgrade --id Python.Python.3.12", items.Single(i => i.Name == "Python 3.12").UpdateCommand);
        Assert.Equal(@"C:\Py312\python.exe -m pip install --upgrade Pillow", items.Single(i => i.Name == "Pillow").UpdateCommand);
        Assert.Equal(@"C:\Py312\python.exe -m pip install --upgrade Pillow", items.Single(i => i.Name == PipScan.SummaryName).UpdateCommand);
        Assert.Null(items.Single(i => i.Category == CheckCategory.Git).UpdateCommand); // aktuell → kein Befehl
        // Hinweis auf neuere Hauptversion: weder Befehl noch Einzelprüfung
        var hint = items.Single(i => i.Name == "Python 3.15");
        Assert.Null(hint.UpdateCommand);
        Assert.Null(hint.Target);
    }

    [Fact]
    public async Task Entry_without_target_cannot_be_rechecked()
    {
        var item = new CheckItem(CheckCategory.Python, "Python", "x", null, null, CheckStatus.Hinweis, "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Machine().Checker().RecheckAsync(item, Installed(), default));
    }

    [Fact]
    public void Fresh_version_is_stored_in_the_list()
    {
        using var tmp = new TempDir();
        var store = new FakePathStore();
        var sw = new Switcher(new DataFolders(tmp.Path), store, new FakeSystemWriter(store, true));
        sw.AddDownloaded(new Installation { Tool = ToolKind.Git, Root = GitRoot, Version = "2.53.0.windows.2" });
        Assert.True(sw.UpdateVersion(ToolKind.Git, GitRoot, "2.56.0.windows.4"));
        Assert.False(sw.UpdateVersion(ToolKind.Git, GitRoot, "2.56.0.windows.4"));
        Assert.Equal("2.56.0.windows.4", new SettingsStore(sw.Folders).Load().Installations.Single().Version);
    }
}
