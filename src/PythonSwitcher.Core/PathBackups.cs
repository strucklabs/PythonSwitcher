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
    private const string UserFile = "Benutzer.txt";
    private const string SystemFile = "System.txt";
    private readonly string _folder;

    public PathBackups(string folder)
    {
        _folder = folder;
    }

    public PathBackup Save(string? user, string? system)
    {
        if (user is null && system is null)
            throw new ArgumentException("Es muss mindestens ein Wert gesichert werden.");

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

    public PathBackup? Latest()
    {
        if (!Directory.Exists(_folder))
            return null;
        var dir = Directory.GetDirectories(_folder)
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .FirstOrDefault();
        if (dir is null)
            return null;
        string? Read(string file)
        {
            var p = Path.Combine(dir, file);
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
        }
        return new PathBackup(dir, Directory.GetCreationTime(dir), Read(UserFile), Read(SystemFile));
    }
}
