namespace PythonSwitcher.Core;

/// <summary>Herunterladen, Installieren und Löschen selbst heruntergeladener Versionen.</summary>
public sealed class InstallService
{
    private readonly Switcher _switcher;

    public InstallService(Switcher switcher)
    {
        _switcher = switcher;
    }

    /// <summary>
    /// Lädt herunter, prüft Prüfsumme und Signatur, installiert bzw. entpackt, nimmt die Version in die Liste auf
    /// und trägt sie im PATH hinter der aktiven Version ein.
    /// </summary>
    public async Task<Installation> DownloadAndInstallAsync(ToolDefinition tool, AvailableRelease release,
        IProgress<double>? progress, Action<string> status, CancellationToken ct)
    {
        var folders = _switcher.Folders;
        var target = Path.Combine(folders.InstallRoot(tool.Kind), release.Version);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new InvalidOperationException($"The target folder is not empty: {target}");
        var file = Path.Combine(folders.Downloads, release.FileName);

        status($"Downloading {release.FileName} …");
        await Downloader.DownloadAsync(release, file, progress, ct);

        status("Checksum OK. Verifying digital signature …");
        await Task.Run(() => SignatureCheck.Verify(file, SignatureCheck.ExpectedSigner(tool.Kind)), ct);

        Installation inst;
        if (tool.Kind == ToolKind.Python)
        {
            status($"Installing Python {release.Version}. This can take a minute …");
            var log = Installers.LogFile($"installation-{release.Version}");
            int rc = await Installers.InstallPythonAsync(file, target, log, CancellationToken.None);
            if (!Installers.IsSuccess(rc))
                throw new InvalidOperationException("The installation failed: " + Installers.DescribePythonExitCode(rc) + $"\nLog: {log}");
            inst = new Installation { Tool = ToolKind.Python, Root = target, Source = InstallSource.Heruntergeladen, InstallerPath = file };
        }
        else
        {
            status($"Extracting Git {release.Version} …");
            int rc = await Installers.ExtractPortableGitAsync(file, target, CancellationToken.None);
            if (rc != 0)
                throw new InvalidOperationException($"Extraction failed (code {rc}).");
            File.Delete(file); // Archiv wird nicht mehr gebraucht
            inst = new Installation { Tool = ToolKind.Git, Root = target, Source = InstallSource.Heruntergeladen };
        }

        inst.Version = await Task.Run(() => VersionProbe.Read(tool, target))
            ?? throw new InvalidOperationException($"The new version cannot be started ({tool.MainExecutablePath(target)}).");
        _switcher.AddDownloaded(inst);

        status("Adding PATH entries …");
        var activeRoot = _switcher.Effective(tool).Installation?.Root;
        var plan = _switcher.Planner(tool).PlanAddInactive(target, activeRoot,
            _switcher.Read(PathScope.Benutzer), _switcher.Read(PathScope.System));
        await Task.Run(() => _switcher.Apply(plan));
        return inst;
    }

    /// <summary>Löscht eine selbst heruntergeladene Version (Python über den aufbewahrten Installer). Fremde Ordner nie.</summary>
    public async Task DeleteDownloadedAsync(Installation inst)
    {
        var folders = _switcher.Folders;
        var ownRoot = folders.InstallRoot(inst.Tool);
        if (inst.Source != InstallSource.Heruntergeladen || !PathUtil.IsUnder(inst.Root, ownRoot) || PathUtil.SameDirectory(inst.Root, ownRoot))
            throw new InvalidOperationException("This folder does not belong to PythonSwitcher and will not be deleted.");

        if (inst.Tool == ToolKind.Python && inst.InstallerPath is { } installer && File.Exists(installer))
        {
            var log = Installers.LogFile($"uninstall-{inst.Version}");
            int rc = await Installers.UninstallPythonAsync(installer, log, CancellationToken.None);
            if (!Installers.IsSuccess(rc))
                throw new InvalidOperationException("The uninstallation failed: " + Installers.DescribePythonExitCode(rc) + $"\nLog: {log}");
            File.Delete(installer);
            Installers.RemoveOrphanedPythonRegistry(Switcher.VersionKey(inst.Version));
        }
        // Reste (z. B. nachträglich installierte Pakete) entfernen.
        if (Directory.Exists(inst.Root))
            await Task.Run(() => Directory.Delete(inst.Root, recursive: true));
    }
}
