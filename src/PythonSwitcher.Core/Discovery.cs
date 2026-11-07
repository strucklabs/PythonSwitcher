using System.Diagnostics;
using Microsoft.Win32;

namespace PythonSwitcher.Core;

public static class VersionProbe
{
    /// <summary>Startet "programm --version" und liest die Version aus der Ausgabe. Null bei Fehler.</summary>
    public static string? Read(ToolDefinition tool, string root, int timeoutMs = 8000)
    {
        var exe = tool.MainExecutablePath(root);
        if (!File.Exists(exe))
            return null;
        try
        {
            var psi = new ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            using var p = Process.Start(psi);
            if (p is null)
                return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }
            // Python 2 schreibt die Version nach stderr.
            return tool.ParseVersion(outTask.Result + "\n" + errTask.Result);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Sucht vorhandene Installationen auf dem Rechner. Liefert Stammordner.</summary>
public sealed class Discovery
{
    private readonly IPathStore _paths;
    private readonly DataFolders _folders;

    public Discovery(IPathStore paths, DataFolders folders)
    {
        _paths = paths;
        _folders = folders;
    }

    public IReadOnlyList<string> FindRoots(ToolDefinition tool)
    {
        var candidates = new List<string>();

        // 1. PATH-Einträge (System und Benutzer)
        foreach (var scope in new[] { PathScope.System, PathScope.Benutzer })
        {
            foreach (var entry in PathUtil.Split(_paths.Read(scope)))
            {
                if (tool.IsAlwaysCompetitor(entry))
                    continue; // z. B. Store-Weiterleitung, keine echte Installation
                var dir = PathUtil.Expand(entry);
                if (File.Exists(Path.Combine(dir, tool.MainExecutable)))
                    AddRoot(candidates, tool.FindRoot(dir, File.Exists));
            }
        }

        // 2. Registry
        if (tool.Kind == ToolKind.Python)
            candidates.AddRange(PythonRegistryRoots());
        else if (tool.Kind == ToolKind.Git)
            candidates.AddRange(GitRegistryRoots());

        // 3. Typische Ordner
        if (tool.Kind == ToolKind.Python)
        {
            var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
            AddChildren(candidates, programs);
        }
        else if (tool.Kind == ToolKind.Git)
        {
            AddRoot(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"));
        }

        // 4. Eigene Downloads
        AddChildren(candidates, _folders.InstallRoot(tool.Kind));

        var result = new List<string>();
        foreach (var c in candidates)
        {
            if (!File.Exists(tool.MainExecutablePath(c)))
                continue;
            if (result.Any(r => PathUtil.SameDirectory(r, c)))
                continue;
            result.Add(c.TrimEnd('\\'));
        }
        return result;
    }

    private static void AddRoot(List<string> list, string? root)
    {
        if (!string.IsNullOrEmpty(root))
            list.Add(root);
    }

    private static void AddChildren(List<string> list, string folder)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var d in Directory.GetDirectories(folder))
            list.Add(d);
    }

    /// <summary>Nach PEP 514 registrierte Python-Installationen (Benutzer und System, 64 und 32 Bit).</summary>
    private static IEnumerable<string> PythonRegistryRoots()
    {
        var result = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var core = baseKey.OpenSubKey(@"Software\Python\PythonCore");
                    if (core is null)
                        continue;
                    foreach (var tag in core.GetSubKeyNames())
                    {
                        using var ip = core.OpenSubKey(tag + @"\InstallPath");
                        if (ip?.GetValue(null) is string path && path.Length > 0)
                            result.Add(path);
                    }
                }
                catch (Exception)
                {
                    // nicht lesbar: überspringen
                }
            }
        }
        return result;
    }

    private static IEnumerable<string> GitRegistryRoots()
    {
        var result = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\GitForWindows");
                if (key?.GetValue("InstallPath") is string path && path.Length > 0)
                    result.Add(path);
            }
            catch (Exception)
            {
            }
        }
        return result;
    }
}
