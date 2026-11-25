using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

/// <summary>Nebeneinander installierte Versionen (z. B. .NET SDK): ältere Kopien sind kein Update-Fall.</summary>
public class SideBySideTests
{
    private static ToolScan Scan() => new(_ => null, (_, _) => null, (_, _) => Task.FromResult<string?>(null), []);

    private static readonly CatalogEntry DotNet = Catalog.Entries.Single(e => e.Name == ".NET SDK");

    [Fact]
    public async Task Older_dotnet_sdk_copy_is_a_note_without_update_command()
    {
        var rows = await Scan().ScanAsync([
            new WingetPackage("Microsoft .NET SDK 10.0.403 (x64)", "Microsoft.DotNet.SDK.10", "10.0.403", "10.0.404", "winget", false),
            new WingetPackage("Microsoft .NET SDK 10.0.404 (x64)", "Microsoft.DotNet.SDK.10", "10.0.404", null, "winget", false),
        ], [DotNet], default);

        var old = rows.Single(r => r.Installed == "10.0.403");
        Assert.Equal(CheckStatus.Hinweis, old.Status);
        Assert.Null(old.UpdateCommand);
        Assert.Contains("10.0.404 is installed next to it", old.Hint);
        Assert.Contains("winget uninstall --id Microsoft.DotNet.SDK.10 --version 10.0.403", old.Hint);

        var current = rows.Single(r => r.Installed == "10.0.404");
        Assert.Equal(CheckStatus.Aktuell, current.Status);
        Assert.Contains("Also installed: 10.0.403", current.Note);
        Assert.DoesNotContain("winget may update only one copy", current.Note);
    }

    [Fact]
    public async Task Newest_copy_still_gets_its_update()
    {
        var rows = await Scan().ScanAsync([
            new WingetPackage("Microsoft .NET SDK 10.0.403 (x64)", "Microsoft.DotNet.SDK.10", "10.0.403", "10.0.405", "winget", false),
            new WingetPackage("Microsoft .NET SDK 10.0.404 (x64)", "Microsoft.DotNet.SDK.10", "10.0.404", "10.0.405", "winget", false),
        ], [DotNet], default);

        Assert.Equal(CheckStatus.Hinweis, rows.Single(r => r.Installed == "10.0.403").Status);
        var current = rows.Single(r => r.Installed == "10.0.404");
        Assert.Equal(CheckStatus.UpdateVerfuegbar, current.Status);
        Assert.Equal("winget upgrade --id Microsoft.DotNet.SDK.10", current.UpdateCommand);
    }

    [Fact]
    public async Task Other_programs_keep_the_duplicate_warning()
    {
        var npp = Catalog.Entries.Single(e => e.Name == "Notepad++");
        var rows = await Scan().ScanAsync([
            new WingetPackage("Notepad++ (64-bit x64)", "Notepad++.Notepad++", "8.7.5", "8.9.9", "winget", false),
            new WingetPackage("Notepad++ (64-bit x64)", "Notepad++.Notepad++", "8.9.9", null, "winget", false),
        ], [npp], default);
        Assert.Equal(CheckStatus.UpdateVerfuegbar, rows.Single(r => r.Installed == "8.7.5").Status);
        Assert.All(rows, r => Assert.Contains("winget may update only one copy", r.Note));
    }
}
