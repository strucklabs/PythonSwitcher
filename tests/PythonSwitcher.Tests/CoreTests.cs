using PythonSwitcher.Core;

namespace PythonSwitcher.Tests;

/// <summary>PATH-Speicher im Arbeitsspeicher, damit kein Test den echten PATH ändert.</summary>
internal sealed class FakePathStore : IPathStore
{
    public string User = "";
    public string System = "";
    public int Writes;

    public string Read(PathScope scope) => scope == PathScope.System ? System : User;

    public void Write(PathScope scope, string value)
    {
        Writes++;
        if (scope == PathScope.System) System = value; else User = value;
    }
}

internal sealed class FakeSystemWriter(FakePathStore store, bool accept) : ISystemPathWriter
{
    public bool Write(string value)
    {
        if (!accept)
            return false;
        store.System = value;
        return true;
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PythonSwitcherTest-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (Exception) { }
    }
}

public class CoreTests
{
    [Theory]
    [InlineData("Python 3.12.4", "3.12.4")]
    [InlineData("Python 3.14.0rc1\r\n", "3.14.0rc1")]
    [InlineData("\nPython 2.7.18", "2.7.18")]
    [InlineData("irgendwas", null)]
    public void Python_version_is_read_from_output(string output, string? expected)
        => Assert.Equal(expected, ToolDefinition.Python.ParseVersion(output));

    [Theory]
    [InlineData("git version 2.53.0.windows.2", "2.53.0.windows.2")]
    [InlineData("git version 2.47.1", "2.47.1")]
    public void Git_version_is_read_from_output(string output, string expected)
        => Assert.Equal(expected, ToolDefinition.Git.ParseVersion(output));

    [Theory]
    [InlineData(@"C:\A\Python310\Scripts", @"C:\A\Python310", true)]
    [InlineData(@"C:\A\Python310\", @"c:\a\python310", true)]
    [InlineData(@"C:\A\Python310", @"C:\A\Python31", false)]
    [InlineData(@"""C:\A\B""", @"C:\A", true)]
    public void IsUnder_respects_folder_boundaries(string entry, string root, bool expected)
        => Assert.Equal(expected, PathUtil.IsUnder(entry, root));

    [Fact]
    public void Git_root_is_found_from_mingw_bin_folder()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Git\cmd\git.exe", @"C:\Git\mingw64\bin\git.exe" };
        Assert.Equal(@"C:\Git", ToolDefinition.Git.FindRoot(@"C:\Git\mingw64\bin", files.Contains));
        Assert.Equal(@"C:\Git", ToolDefinition.Git.FindRoot(@"C:\Git\cmd", files.Contains));
    }

    [Fact]
    public void Python_catalog_keeps_only_final_64bit_installers_newest_first()
    {
        const string json = """
        [
          {"url":"https://www.python.org/ftp/python/3.12.4/python-3.12.4-amd64.exe","md5_sum":"aa","sha256_sum":"","filesize":10},
          {"url":"https://www.python.org/ftp/python/3.12.4/python-3.12.4.exe","md5_sum":"bb","sha256_sum":"","filesize":10},
          {"url":"https://www.python.org/ftp/python/3.14.0/python-3.14.0rc1-amd64.exe","md5_sum":"cc","sha256_sum":"","filesize":10},
          {"url":"https://www.python.org/ftp/python/3.13.9/python-3.13.9-amd64.exe","md5_sum":"dd","sha256_sum":"","filesize":10},
          {"url":"https://www.python.org/ftp/python/3.13.10/python-3.13.10-amd64.exe","md5_sum":"","sha256_sum":"","filesize":10},
          {"url":"https://www.python.org/ftp/python/3.12.4/python-3.12.4-embed-amd64.zip","md5_sum":"ee","sha256_sum":"","filesize":10}
        ]
        """;
        var list = ReleaseCatalog.ParsePython(json);
        Assert.Equal(["3.13.9", "3.12.4"], list.Select(r => r.Version));
        Assert.Equal("dd", list[0].Md5);
    }

    [Fact]
    public void Git_catalog_skips_prereleases_and_assets_without_checksum()
    {
        const string json = """
        [
          {"prerelease":true,"draft":false,"assets":[{"name":"PortableGit-2.57.0-rc0-64-bit.7z.exe","size":1,"digest":"sha256:aa","browser_download_url":"u0"}]},
          {"prerelease":false,"draft":false,"assets":[
            {"name":"PortableGit-2.56.0.4-64-bit.7z.exe","size":5,"digest":"sha256:abc","browser_download_url":"u1"},
            {"name":"Git-2.56.0.4-64-bit.exe","size":5,"digest":"sha256:def","browser_download_url":"u2"}]},
          {"prerelease":false,"draft":false,"assets":[{"name":"PortableGit-2.55.0-64-bit.7z.exe","size":5,"digest":null,"browser_download_url":"u3"}]}
        ]
        """;
        var list = ReleaseCatalog.ParseGit(json);
        Assert.Single(list);
        Assert.Equal("2.56.0.4", list[0].Version);
        Assert.Equal("abc", list[0].Sha256);
    }

    [Fact]
    public void Backups_are_written_and_latest_is_found()
    {
        using var tmp = new TempDir();
        var backups = new PathBackups(tmp.Path);
        backups.Save("eins", null);
        Thread.Sleep(5);
        backups.Save("zwei;ä", "sys");
        var latest = backups.Latest()!;
        Assert.Equal("zwei;ä", latest.User);
        Assert.Equal("sys", latest.System);
    }

    private static (Switcher, FakePathStore, TempDir) NewSwitcher(bool acceptUac, string user, string system, Func<string, bool> files)
    {
        var tmp = new TempDir();
        var store = new FakePathStore { User = user, System = system };
        var sw = new Switcher(new DataFolders(tmp.Path), store, new FakeSystemWriter(store, acceptUac), files);
        return (sw, store, tmp);
    }

    [Fact]
    public void Declined_uac_leaves_both_paths_unchanged()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Git\cmd\git.exe", @"D:\PGit\cmd\git.exe" };
        var (sw, store, tmp) = NewSwitcher(false, @"C:\Tools;D:\PGit\cmd", @"C:\Windows;C:\Git\cmd", files.Contains);
        using (tmp)
        {
            var plan = sw.Planner(ToolDefinition.Git).PlanActivation(@"D:\PGit",
                sw.Read(PathScope.Benutzer), sw.Read(PathScope.System), allowSystem: true);
            Assert.False(sw.Apply(plan));
            Assert.Equal(@"C:\Tools;D:\PGit\cmd", store.User);
            Assert.Equal(@"C:\Windows;C:\Git\cmd", store.System);
            Assert.Equal(0, store.Writes);
        }
    }

    [Fact]
    public void Apply_backs_up_and_restore_brings_old_value_back()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Py1\python.exe", @"C:\Py2\python.exe" };
        const string original = @"C:\Py1\Scripts;C:\Py1;;C:\Py2\Scripts;C:\Py2";
        var (sw, store, tmp) = NewSwitcher(true, original, @"C:\Windows", files.Contains);
        using (tmp)
        {
            var plan = sw.Planner(ToolDefinition.Python).PlanActivation(@"C:\Py2",
                sw.Read(PathScope.Benutzer), sw.Read(PathScope.System), allowSystem: false);
            Assert.True(sw.Apply(plan));
            Assert.Equal(@"C:\Py2\Scripts;C:\Py2;C:\Py1\Scripts;C:\Py1", store.User);
            Assert.Equal(original, sw.LatestBackup()!.User);

            Assert.True(sw.RestoreLatest());
            Assert.Equal(original, store.User); // auch der leere Eintrag ist wieder da
            Assert.Equal(@"C:\Windows", store.System);
        }
    }

    [Fact]
    public void Unregistered_installation_is_not_picked_up_again()
    {
        var (sw, _, tmp) = NewSwitcher(true, "", "", _ => false);
        using (tmp)
        {
            var inst = new Installation { Tool = ToolKind.Python, Root = @"C:\Py1", Version = "3.12.0" };
            sw.AddDownloaded(inst);
            sw.Unregister(inst);
            Assert.Empty(sw.Settings.Installations);
            Assert.True(sw.Settings.IsIgnored(@"c:\py1\"));
            // Einstellungen überleben einen Neustart
            var reloaded = new SettingsStore(sw.Folders).Load();
            Assert.True(reloaded.IsIgnored(@"C:\Py1"));
        }
    }
}
