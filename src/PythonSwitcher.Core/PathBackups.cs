using System.Text;

namespace PythonSwitcher.Core;

/// <summary>Eine Sicherung: Zeitpunkt und die gesicherten PATH-Werte (Benutzer und/oder System).</summary>
public sealed record PathBackup(string Folder, DateTime Time, string? User, string? System);

/// <summary>
/// Sichert PATH-Werte vor jeder Änderung. Jede Änderung bekommt einen eigenen Ordner
/// Sicherungen\JJJJMMTT-HHMMSS-fff mit Benutzer.txt und/oder System.txt.
/// Die Dateien sind reiner Text und lassen sich auch von Hand zurückkopieren.
/// </summary>
public sealed class PathBackups
{
    private const string UserFile = "User.txt";
    private const string LegacyUserFile = "Benutzer.txt"; // ältere Versionen
    private const string SystemFile = "System.txt";
    private readonly string _folder;

    public PathBackups(string folder)
    {
        _folder = folder;
    }

    public PathBackup Save(string? user, string? system)
    {
        if (user is null && system is null)
            throw new ArgumentException("At least one value must be backed up.");

        var time = DateTime.Now;
        var name = time.ToString("yyyyMMdd-HHmmss-fff");
        var dir = Path.Combine(_folder, name);
        for (int i = 2; Directory.Exists(dir); i++)
            dir = Path.Combine(_folder, $"{name}-{i}");
        Directory.CreateDirectory(dir);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (user is not null)
            File.WriteAllText(Path.Combine(dir, UserFile), user, utf8);
        if (system is not null)
            File.WriteAllText(Path.Combine(dir, SystemFile), system, utf8);
        return new PathBackup(dir, time, user, system);
    }

    /// <summary>Nur Ordner mit diesem Namensmuster gelten als Sicherung; alles andere bleibt unangetastet.</summary>
    private static readonly System.Text.RegularExpressions.Regex BackupName =
        new(@"^\d{8}-\d{6}-\d{3}(-\d+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Sicherungsordner, jüngste zuerst.</summary>
    public List<string> All()
    {
        if (!Directory.Exists(_folder))
            return [];
        return Directory.GetDirectories(_folder)
            .Where(d => BackupName.IsMatch(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Wie viele Sicherungen bei dieser Grenze gelöscht würden.</summary>
    public int CountExcess(int keep) => Math.Max(0, All().Count - Math.Max(1, keep));

    /// <summary>Löscht die ältesten Sicherungen, bis höchstens "keep" übrig sind. Liefert die Anzahl gelöschter.</summary>
    public int Prune(int keep)
    {
        int deleted = 0;
        foreach (var dir in All().Skip(Math.Max(1, keep)))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception)
            {
                // gesperrt o. Ä.: beim nächsten Mal erneut versuchen
            }
        }
        return deleted;
    }

    public PathBackup? Latest()
    {
        var dir = All().FirstOrDefault();
        if (dir is null)
            return null;
        string? Read(string file)
        {
            var p = Path.Combine(dir, file);
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
        }
        return new PathBackup(dir, Directory.GetCreationTime(dir), Read(UserFile) ?? Read(LegacyUserFile), Read(SystemFile));
    }
}
