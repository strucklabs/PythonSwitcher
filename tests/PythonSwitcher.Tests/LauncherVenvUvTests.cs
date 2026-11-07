using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

/// <summary>Mehrfach-Installationen, py-Launcher, abhängige Umgebungen und uv-Pythons.</summary>
public class LauncherVenvUvTests
{
    private const string WinPy = @"C:\Windows\py.exe";
    private const string UserPy = @"C:\Users\Test\AppData\Local\Programs\Python\Launcher\py.exe";

    private static List<WingetPackage> Sample() =>
        Winget.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Daten", "winget-list-de.txt")));

    private static ToolScan LauncherScan() => new(
        name => name == "py" ? WinPy : null, (_, _) => "Python 3.13.0", (_, _) => Task.FromResult<string?>(null), [],
        name => name == "py" ? [WinPy, UserPy] : [],
        file => file == WinPy ? "3.10.11" : file == UserPy ? "3.12.0" : null);

    private static CatalogEntry Launcher => Catalog.Entries.Single(e => e.Name == "Python Launcher (py)");

    // ---------- 1. Mehrfach installiert ----------

    [Fact]
    public async Task Older_copy_with_same_winget_id_is_not_reported_as_up_to_date()
    {
        var rows = await LauncherScan().ScanAsync(Sample(), [Launcher], default);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(("3.15.2", CheckStatus.UpdateVerfuegbar), (r.Latest, r.Status)));
        Assert.All(rows, r => Assert.Contains("Installed 2 times", r.Note));
        Assert.All(rows, r => Assert.Equal("winget upgrade --id Python.Launcher", r.UpdateCommand));
    }

    [Fact]
    public async Task Single_installation_without_update_stays_up_to_date()
    {
        var php = await LauncherScan().ScanAsync(Sample(), Catalog.Entries.Where(e => e.Name == "PHP"), default);
        Assert.Equal(CheckStatus.Aktuell, php.Single().Status);
        Assert.Null(php.Single().Note);
    }

    // ---------- 2. Welche Launcher-Datei, benutzt oder nicht ----------

    [Fact]
    public async Task Launcher_rows_name_their_file_and_whether_it_is_used()
    {
        var rows = await LauncherScan().ScanAsync(Sample(), [Launcher], default);
        var used = rows.Single(r => r.Installed == "3.10.11");
        var unused = rows.Single(r => r.Installed == "3.12.0");
        Assert.Contains(@"C:\Windows\py.exe – used by the ""py"" command", used.Note);
        Assert.Contains("not used, because C:\\Windows\\py.exe is found first", unused.Note);
        Assert.Equal(UserPy, unused.Detail);
    }

    [Fact]
    public async Task Launcher_without_winget_reads_its_own_file_version_not_the_python_version()
    {
        var rows = await LauncherScan().ScanAsync([], [Launcher], default);
        Assert.Equal("3.10.11", rows.Single().Installed); // nicht "3.13.0" aus "py --version"
    }

    // ---------- 3. Was startet py? ----------

    private static readonly List<Installation> Pythons =
    [
        new() { Tool = ToolKind.Python, Root = @"C:\Py310", Version = "3.10.11" },
        new() { Tool = ToolKind.Python, Root = @"C:\Py312", Version = "3.12.7" },
        new() { Tool = ToolKind.Python, Root = @"C:\Py313", Version = "3.13.0" },
    ];

    private static PyDefault Resolve(string? env, Dictionary<string, string> files) => PyLauncher.Resolve(Pythons,
        name => name == "PY_PYTHON" ? env : null, [@"C:\Local\py.ini", @"C:\Windows\py.ini"],
        f => files.GetValueOrDefault(f));

    [Fact]
    public void Py_ini_in_localappdata_sets_the_default()
    {
        var d = Resolve(null, new() { [@"C:\Local\py.ini"] = "[defaults]\r\npython=3.13\r\n" });
        Assert.Equal(("3.13", "3.13.0"), (d.Tag, d.Installation?.Version));
        Assert.Contains(@"C:\Local\py.ini", d.Source);
    }

    [Fact]
    public void Environment_variable_wins_over_py_ini()
    {
        var d = Resolve("3.12", new() { [@"C:\Local\py.ini"] = "[defaults]\npython=3.13" });
        Assert.Equal("3.12.7", d.Installation?.Version);
        Assert.Contains("PY_PYTHON", d.Source);
    }

    [Fact]
    public void Ini_next_to_launcher_is_used_when_localappdata_has_none()
    {
        var d = Resolve(null, new() { [@"C:\Windows\py.ini"] = "[defaults]\npython=3.10-64" });
        Assert.Equal("3.10.11", d.Installation?.Version);
    }

    [Fact]
    public void Without_any_default_the_newest_version_is_started()
    {
        var d = Resolve(null, []);
        Assert.Null(d.Tag);
        Assert.Equal("3.13.0", d.Installation?.Version);
    }

    [Theory]
    [InlineData("[commands]\npython=x\n[defaults]\n; python=3.10\npython=3.12", "3.12")]
    [InlineData("[defaults]\npython3=3.12", null)]
    [InlineData("", null)]
    public void Py_ini_default_is_read_only_from_the_defaults_section(string ini, string? expected)
        => Assert.Equal(expected, PyLauncher.ReadDefault(ini));

    // ---------- 4. Abhängige Projektumgebungen ----------

    /// <summary>Simuliertes Dateisystem: Ordner → Unterordner, Dateien → Inhalt.</summary>
    private static VenvScan FakeFs(Dictionary<string, string[]> dirs, Dictionary<string, string> files) =>
        new(d => dirs.GetValueOrDefault(d) ?? [], f => files.GetValueOrDefault(f));

    [Fact]
    public void Venvs_built_on_the_version_are_found_others_are_not()
    {
        var dirs = new Dictionary<string, string[]>
        {
            [@"C:\U"] = [@"C:\U\Desktop", @"C:\U\AppData", @"C:\U\invokeai"],
            [@"C:\U\Desktop"] = [@"C:\U\Desktop\Video", @"C:\U\Desktop\Other"],
            [@"C:\U\Desktop\Video"] = [@"C:\U\Desktop\Video\.venv"],
            [@"C:\U\Desktop\Other"] = [@"C:\U\Desktop\Other\.venv"],
            [@"C:\U\invokeai"] = [@"C:\U\invokeai\.venv"],
            [@"C:\U\AppData"] = [@"C:\U\AppData\x"],
        };
        var files = new Dictionary<string, string>
        {
            [@"C:\U\Desktop\Video\.venv\pyvenv.cfg"] = "home = C:\\Py310\nversion = 3.10.11",
            [@"C:\U\Desktop\Other\.venv\pyvenv.cfg"] = "home = C:\\Py3100\n",          // ähnlicher Name, andere Installation
            [@"C:\U\invokeai\.venv\pyvenv.cfg"] = "home = C:\\uv\\cpython-3.12.9\n",
            [@"C:\U\AppData\x\pyvenv.cfg"] = "home = C:\\Py310\n",                     // AppData wird übersprungen
        };
        var result = FakeFs(dirs, files).FindDependents(@"C:\Py310", [@"C:\U"]);
        Assert.Equal([@"C:\U\Desktop\Video\.venv"], result.Dependents.Select(d => d.VenvFolder));
        Assert.False(result.Incomplete);

        var uv = FakeFs(dirs, files).FindDependents(@"C:\uv\cpython-3.12.9", [@"C:\U"]);
        Assert.Equal([@"C:\U\invokeai\.venv"], uv.Dependents.Select(d => d.VenvFolder));
    }

    [Fact]
    public void Search_stops_at_the_time_limit_and_says_so()
    {
        var result = FakeFs(new() { [@"C:\U"] = [@"C:\U\a"] }, []).FindDependents(@"C:\Py310", [@"C:\U"], timeLimit: TimeSpan.Zero);
        Assert.True(result.Incomplete);
    }

    [Fact]
    public void Search_respects_the_maximum_depth()
    {
        var dirs = new Dictionary<string, string[]> { [@"C:\U"] = [@"C:\U\a"], [@"C:\U\a"] = [@"C:\U\a\.venv"] };
        var files = new Dictionary<string, string> { [@"C:\U\a\.venv\pyvenv.cfg"] = "home = C:\\Py310" };
        Assert.Empty(FakeFs(dirs, files).FindDependents(@"C:\Py310", [@"C:\U"], maxDepth: 1).Dependents);
        Assert.Single(FakeFs(dirs, files).FindDependents(@"C:\Py310", [@"C:\U"], maxDepth: 2).Dependents);
    }

    // ---------- 5. uv-Pythons ----------

    [Fact]
    public void Uv_python_is_compared_with_the_newest_release_and_points_to_uv()
    {
        var uv = new Installation { Tool = ToolKind.Python, Root = @"C:\uv\cpython-3.12.9", Version = "3.12.9", Source = InstallSource.Uv };
        Version[] installers = [new(3, 12, 10)];
        Version[] all = [new(3, 12, 16)];
        var item = RuntimeScan.Python([uv], installers, all, null).Single(i => i.Name == "Python 3.12");
        Assert.Equal(("3.12.16", CheckStatus.UpdateVerfuegbar), (item.Latest, item.Status));
        Assert.Contains("with uv", item.Hint);
        Assert.Contains("Managed by uv", item.Note);
        Assert.Null(item.UpdateCommand); // kein python.org-/winget-Befehl für uv-Pythons
        Assert.NotNull(item.Target);

        uv.Version = "3.12.16";
        Assert.Equal(CheckStatus.Aktuell, RuntimeScan.Python([uv], installers, all, null).Single(i => i.Name == "Python 3.12").Status);
    }

    [Fact]
    public void Uv_source_survives_saving_and_loading()
    {
        using var tmp = new TempDir();
        var store = new FakePathStore();
        var sw = new Switcher(new DataFolders(tmp.Path), store, new FakeSystemWriter(store, true));
        sw.AddDownloaded(new Installation { Tool = ToolKind.Python, Root = @"C:\uv\cpython-3.12.9", Version = "3.12.9", Source = InstallSource.Uv });
        Assert.Equal(InstallSource.Uv, new SettingsStore(sw.Folders).Load().Installations.Single().Source);
    }
}
