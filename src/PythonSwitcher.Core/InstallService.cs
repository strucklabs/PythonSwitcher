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
            throw new InvalidOperationException($"Der Zielordner ist nicht leer: {target}");
        var file = Path.Combine(folders.Downloads, release.FileName);

        status($"{release.FileName} wird heruntergeladen …");
        await Downloader.DownloadAsync(release, file, progress, ct);

        status("Prüfsumme stimmt. Digitale Signatur wird geprüft …");
        await Task.Run(() => SignatureCheck.Verify(file, SignatureCheck.ExpectedSigner(tool.Kind)), ct);

        Installation inst;
        if (tool.Kind == ToolKind.Python)
        {
            status($"Python {release.Version} wird installiert. Das kann eine Minute dauern …");
            var log = Installers.LogFile($"installation-{release.Version}");
            int rc = await Installers.InstallPythonAsync(file, target, log, CancellationToken.None);
            if (!Installers.IsSuccess(rc))
                throw new InvalidOperationException("Die Installation ist fehlgeschlagen: " + Installers.DescribePythonExitCode(rc) + $"\nProtokoll: {log}");
            inst = new Installation { Tool = ToolKind.Python, Root = target, Source = InstallSource.Heruntergeladen, InstallerPath = file };
        }
        else
        {
            status($"Git {release.Version} wird entpackt …");
            int rc = await Installers.ExtractPortableGitAsync(file, target, CancellationToken.None);
            if (rc != 0)
                throw new InvalidOperationException($"Das Entpacken ist fehlgeschlagen (Code {rc}).");
            File.Delete(file); // Archiv wird nicht mehr gebraucht
            inst = new Installation { Tool = ToolKind.Git, Root = target, Source = InstallSource.Heruntergeladen };
        }

        inst.Version = await Task.Run(() => VersionProbe.Read(tool, target))
            ?? throw new InvalidOperationException($"Die neue Version lässt sich nicht starten ({tool.MainExecutablePath(target)}).");
        _switcher.AddDownloaded(inst);

        status("PATH-Einträge werden ergänzt …");
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
            throw new InvalidOperationException("Dieser Ordner gehört nicht zu PythonSwitcher und wird nicht gelöscht.");

        if (inst.Tool == ToolKind.Python && inst.InstallerPath is { } installer && File.Exists(installer))
        {
            var log = Installers.LogFile($"deinstallation-{inst.Version}");
            int rc = await Installers.UninstallPythonAsync(installer, log, CancellationToken.None);
            if (!Installers.IsSuccess(rc))
                throw new InvalidOperationException("Die Deinstallation ist fehlgeschlagen: " + Installers.DescribePythonExitCode(rc) + $"\nProtokoll: {log}");
            File.Delete(installer);
            Installers.RemoveOrphanedPythonRegistry(Switcher.VersionKey(inst.Version));
        }
        // Reste (z. B. nachträglich installierte Pakete) entfernen.
        if (Directory.Exists(inst.Root))
            await Task.Run(() => Directory.Delete(inst.Root, recursive: true));
    }
}
