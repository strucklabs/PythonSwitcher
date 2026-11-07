using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

/// <summary>Python/Git-Updates mit dem Stand vom 07.11.2026 (Zahlen von python.org und GitHub).</summary>
public class RuntimeScanTests
{
    private static Installation Py(string v, InstallSource s = InstallSource.Gefunden) =>
        new() { Tool = ToolKind.Python, Root = $@"C:\Py\{v}", Version = v, Source = s };

    private static readonly Version[] Installers =
        [new(3, 15, 2), new(3, 14, 10), new(3, 13, 17), new(3, 12, 10), new(3, 10, 11)];
    private static readonly Version[] All =
        [new(3, 15, 2), new(3, 14, 10), new(3, 13, 18), new(3, 12, 16), new(3, 10, 22)];

    private static List<WingetPackage> Winget() =>
        PythonSwitcher.Core.VersionCheck.Winget.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Daten", "winget-list-de.txt")));

    [Fact]
    public void Newer_patch_with_installer_is_reported()
    {
        var items = RuntimeScan.Python([Py("3.10.11"), Py("3.12.7"), Py("3.13.0")], Installers, All, Winget());

        var p312 = items.Single(i => i.Name == "Python 3.12");
        Assert.Equal(("3.12.7", "3.12.10", CheckStatus.UpdateVerfuegbar), (p312.Installed, p312.Latest, p312.Status));
        Assert.Contains("winget upgrade --id Python.Python.3.12", p312.Hint);

        var p313 = items.Single(i => i.Name == "Python 3.13");
        Assert.Equal(("3.13.0", "3.13.17", CheckStatus.UpdateVerfuegbar), (p313.Installed, p313.Latest, p313.Status));
    }

    [Fact]
    public void Version_without_newer_installer_is_current_with_note()
    {
        var items = RuntimeScan.Python([Py("3.10.11")], Installers, All, null);
        var p310 = items.Single(i => i.Name == "Python 3.10");
        Assert.Equal(CheckStatus.Aktuell, p310.Status);
        Assert.Contains("3.10.22", p310.Note);
        Assert.Contains("keine Windows-Installer", p310.Note);
    }

    [Fact]
    public void Newer_major_version_appears_as_hint()
    {
        var items = RuntimeScan.Python([Py("3.10.11"), Py("3.12.7"), Py("3.13.0")], Installers, All, null);
        var hint = items.Single(i => i.Status == CheckStatus.Hinweis);
        Assert.Equal(("Python 3.15", "3.15.2"), (hint.Name, hint.Latest));
    }

    [Fact]
    public void No_major_hint_when_newest_is_installed()
    {
        var items = RuntimeScan.Python([Py("3.15.2")], Installers, All, null);
        Assert.DoesNotContain(items, i => i.Status == CheckStatus.Hinweis);
        Assert.Equal(CheckStatus.Aktuell, items.Single().Status);
    }

    [Fact]
    public void Downloaded_version_gets_installer_hint_not_winget()
    {
        var items = RuntimeScan.Python([Py("3.14.9", InstallSource.Heruntergeladen)], Installers, All, Winget());
        var item = items.Single(i => i.Name == "Python 3.14");
        Assert.Equal(CheckStatus.UpdateVerfuegbar, item.Status);
        Assert.DoesNotContain("winget", item.Hint);
    }

    [Fact]
    public void Git_update_is_reported_and_windows_suffix_is_understood()
    {
        AvailableRelease Rel(string v, Version k) => new(ToolKind.Git, v, k, "u", "f", 1, "x", null);
        var releases = new[] { Rel("2.56.0.4", new Version(2, 56, 0, 4)), Rel("2.56.0.3", new Version(2, 56, 0, 3)) };
        var git = new Installation { Tool = ToolKind.Git, Root = @"C:\Program Files\Git", Version = "2.53.0.windows.2" };

        var item = RuntimeScan.Git([git], releases, Winget()).Single();
        Assert.Equal(("2.53.0.windows.2", "2.56.0.4", CheckStatus.UpdateVerfuegbar), (item.Installed, item.Latest, item.Status));
        Assert.Contains("winget upgrade --id Git.Git", item.Hint);

        git.Version = "2.56.0.windows.4";
        Assert.Equal(CheckStatus.Aktuell, RuntimeScan.Git([git], releases, null).Single().Status);
    }

    [Fact]
    public void All_versions_list_skips_prereleases()
    {
        const string json = """
        [{"name":"Python 3.10.22","pre_release":false},{"name":"Python 3.15.0rc1","pre_release":true},
         {"name":"Python install manager 26.4","pre_release":false},{"name":"Python 3.12.16","pre_release":false}]
        """;
        Assert.Equal([new Version(3, 12, 16), new Version(3, 10, 22)], ReleaseCatalog.ParsePythonAllVersions(json));
    }
}
