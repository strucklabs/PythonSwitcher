using System.Diagnostics;

namespace PythonSwitcher.Core;

/// <summary>Eine virtuelle Umgebung (Ordner mit pyvenv.cfg) und die Python-Installation, auf der sie aufbaut.</summary>
public sealed record VenvInfo(string VenvFolder, string BasePython);

public sealed record VenvScanResult(IReadOnlyList<VenvInfo> Dependents, bool Incomplete);

/// <summary>
/// Sucht virtuelle Umgebungen, die auf einer bestimmten Python-Installation aufbauen. Solche Umgebungen
/// funktionieren nicht mehr, wenn diese Installation deinstalliert oder gelöscht wird.
/// </summary>
public sealed class VenvScan
{
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "node_modules", ".git", ".hg", ".svn", "$Recycle.Bin", "site-packages", "__pycache__",
        "Lib", "Include", "Scripts", "bin", "obj", ".cache", ".nuget", ".vscode", "OneDrive",
    };

    private readonly Func<string, IEnumerable<string>> _listDirs;
    private readonly Func<string, string?> _readFile;

    /// <param name="listDirs">Unterordner eines Ordners (Tests: simuliertes Dateisystem).</param>
    /// <param name="readFile">Inhalt einer Datei oder null.</param>
    public VenvScan(Func<string, IEnumerable<string>>? listDirs = null, Func<string, string?>? readFile = null)
    {
        _listDirs = listDirs ?? SafeListDirs;
        _readFile = readFile ?? (f => File.Exists(f) ? File.ReadAllText(f) : null);
    }

    public static IReadOnlyList<string> DefaultSearchRoots() =>
        [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];

    public VenvScanResult FindDependents(string pythonRoot, IEnumerable<string> searchRoots,
        int maxDepth = 6, TimeSpan? timeLimit = null, CancellationToken ct = default)
    {
        var limit = timeLimit ?? TimeSpan.FromSeconds(15);
        var watch = Stopwatch.StartNew();
        var found = new List<VenvInfo>();
        bool incomplete = false;

        var queue = new Queue<(string Dir, int Depth)>(searchRoots.Select(r => (r, 0)));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (watch.Elapsed > limit)
            {
                incomplete = true;
                break;
            }
            var (dir, depth) = queue.Dequeue();

            var cfg = _readFile(Path.Combine(dir, "pyvenv.cfg"));
            if (cfg is not null)
            {
                // Eine Umgebung: Basis-Python auslesen, nicht weiter hineinsuchen
                var home = ReadHome(cfg);
                if (home is not null && PathUtil.IsUnder(home, pythonRoot))
                    found.Add(new VenvInfo(dir, home));
                continue;
            }
            if (depth >= maxDepth)
                continue;
            foreach (var sub in _listDirs(dir))
            {
                if (!SkipNames.Contains(Path.GetFileName(sub)))
                    queue.Enqueue((sub, depth + 1));
            }
        }
        return new VenvScanResult(found, incomplete);
    }

    /// <summary>"home = C:\…\Python310" aus pyvenv.cfg (Ordner der Basis-Installation).</summary>
    internal static string? ReadHome(string cfg)
    {
        foreach (var raw in cfg.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals("home", StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim();
        }
        return null;
    }

    private static IEnumerable<string> SafeListDirs(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            // Verknüpfungen (Junctions) nicht verfolgen: sonst Schleifen und doppelte Treffer
            return info.EnumerateDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint) && !d.Attributes.HasFlag(FileAttributes.System))
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
