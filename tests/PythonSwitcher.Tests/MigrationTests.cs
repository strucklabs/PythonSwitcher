using PythonSwitcher.Core;

namespace PythonSwitcher.Tests;

/// <summary>Umzug der Daten aus älteren (deutschsprachigen) Versionen.</summary>
public class MigrationTests
{
    private static Switcher Open(string root)
    {
        var store = new FakePathStore();
        return new Switcher(new DataFolders(root), store, new FakeSystemWriter(store, true));
    }

    [Fact]
    public void Old_settings_and_backups_are_moved_to_new_names()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "einstellungen.json"), """{ "Installations": [], "Ignored": [], "BackupLimit": 5 }""");
        var oldBackup = Path.Combine(tmp.Path, "Sicherungen", "20261107-120000-000");
        Directory.CreateDirectory(oldBackup);
        File.WriteAllText(Path.Combine(oldBackup, "Benutzer.txt"), @"C:\Alt");

        var sw = Open(tmp.Path);

        Assert.Equal(5, sw.Settings.BackupLimit);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "settings.json")));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "einstellungen.json")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "Sicherungen")));
        Assert.Equal(@"C:\Alt", sw.LatestBackup()!.User); // alte Datei Benutzer.txt wird weiter gelesen
    }

    [Fact]
    public void Existing_new_settings_are_never_overwritten()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "einstellungen.json"), """{ "BackupLimit": 5 }""");
        File.WriteAllText(Path.Combine(tmp.Path, "settings.json"), """{ "BackupLimit": 30 }""");

        var sw = Open(tmp.Path);

        Assert.Equal(30, sw.Settings.BackupLimit);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "einstellungen.json"))); // bleibt liegen, nichts geht verloren
    }

    [Fact]
    public void Both_backup_folders_are_merged_without_overwriting()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Sicherungen", "20261101-120000-000"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Sicherungen", "20261102-120000-000"));
        File.WriteAllText(Path.Combine(tmp.Path, "Sicherungen", "20261102-120000-000", "Benutzer.txt"), "alt");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Backups", "20261102-120000-000"));
        File.WriteAllText(Path.Combine(tmp.Path, "Backups", "20261102-120000-000", "User.txt"), "neu");

        var sw = Open(tmp.Path);

        Assert.Equal(2, sw.BackupCount);
        Assert.Equal("neu", File.ReadAllText(Path.Combine(tmp.Path, "Backups", "20261102-120000-000", "User.txt")));
        // Der Konfliktfall bleibt im alten Ordner erhalten
        Assert.True(File.Exists(Path.Combine(tmp.Path, "Sicherungen", "20261102-120000-000", "Benutzer.txt")));
    }

    [Fact]
    public void New_backups_use_english_file_names()
    {
        using var tmp = new TempDir();
        var store = new FakePathStore { User = @"C:\A;C:\B" };
        var sw = new Switcher(new DataFolders(tmp.Path), store, new FakeSystemWriter(store, true), _ => false, _ => true);
        sw.Apply(new PathPlan { User = [@"C:\B", @"C:\A"], System = [], UserChanged = true });
        var dir = Directory.GetDirectories(Path.Combine(tmp.Path, "Backups")).Single();
        Assert.True(File.Exists(Path.Combine(dir, "User.txt")));
    }
}
