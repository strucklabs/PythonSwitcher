using PythonSwitcher.Core;

namespace PythonSwitcher.Tests;

/// <summary>Rückgängig ohne tote Einträge und Begrenzung der Sicherungen – nur auf nachgebautem PATH.</summary>
public class RestoreAndLimitTests
{
    private const string Py310 = @"C:\Py310";
    private const string Py314 = @"C:\Data\python\3.14.10";

    /// <summary>Simuliertes Dateisystem, in dem sich Ordner "löschen" lassen.</summary>
    private sealed class FakeDisk
    {
        public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase)
            { Py310, Py310 + @"\Scripts", Py314, Py314 + @"\Scripts", @"C:\Tools" };
        public bool DirExists(string d) => Dirs.Contains(d.TrimEnd('\\'));
        public bool FileExists(string f) => Dirs.Contains(Path.GetDirectoryName(f)!) && f.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase)
                                            && !f.Contains(@"\Scripts\", StringComparison.OrdinalIgnoreCase);
        public void Delete(string root) => Dirs.RemoveWhere(d => d.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private static (Switcher, FakePathStore, FakeDisk, TempDir) Setup(string user)
    {
        var tmp = new TempDir();
        var disk = new FakeDisk();
        var store = new FakePathStore { User = user, System = @"C:\Windows" };
        var sw = new Switcher(new DataFolders(tmp.Path), store, new FakeSystemWriter(store, true), disk.FileExists, disk.DirExists);
        return (sw, store, disk, tmp);
    }

    [Fact]
    public void Undo_after_deleting_a_version_offers_to_leave_out_its_entries()
    {
        const string start = $@"{Py310}\Scripts;{Py310};;{Py314}\Scripts;{Py314};C:\Tools";
        var (sw, store, disk, tmp) = Setup(start);
        using (tmp)
        {
            // Entfernen mit PATH-Einträgen, dann Dateien löschen (wie im Programm)
            sw.Apply(sw.Planner(ToolDefinition.Python).PlanRemoval(Py314, sw.Read(PathScope.Benutzer), sw.Read(PathScope.System), false));
            disk.Delete(Py314);
            var afterRemoval = store.User;

            var plan = sw.PlanRestore()!;
            Assert.True(plan.HasDead);
            Assert.Equal([$@"{Py314}\Scripts", Py314], plan.DeadUser);

            Assert.True(sw.RestoreLatest(omitDead: true));
            // Alles wie in der Sicherung (inkl. leerem Eintrag), nur ohne die toten Einträge
            Assert.Equal($@"{Py310}\Scripts;{Py310};;C:\Tools", store.User);
            Assert.NotEqual(afterRemoval, store.User);
        }
    }

    [Fact]
    public void Undo_can_still_restore_dead_entries_if_wanted()
    {
        const string start = $@"{Py310};{Py314}";
        var (sw, store, disk, tmp) = Setup(start);
        using (tmp)
        {
            sw.Apply(sw.Planner(ToolDefinition.Python).PlanRemoval(Py314, sw.Read(PathScope.Benutzer), sw.Read(PathScope.System), false));
            disk.Delete(Py314);
            Assert.True(sw.RestoreLatest(omitDead: false));
            Assert.Equal(start, store.User);
        }
    }

    [Fact]
    public void Dead_entries_already_in_path_are_not_touched()
    {
        // C:\Weg existiert nicht, steht aber schon jetzt im PATH – das ist Sache des Benutzers.
        const string start = $@"C:\Weg;{Py310};C:\Tools";
        var (sw, store, _, tmp) = Setup(start);
        using (tmp)
        {
            sw.Apply(sw.Planner(ToolDefinition.Python).PlanRemoval(@"C:\Tools", sw.Read(PathScope.Benutzer), sw.Read(PathScope.System), false));
            var plan = sw.PlanRestore()!;
            Assert.False(plan.HasDead);
            Assert.True(sw.RestoreLatest());
            Assert.Equal(start, store.User);
        }
    }

    [Fact]
    public void Without_keeps_everything_else_character_exact()
    {
        Assert.Equal(@"A ; ;B;", Switcher.Without(@"A ; ;X;B;", ["X"]));
        Assert.Equal("A;B", Switcher.Without("A;B", []));
    }

    [Fact]
    public void Default_limit_is_50_and_survives_restart()
    {
        using var tmp = new TempDir();
        var store = new FakePathStore();
        var sw = new Switcher(new DataFolders(tmp.Path), store, new FakeSystemWriter(store, true));
        Assert.Equal(50, sw.Settings.BackupLimit);
        sw.SetBackupLimit(7);
        Assert.Equal(7, new SettingsStore(sw.Folders).Load().BackupLimit);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100000, 9999)]
    public void Limit_is_kept_within_bounds(int input, int expected)
    {
        var s = new AppSettings { BackupLimit = input };
        Assert.Equal(expected, s.BackupLimit);
    }

    [Fact]
    public void Old_settings_file_without_limit_gets_default()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "einstellungen.json"), """{ "Installations": [], "Ignored": [] }""");
        Assert.Equal(50, new SettingsStore(new DataFolders(tmp.Path)).Load().BackupLimit);
    }

    [Fact]
    public void Only_the_newest_backups_are_kept_and_foreign_files_stay()
    {
        using var tmp = new TempDir();
        var folders = new DataFolders(tmp.Path);
        Directory.CreateDirectory(folders.Backups);
        // 6 vorhandene Sicherungen mit festen Zeitstempeln, dazu fremde Inhalte
        for (int i = 1; i <= 6; i++)
            Directory.CreateDirectory(Path.Combine(folders.Backups, $"2026110{i}-120000-000"));
        Directory.CreateDirectory(Path.Combine(folders.Backups, "Meine Notizen"));
        File.WriteAllText(Path.Combine(folders.Backups, "liesmich.txt"), "x");

        var store = new FakePathStore { User = @"C:\A;C:\B" };
        var sw = new Switcher(folders, store, new FakeSystemWriter(store, true), _ => false, _ => true);
        Assert.Equal(3, sw.BackupsToDelete(3));
        Assert.Equal(3, sw.SetBackupLimit(3));

        var left = Directory.GetDirectories(folders.Backups).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["20261104-120000-000", "20261105-120000-000", "20261106-120000-000", "Meine Notizen"], left);
        Assert.True(File.Exists(Path.Combine(folders.Backups, "liesmich.txt")));

        // Jede neue Sicherung hält die Grenze ein; die neue ist dabei.
        sw.Apply(new PathPlan { User = ["C:\\B", "C:\\A"], System = [], UserChanged = true });
        Assert.Equal(3, sw.BackupCount);
        Assert.Equal(@"C:\A;C:\B", sw.LatestBackup()!.User);
    }
}
