namespace PythonSwitcher.Core.VersionCheck;

/// <summary>Abgerufene Versionslisten der letzten Prüfung; eine Einzelprüfung verwendet sie weiter.</summary>
public sealed class CheckContext
{
    public List<WingetPackage>? Winget { get; set; }
    public List<Version> PyInstallers { get; set; } = [];
    public List<Version> PyAll { get; set; } = [];
    public List<AvailableRelease> GitReleases { get; set; } = [];
}

/// <summary>Führt den Versionscheck aus – gesamt oder für eine einzelne Zeile. Liest nur, verändert nichts.</summary>
public sealed class VersionChecker
{
    private readonly IPathStore _paths;
    private readonly Func<CancellationToken, Task<List<WingetPackage>?>> _winget;
    private readonly Func<ToolDefinition, string, string?> _probe;
    private readonly PipScan _pip;
    private readonly Func<IReadOnlyList<string>, ToolScan> _toolScan;
    private readonly Func<CancellationToken, Task<CheckContext>> _catalogs;
    private readonly Func<string, bool> _fileExists;

    /// <param name="winget">Ersatz für "winget list" (Tests).</param>
    /// <param name="probe">Ersatz für das Auslesen der Version per "--version" (Tests).</param>
    /// <param name="pip">Ersatz für die pip-Abfragen (Tests).</param>
    /// <param name="toolScan">Ersatz für die Werkzeugerkennung, erhält die Python-Ordner (Tests).</param>
    /// <param name="catalogs">Ersatz für die Versionslisten von python.org und GitHub (Tests); PyInstallers leer = nicht abrufbar.</param>
    /// <param name="fileExists">Ersatz für die Dateiprüfung (Tests).</param>
    public VersionChecker(IPathStore paths,
        Func<CancellationToken, Task<List<WingetPackage>?>>? winget = null,
        Func<ToolDefinition, string, string?>? probe = null,
        PipScan? pip = null,
        Func<IReadOnlyList<string>, ToolScan>? toolScan = null,
        Func<CancellationToken, Task<CheckContext>>? catalogs = null,
        Func<string, bool>? fileExists = null)
    {
        _paths = paths;
        _winget = winget ?? Winget.ListAsync;
        _probe = probe ?? ((tool, root) => VersionProbe.Read(tool, root));
        _pip = pip ?? new PipScan();
        _toolScan = toolScan ?? DefaultToolScan;
        _catalogs = catalogs ?? LoadCatalogsAsync;
        _fileExists = fileExists ?? File.Exists;
    }

    private static async Task<CheckContext> LoadCatalogsAsync(CancellationToken ct) => new()
    {
        PyInstallers = await Try(async () => (await ReleaseCatalog.PythonAsync(ct)).Select(r => r.SortKey).ToList(), ct) ?? [],
        PyAll = await Try(() => ReleaseCatalog.PythonAllVersionsAsync(ct), ct) ?? [],
        GitReleases = await Try(() => ReleaseCatalog.GitAsync(ct), ct) ?? [],
    };

    public CheckContext? Context { get; set; }

    private ToolScan DefaultToolScan(IReadOnlyList<string> pythonRoots)
    {
        var env = new SystemEnvironment(_paths);
        return new ToolScan(env.FindCommand, (exe, args) => env.Run(exe, args), SystemEnvironment.PyPiLatestAsync, pythonRoots,
            env.FindAllCommands, SystemEnvironment.FileVersion);
    }

    /// <summary>Kopie der Installationen mit frisch ausgelesener Version (z. B. nach einem Update von außen).</summary>
    private List<Installation> Fresh(IEnumerable<Installation> installations, ToolKind kind)
    {
        var tool = ToolDefinition.For(kind);
        return installations
            .Where(i => i.Tool == kind && _fileExists(tool.MainExecutablePath(i.Root)))
            .Select(i => new Installation
            {
                Tool = i.Tool, Root = i.Root, Source = i.Source, InstallerPath = i.InstallerPath,
                Version = _probe(tool, i.Root) ?? i.Version,
            })
            .ToList();
    }

    /// <param name="installations">Momentaufnahme der Liste (wird nicht verändert).</param>
    public async Task<List<CheckItem>> RunAsync(IReadOnlyList<Installation> installations, IProgress<string> progress, CancellationToken ct)
    {
        var result = new List<CheckItem>();

        progress.Report("Reading installed Python and Git versions …");
        var pythons = Fresh(installations, ToolKind.Python);
        var gits = Fresh(installations, ToolKind.Git);

        progress.Report("Querying Python and Git versions from python.org and GitHub …");
        var ctx = await _catalogs(ct);

        progress.Report("Reading the list of installed programs (winget) …");
        ctx.Winget = await _winget(ct);

        if (ctx.PyInstallers.Count == 0)
        {
            result.Add(new CheckItem(CheckCategory.Python, ToolScan.GroupName(CheckCategory.Python), "python.org", null, null,
                CheckStatus.Unbekannt, "The version list from python.org could not be retrieved (no connection?)."));
        }
        else
        {
            result.AddRange(RuntimeScan.Python(pythons, ctx.PyInstallers, ctx.PyAll, ctx.Winget));
        }
        result.AddRange(RuntimeScan.Git(gits, ctx.GitReleases, ctx.Winget));

        progress.Report("Detecting tools, editors and languages …");
        result.AddRange(await _toolScan(pythons.Select(p => p.Root).ToList()).ScanAsync(ctx.Winget, Catalog.Entries, ct));
        if (ctx.Winget is null)
        {
            result.Add(new CheckItem(CheckCategory.PythonWerkzeug, ToolScan.GroupName(CheckCategory.PythonWerkzeug), "winget", null, null,
                CheckStatus.Hinweis,
                "winget (App Installer by Microsoft) was not found or did not respond. Editors and languages " +
                "are therefore only partially detected, and latest versions are missing."));
        }

        for (int i = 0; i < pythons.Count; i++)
        {
            progress.Report($"Checking pip packages of Python {pythons[i].Version} ({i + 1} of {pythons.Count}) …");
            result.AddRange(await _pip.ScanAsync(pythons[i], ct));
        }

        Context = ctx;
        progress.Report("Done.");
        return result;
    }

    /// <summary>
    /// Prüft eine einzelne Zeile neu. Liefert die Ersatzzeilen: meist eine, bei Werkzeugen mit mehreren
    /// Installationen mehrere, bei der pip-Übersicht alle Zeilen dieser Python-Version.
    /// </summary>
    public async Task<List<CheckItem>> RecheckAsync(CheckItem item, IReadOnlyList<Installation> installations, CancellationToken ct)
    {
        var target = item.Target ?? throw new InvalidOperationException("This entry cannot be checked individually.");
        var ctx = Context ??= new CheckContext();

        switch (target.Kind)
        {
            case TargetKind.Runtime:
            {
                var tool = ToolDefinition.For(target.Tool!.Value);
                var inst = installations.FirstOrDefault(i => i.Tool == tool.Kind && PathUtil.SameDirectory(i.Root, target.Root!));
                var version = await Task.Run(() => _probe(tool, target.Root!), ct);
                if (version is null)
                {
                    return [item with
                    {
                        Installed = null, Status = CheckStatus.Unbekannt, UpdateCommand = null,
                        Hint = "The program could not be started. It may have been uninstalled or moved.",
                    }];
                }
                ctx.Winget = await _winget(ct) ?? ctx.Winget;
                var fresh = new Installation
                {
                    Tool = tool.Kind, Root = target.Root!, Version = version,
                    Source = inst?.Source ?? InstallSource.Gefunden, InstallerPath = inst?.InstallerPath,
                };
                var rows = tool.Kind == ToolKind.Python
                    ? RuntimeScan.Python([fresh], ctx.PyInstallers, ctx.PyAll, ctx.Winget)
                    : RuntimeScan.Git([fresh], ctx.GitReleases, ctx.Winget);
                return rows.Where(r => r.Target?.Kind == TargetKind.Runtime).ToList();
            }

            case TargetKind.Tool:
            {
                var entry = Catalog.Entries.First(e => e.Name == target.CatalogName);
                ctx.Winget = await _winget(ct) ?? ctx.Winget;
                var roots = installations.Where(i => i.Tool == ToolKind.Python).Select(i => i.Root).ToList();
                var rows = await _toolScan(roots).ScanAsync(ctx.Winget, [entry], ct);
                if (rows.Count > 0)
                    return rows;
                return [item with
                {
                    Installed = null, Latest = null, Status = CheckStatus.Hinweis, UpdateCommand = null,
                    Hint = "No longer found. It may have been uninstalled.",
                }];
            }

            case TargetKind.PipPackage:
            case TargetKind.PipSummary:
            case TargetKind.PipCheck:
            {
                var inst = Fresh(installations.Where(i => PathUtil.SameDirectory(i.Root, target.Root!)), ToolKind.Python).FirstOrDefault();
                if (inst is null)
                {
                    return [item with
                    {
                        Status = CheckStatus.Unbekannt, UpdateCommand = null,
                        Hint = "This Python installation is no longer available.",
                    }];
                }
                return target.Kind switch
                {
                    TargetKind.PipPackage => [await _pip.RecheckPackageAsync(item, inst, ct)],
                    TargetKind.PipCheck => await _pip.DependencyRowsAsync(inst, ct),
                    _ => await _pip.ScanAsync(inst, ct),
                };
            }

            default:
                throw new InvalidOperationException("This entry cannot be checked individually.");
        }
    }

    /// <summary>
    /// Trockenlauf für eine Paketzeile oder die Übersicht (alle Pakete mit neuerer Version) einer Python-Version.
    /// Es wird nichts installiert.
    /// </summary>
    public async Task<PipScan.DryRunResult> DryRunAsync(CheckItem item, IEnumerable<CheckItem> groupRows,
        IReadOnlyList<Installation> installations, CancellationToken ct)
    {
        var target = item.Target ?? throw new InvalidOperationException("No dry run is available for this entry.");
        var inst = Fresh(installations.Where(i => PathUtil.SameDirectory(i.Root, target.Root ?? "")), ToolKind.Python).FirstOrDefault()
                   ?? throw new InvalidOperationException("This Python installation is no longer available.");
        var wanted = target.Kind == TargetKind.PipPackage
            ? new Dictionary<string, string?> { [target.Package ?? item.Name] = item.Latest }
            : groupRows
                .Where(r => r.Target?.Kind == TargetKind.PipPackage && r.Latest is not null
                            && VersionCompare.Compare(r.Installed, r.Latest) < 0)
                .ToDictionary(r => r.Target!.Package ?? r.Name, r => r.Latest);
        if (wanted.Count == 0)
            return new PipScan.DryRunResult([], new Dictionary<string, string>(), "There is nothing to update.");
        return await _pip.DryRunAsync(inst, wanted, ct);
    }

    private static async Task<T?> Try<T>(Func<Task<T>> f, CancellationToken ct) where T : class
    {
        try
        {
            return await f();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
