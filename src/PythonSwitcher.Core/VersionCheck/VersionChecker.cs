namespace PythonSwitcher.Core.VersionCheck;

/// <summary>Führt den gesamten Versionscheck aus. Liest nur, verändert nichts.</summary>
public sealed class VersionChecker
{
    private readonly IPathStore _paths;

    public VersionChecker(IPathStore paths)
    {
        _paths = paths;
    }

    /// <param name="installations">Momentaufnahme der Liste (wird nicht verändert).</param>
    public async Task<List<CheckItem>> RunAsync(IReadOnlyList<Installation> installations, IProgress<string> progress, CancellationToken ct)
    {
        var result = new List<CheckItem>();
        var pythons = installations.Where(i => i.Tool == ToolKind.Python && File.Exists(ToolDefinition.Python.MainExecutablePath(i.Root))).ToList();
        var gits = installations.Where(i => i.Tool == ToolKind.Git && File.Exists(ToolDefinition.Git.MainExecutablePath(i.Root))).ToList();

        progress.Report("Reading the list of installed programs (winget) …");
        var winget = await Winget.ListAsync(ct);

        progress.Report("Querying Python versions from python.org …");
        var pyInstallers = await Try(async () => (await ReleaseCatalog.PythonAsync(ct)).Select(r => r.SortKey).ToList());
        var pyAll = await Try(() => ReleaseCatalog.PythonAllVersionsAsync(ct)) ?? [];
        if (pyInstallers is null)
            result.Add(new CheckItem(CheckCategory.Python, ToolScan.GroupName(CheckCategory.Python), "python.org", null, null,
                CheckStatus.Unbekannt, "The version list from python.org could not be retrieved (no connection?)."));
        else
            result.AddRange(RuntimeScan.Python(pythons, pyInstallers, pyAll, winget));

        progress.Report("Querying Git versions …");
        var gitReleases = await Try(() => ReleaseCatalog.GitAsync(ct)) ?? [];
        result.AddRange(RuntimeScan.Git(gits, gitReleases, winget));

        progress.Report("Detecting tools, editors and languages …");
        var env = new SystemEnvironment(_paths);
        var tools = new ToolScan(env.FindCommand, (exe, args) => env.Run(exe, args), SystemEnvironment.PyPiLatestAsync,
            pythons.Select(p => p.Root).ToList());
        result.AddRange(await tools.ScanAsync(winget, Catalog.Entries, ct));
        if (winget is null)
        {
            result.Add(new CheckItem(CheckCategory.PythonWerkzeug, ToolScan.GroupName(CheckCategory.PythonWerkzeug), "winget", null, null,
                CheckStatus.Hinweis,
                "winget (App Installer by Microsoft) was not found or did not respond. Editors and languages " +
                "are therefore only partially detected, and latest versions are missing."));
        }

        var pip = new PipScan();
        for (int i = 0; i < pythons.Count; i++)
        {
            progress.Report($"Checking pip packages of Python {pythons[i].Version} ({i + 1} of {pythons.Count}) …");
            result.AddRange(await pip.ScanAsync(pythons[i], ct));
        }

        progress.Report("Done.");
        return result;

        async Task<T?> Try<T>(Func<Task<T>> f) where T : class
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
}
