using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

public class ToolScanTests
{
    private static List<WingetPackage> Sample() =>
        Winget.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Daten", "winget-list-de.txt")));

    private static ToolScan NoCommands() => new(_ => null, (_, _) => null, (_, _) => Task.FromResult<string?>(null), []);

    [Fact]
    public async Task Catalog_finds_installed_programs_from_real_winget_output()
    {
        var items = await NoCommands().ScanAsync(Sample(), Catalog.Entries, default);

        var npp = items.Single(i => i.Name == "Notepad++");
        Assert.Equal(("8.7.5", "8.9.9", CheckStatus.UpdateVerfuegbar), (npp.Installed, npp.Latest, npp.Status));
        Assert.Contains("winget upgrade --id Notepad++.Notepad++", npp.Hint);

        var java = items.Single(i => i.Name == "Java");
        Assert.Equal(("8.0.5110.33", CheckStatus.Aktuell), (java.Installed, java.Status));

        var php = items.Single(i => i.Name == "PHP");
        Assert.Equal(("8.4.27", CheckStatus.Aktuell), (php.Installed, php.Status));

        var sdk = items.Single(i => i.Name == ".NET SDK");
        Assert.Equal(("10.0.402", "10.0.403", CheckStatus.UpdateVerfuegbar), (sdk.Installed, sdk.Latest, sdk.Status));

        Assert.Equal(2, items.Count(i => i.Name == "Python Launcher (py)"));
    }

    [Fact]
    public async Task Programs_that_are_not_installed_do_not_appear()
    {
        var items = await NoCommands().ScanAsync(Sample(), Catalog.Entries, default);
        Assert.DoesNotContain(items, i => i.Name is "Visual Studio Code" or "PyCharm" or "Node.js" or "uv");
        // XmlNotepad darf nicht als Notepad++ gelten
        Assert.Single(items, i => i.Name == "Notepad++");
    }

    [Fact]
    public async Task Tool_found_in_path_gets_version_from_command_and_latest_from_pypi()
    {
        var scan = new ToolScan(
            name => name == "pipx" ? @"C:\Users\Test\.local\bin\pipx.exe" : null,
            (_, _) => "1.6.0\n",
            (pkg, _) => Task.FromResult<string?>(pkg == "pipx" ? "1.7.1" : null),
            [@"C:\Py312"]);
        var entry = Catalog.Entries.Single(e => e.Name == "pipx");
        var item = (await scan.ScanAsync([], [entry], default)).Single();
        Assert.Equal(("1.6.0", "1.7.1", CheckStatus.UpdateVerfuegbar), (item.Installed, item.Latest, item.Status));
        Assert.Contains("pip install --upgrade pipx", item.Hint);
    }

    [Fact]
    public async Task Tool_inside_a_python_installation_is_left_to_the_pip_section()
    {
        var scan = new ToolScan(
            _ => @"C:\Py312\Scripts\ruff.exe", (_, _) => "ruff 0.6.9",
            (_, _) => Task.FromResult<string?>("0.9.0"), [@"C:\Py312"]);
        var entry = Catalog.Entries.Single(e => e.Name == "ruff");
        Assert.Empty(await scan.ScanAsync([], [entry], default));
    }

    [Fact]
    public async Task Tools_that_manage_python_carry_a_note()
    {
        var scan = new ToolScan(n => n == "uv" ? @"C:\Tools\uv.exe" : null, (_, _) => "uv 0.13.7 (abc 2026-10-01)",
            (_, _) => Task.FromResult<string?>("0.13.7"), []);
        var item = (await scan.ScanAsync([], Catalog.Entries.Where(e => e.Name == "uv"), default)).Single();
        Assert.Equal(CheckStatus.Aktuell, item.Status);
        Assert.NotNull(item.Note);
    }

    [Theory]
    [InlineData("v22.11.0", "22.11.0")]
    [InlineData("go version go1.23.4 windows/amd64", "1.23.4")]
    [InlineData("Poetry (version 1.8.3)", "1.8.3")]
    [InlineData("rustc 1.82.0 (f6e511eec 2024-10-15)", "1.82.0")]
    [InlineData("PHP 8.4.27 (cli) (built: ...)", "8.4.27")]
    [InlineData("keine Zahl", null)]
    public void Version_is_extracted_from_command_output(string text, string? expected)
        => Assert.Equal(expected, Catalog.ExtractVersion(text));

    [Theory]
    [InlineData("3.12.7", "3.12.10", -1)]
    [InlineData("2.56.0.windows.4", "2.56.0.4", 0)]
    [InlineData("10.0.402", "10.0.402", 0)]
    [InlineData("1.10", "1.9", 1)]
    public void Versions_are_compared_numerically(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(VersionCompare.Compare(a, b)!.Value));
}
