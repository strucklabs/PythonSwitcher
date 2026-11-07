using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher.Tests;

/// <summary>Auswertung von "winget list" an einer echten, gekürzten Ausgabe (deutsche Spaltenköpfe).</summary>
public class WingetTests
{
    private static string Sample() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Daten", "winget-list-de.txt"));

    private static WingetPackage Get(List<WingetPackage> list, string id) => list.Single(p => p.Id == id);

    [Fact]
    public void Real_output_gives_installed_and_available_versions()
    {
        var list = Winget.Parse(Sample());
        Assert.Equal(16, list.Count);

        var git = Get(list, "Git.Git");
        Assert.Equal(("2.53.0.2", "2.56.0.4", "winget"), (git.Version, git.Available, git.Source));

        var npp = Get(list, "Notepad++.Notepad++");
        Assert.Equal(("Notepad++ (64-bit x64)", "8.7.5", "8.9.9"), (npp.Name, npp.Version, npp.Available));

        Assert.Equal(("3.12.7", "3.12.10"), (Get(list, "Python.Python.3.12").Version, Get(list, "Python.Python.3.12").Available));
        Assert.Equal(("3.13.0", "3.13.17"), (Get(list, "Python.Python.3.13").Version, Get(list, "Python.Python.3.13").Available));
        Assert.Equal(("10.0.402", "10.0.403"), (Get(list, "Microsoft.DotNet.SDK.10").Version, Get(list, "Microsoft.DotNet.SDK.10").Available));
        Assert.All(list, p => Assert.False(p.Uncertain));
    }

    [Fact]
    public void Packages_without_update_have_no_available_version()
    {
        var list = Winget.Parse(Sample());
        var py310 = Get(list, "Python.Python.3.10");
        Assert.Equal("3.10.11", py310.Version);
        Assert.Null(py310.Available);
        Assert.Equal("winget", py310.Source);

        var php = Get(list, "PHP.PHP.NTS.8.4");
        Assert.Equal("8.4.27", php.Version);
        Assert.Null(php.Available);
    }

    [Fact]
    public void Unknown_version_and_foreign_entries_are_handled()
    {
        var list = Winget.Parse(Sample());
        var game = list.Single(p => p.Name == "7 Days to Die");
        Assert.Null(game.Version);   // "Unknown"
        Assert.Null(game.Source);    // nicht aus winget
        Assert.Contains(list, p => p.Name == "Generation Zero®");
        Assert.Contains(list, p => p.Name == "Host der Store-Benutzeroberfläche");
    }

    [Fact]
    public void Duplicate_ids_are_kept()
    {
        var launchers = Winget.Parse(Sample()).Where(p => p.Id == "Python.Launcher").ToList();
        Assert.Equal(2, launchers.Count);
        Assert.Contains(launchers, l => l.Available == "3.15.2");
    }

    [Fact]
    public void English_headers_work_too()
    {
        var en = Sample()
            .Replace("Verfügbar", "Available")
            .Replace("Quelle", "Source");
        var git = Winget.Parse(en).Single(p => p.Id == "Git.Git");
        Assert.Equal(("2.53.0.2", "2.56.0.4", "winget"), (git.Version, git.Available, git.Source));
    }

    [Fact]
    public void Table_without_available_column_is_read_correctly()
    {
        const string text =
            "Name                ID                  Version     Quelle\n" +
            "----------------------------------------------------------\n" +
            "Notepad++           Notepad++.Notepad++ 8.9.9       winget\n";
        var p = Winget.Parse(text).Single();
        Assert.Equal(("8.9.9", null, "winget"), (p.Version, p.Available, p.Source));
    }

    [Fact]
    public void Shifted_or_truncated_rows_are_marked_uncertain()
    {
        const string text =
            "Name          ID            Version   Verfügbar Quelle\n" +
            "------------------------------------------------------\n" +
            "Ein sehr langer NameXyz.Tool 1.0      2.0       winget\n" +
            "Gekürzt…      Abc.Tool      1.0       2.0       winget\n" +
            "Normal        Def.Tool      1.0       2.0       winget\n";
        var list = Winget.Parse(text);
        Assert.True(list[0].Uncertain);
        Assert.True(list[1].Uncertain);
        Assert.False(list[2].Uncertain);
    }

    [Fact]
    public void Progress_characters_before_the_table_are_ignored()
    {
        var text = "\r   - \r   \\ \r" + Sample();
        Assert.Equal(16, Winget.Parse(text).Count);
    }

    [Fact]
    public void Garbage_gives_empty_list()
    {
        Assert.Empty(Winget.Parse("Es wurde kein installiertes Paket gefunden."));
        Assert.Empty(Winget.Parse(""));
    }
}
