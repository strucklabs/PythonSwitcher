namespace PythonSwitcher.Core.VersionCheck;

/// <summary>Bewertet die Python- und Git-Installationen aus der Liste gegen die offiziellen Versionen.</summary>
public static class RuntimeScan
{
    /// <param name="installations">Python-Installationen aus der Liste.</param>
    /// <param name="installers">Versionen mit 64-Bit-Windows-Installer (python.org).</param>
    /// <param name="allVersions">Alle stabilen Versionen, auch nur als Quelltext; darf leer sein.</param>
    /// <param name="winget">winget-Liste für den Aktualisierungs-Hinweis; darf null sein.</param>
    public static List<CheckItem> Python(IEnumerable<Installation> installations, IReadOnlyList<Version> installers,
        IReadOnlyList<Version> allVersions, IReadOnlyList<WingetPackage>? winget)
    {
        var group = ToolScan.GroupName(CheckCategory.Python);
        var result = new List<CheckItem>();
        var minors = new List<(int, int)>();

        foreach (var inst in installations.OrderByDescending(i => Switcher.VersionKey(i.Version)))
        {
            var v = Switcher.VersionKey(inst.Version);
            var name = $"Python {v.Major}.{v.Minor}";
            minors.Add((v.Major, v.Minor));
            var newestInstaller = installers.Where(r => r.Major == v.Major && r.Minor == v.Minor).DefaultIfEmpty().Max();
            var newestSource = allVersions.Where(r => r.Major == v.Major && r.Minor == v.Minor).DefaultIfEmpty().Max();

            string? sourceNote = null;
            if (newestSource is not null && (newestInstaller is null || newestSource > newestInstaller)
                && VersionCompare.Compare(inst.Version, newestSource.ToString()) < 0)
            {
                sourceNote = $"Newer version {newestSource} is only available as source code. This version series no longer " +
                             "gets Windows installers, only security fixes.";
            }

            if (newestInstaller is null)
            {
                result.Add(new CheckItem(CheckCategory.Python, group, name, inst.Version, null, CheckStatus.Unbekannt,
                    "There is no 64-bit Windows installer for this version series on python.org.", sourceNote, inst.Root));
                continue;
            }

            if (VersionCompare.Compare(inst.Version, newestInstaller.ToString()) < 0)
            {
                var wingetId = winget?.FirstOrDefault(p => p.Id.Equals($"Python.Python.{v.Major}.{v.Minor}", StringComparison.OrdinalIgnoreCase)
                    && p.Version == inst.Version)?.Id;
                var hint = inst.Source == InstallSource.Heruntergeladen
                    ? $"Update: run the installer of Python {newestInstaller} (python.org); it replaces this installation."
                    : wingetId is not null
                        ? $"Update: winget upgrade --id {wingetId} or the installer of Python {newestInstaller} (python.org); replaces this installation."
                        : $"Update: run the installer of Python {newestInstaller} (python.org); it replaces this installation.";
                result.Add(new CheckItem(CheckCategory.Python, group, name, inst.Version, newestInstaller.ToString(),
                    CheckStatus.UpdateVerfuegbar, hint, sourceNote, inst.Root));
            }
            else
            {
                result.Add(new CheckItem(CheckCategory.Python, group, name, inst.Version, newestInstaller.ToString(),
                    CheckStatus.Aktuell, "", sourceNote, inst.Root));
            }
        }

        // Neuere Hauptversion, die noch nicht installiert ist
        var newest = installers.DefaultIfEmpty().Max();
        if (newest is not null && minors.Count > 0 && !minors.Contains((newest.Major, newest.Minor))
            && minors.All(m => (m.Item1, m.Item2).CompareTo((newest.Major, newest.Minor)) < 0))
        {
            result.Add(new CheckItem(CheckCategory.Python, group, $"Python {newest.Major}.{newest.Minor}", null, newest.ToString(),
                CheckStatus.Hinweis, "Newer version series, not installed yet. Available on the Python tab via \"Download …\"."));
        }
        return result;
    }

    public static List<CheckItem> Git(IEnumerable<Installation> installations, IReadOnlyList<AvailableRelease> releases,
        IReadOnlyList<WingetPackage>? winget)
    {
        var group = ToolScan.GroupName(CheckCategory.Git);
        var newest = releases.OrderByDescending(r => r.SortKey).FirstOrDefault();
        var result = new List<CheckItem>();
        foreach (var inst in installations.OrderByDescending(i => Switcher.VersionKey(i.Version)))
        {
            if (newest is null)
            {
                result.Add(new CheckItem(CheckCategory.Git, group, "Git", inst.Version, null, CheckStatus.Unbekannt,
                    "The version list of Git for Windows could not be retrieved.", null, inst.Root));
                continue;
            }
            if (VersionCompare.Compare(inst.Version, newest.Version) < 0)
            {
                var viaWinget = winget?.Any(p => p.Id.Equals("Git.Git", StringComparison.OrdinalIgnoreCase)) == true
                                && inst.Source != InstallSource.Heruntergeladen;
                var hint = inst.Source == InstallSource.Heruntergeladen
                    ? "Update: download the new version via \"Download …\" on the Git tab, then remove this one."
                    : viaWinget
                        ? "Update: winget upgrade --id Git.Git or the installer from gitforwindows.org."
                        : "Update: run the installer from gitforwindows.org.";
                result.Add(new CheckItem(CheckCategory.Git, group, "Git", inst.Version, newest.Version,
                    CheckStatus.UpdateVerfuegbar, hint, null, inst.Root));
            }
            else
            {
                result.Add(new CheckItem(CheckCategory.Git, group, "Git", inst.Version, newest.Version, CheckStatus.Aktuell, "", null, inst.Root));
            }
        }
        return result;
    }
}
