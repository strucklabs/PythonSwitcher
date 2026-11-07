using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PythonSwitcher.Core;

/// <summary>Schreibt den System-PATH; braucht Adminrechte. False, wenn der Benutzer abgelehnt hat.</summary>
public interface ISystemPathWriter
{
    bool Write(string value);
}

/// <summary>
/// Startet das eigene Programm mit Adminrechten (UAC-Abfrage), das nur den System-PATH schreibt und sich beendet.
/// Aufruf: PythonSwitcher.exe --system-path-schreiben &lt;Datei&gt;
/// </summary>
public sealed class ElevatedSystemPathWriter : ISystemPathWriter
{
    public const string Argument = "--system-path-schreiben";
    private readonly DataFolders _folders;
    private readonly string _exe;

    /// <param name="exe">Programm, das mit Adminrechten gestartet wird; Standard: das laufende Programm.</param>
    public ElevatedSystemPathWriter(DataFolders folders, string? exe = null)
    {
        _folders = folders;
        _exe = exe ?? Environment.ProcessPath!;
    }

    public bool Write(string value)
    {
        Directory.CreateDirectory(_folders.Temp);
        var file = Path.Combine(_folders.Temp, $"system-path-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, value, new UTF8Encoding(false));
        try
        {
            var psi = new ProcessStartInfo(_exe)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            psi.ArgumentList.Add(Argument);
            psi.ArgumentList.Add(file);
            using var p = Process.Start(psi);
            if (p is null)
                return false;
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"The system PATH could not be written (code {p.ExitCode}).");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // UAC-Abfrage abgelehnt
        }
        finally
        {
            try { File.Delete(file); } catch (Exception) { }
        }
    }

    /// <summary>Wird im Admin-Prozess ausgeführt. Liefert den Exit-Code.</summary>
    public static int RunElevated(string file)
    {
        try
        {
            var value = File.ReadAllText(file, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(value))
                return 2; // Sicherheitsnetz: niemals einen leeren System-PATH schreiben
            new RegistryPathStore().Write(PathScope.System, value);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}

/// <summary>Eine Zeile in der Liste: Installation plus Zustand.</summary>
public sealed record InstallationState(Installation Installation, bool IsActive, bool Missing);

/// <summary>Was Windows beim Aufruf des Werkzeugs gerade tatsächlich startet.</summary>
public sealed record EffectiveInfo(string? Entry, PathScope? Scope, Installation? Installation);

/// <summary>Bündelt Einstellungen, Suche und PATH-Änderungen.</summary>
public sealed class Switcher
{
    private readonly IPathStore _paths;
    private readonly ISystemPathWriter _systemWriter;
    private readonly SettingsStore _store;
    private readonly PathBackups _backups;
    private readonly Discovery _discovery;
    private readonly Func<string, bool> _fileExists;

    private readonly Func<string, bool> _directoryExists;

    public Switcher(DataFolders folders, IPathStore paths, ISystemPathWriter systemWriter,
        Func<string, bool>? fileExists = null, Func<string, bool>? directoryExists = null)
    {
        Folders = folders;
        _paths = paths;
        _systemWriter = systemWriter;
        try
        {
            folders.MigrateLegacyNames();
        }
        catch (IOException)
        {
            // Umzug gescheitert (z. B. Datei gesperrt): mit den neuen Namen weiterarbeiten, beim nächsten Start erneut versuchen.
        }
        _store = new SettingsStore(folders);
        _backups = new PathBackups(folders.Backups);
        _discovery = new Discovery(paths, folders);
        _fileExists = fileExists ?? File.Exists;
        _directoryExists = directoryExists ?? Directory.Exists;
        Settings = _store.Load();
    }

    /// <summary>Legt eine Sicherung an und löscht danach die ältesten über der eingestellten Grenze.</summary>
    private void Backup(string? user, string? system)
    {
        _backups.Save(user, system);
        _backups.Prune(Settings.BackupLimit);
    }

    public int BackupCount => _backups.All().Count;

    /// <summary>Wie viele Sicherungen beim Senken auf diese Grenze gelöscht würden.</summary>
    public int BackupsToDelete(int newLimit) => _backups.CountExcess(Math.Clamp(newLimit, AppSettings.MinBackupLimit, AppSettings.MaxBackupLimit));

    /// <summary>Speichert die Grenze und löscht sofort überzählige Sicherungen. Liefert die Anzahl gelöschter.</summary>
    public int SetBackupLimit(int limit)
    {
        Settings.BackupLimit = limit;
        _store.Save(Settings);
        return _backups.Prune(Settings.BackupLimit);
    }

    public DataFolders Folders { get; }
    public AppSettings Settings { get; }

    public PathPlanner Planner(ToolDefinition tool) => new(tool, _fileExists);

    public List<string> Read(PathScope scope) => PathUtil.Split(_paths.Read(scope));

    /// <summary>Sucht nach Installationen, nimmt neue auf und aktualisiert Versionsnummern.</summary>
    public int Scan(ToolDefinition tool)
    {
        int added = 0;
        foreach (var root in _discovery.FindRoots(tool))
        {
            if (Settings.IsIgnored(root) || Settings.Find(tool.Kind, root) is not null)
                continue;
            var version = VersionProbe.Read(tool, root);
            if (version is null)
                continue;
            var own = PathUtil.IsUnder(root, Folders.InstallRoot(tool.Kind));
            Settings.Installations.Add(new Installation
            {
                Tool = tool.Kind,
                Root = root,
                Version = version,
                Source = own ? InstallSource.Heruntergeladen : InstallSource.Gefunden,
            });
            added++;
        }
        foreach (var inst in Settings.For(tool.Kind))
        {
            if (VersionProbe.Read(tool, inst.Root) is { } v)
                inst.Version = v;
        }
        _store.Save(Settings);
        return added;
    }

    /// <summary>Nimmt einen vom Benutzer gewählten Ordner auf. Liefert eine Fehlermeldung oder null.</summary>
    public string? AddManual(ToolDefinition tool, string folder, out Installation? installation)
    {
        installation = null;
        var root = tool.FindRoot(folder, _fileExists);
        if (root is null)
            return $"No {tool.DisplayName} was found in this folder (looked for: {tool.MainExecutable}).";
        if (Settings.Find(tool.Kind, root) is { } existing)
        {
            installation = existing;
            return $"This installation is already in the list ({existing.Version}).";
        }
        var version = VersionProbe.Read(tool, root);
        if (version is null)
            return $"{tool.MainExecutable} was found but could not be started or did not report a version number.";

        Settings.Ignored.RemoveAll(r => PathUtil.SameDirectory(r, root));
        installation = new Installation { Tool = tool.Kind, Root = root, Version = version, Source = InstallSource.Manuell };
        Settings.Installations.Add(installation);
        _store.Save(Settings);
        return null;
    }

    public void AddDownloaded(Installation installation)
    {
        Settings.Installations.RemoveAll(i => i.Tool == installation.Tool && PathUtil.SameDirectory(i.Root, installation.Root));
        Settings.Ignored.RemoveAll(r => PathUtil.SameDirectory(r, installation.Root));
        Settings.Installations.Add(installation);
        _store.Save(Settings);
    }

    /// <summary>
    /// Übernimmt eine frisch ausgelesene Versionsnummer (z. B. nach einem Update von außen).
    /// Liefert true, wenn sich etwas geändert hat.
    /// </summary>
    public bool UpdateVersion(ToolKind tool, string root, string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || Settings.Find(tool, root) is not { } inst || inst.Version == version)
            return false;
        inst.Version = version;
        _store.Save(Settings);
        return true;
    }

    /// <summary>Nimmt eine Installation aus der Liste; die automatische Suche nimmt sie danach nicht wieder auf.</summary>
    public void Unregister(Installation installation)
    {
        Settings.Installations.Remove(installation);
        if (!Settings.IsIgnored(installation.Root))
            Settings.Ignored.Add(installation.Root);
        _store.Save(Settings);
    }

    public EffectiveInfo Effective(ToolDefinition tool)
    {
        var hit = Planner(tool).FindEffective(Read(PathScope.Benutzer), Read(PathScope.System));
        if (hit is null)
            return new EffectiveInfo(null, null, null);
        var inst = Settings.For(tool.Kind).FirstOrDefault(i => PathUtil.IsUnder(hit.Value.Entry, i.Root));
        return new EffectiveInfo(hit.Value.Entry, hit.Value.Scope, inst);
    }

    public List<InstallationState> States(ToolDefinition tool)
    {
        var effective = Effective(tool);
        return Settings.For(tool.Kind)
            .OrderByDescending(i => VersionKey(i.Version))
            .Select(i => new InstallationState(i,
                ReferenceEquals(i, effective.Installation),
                !_fileExists(tool.MainExecutablePath(i.Root))))
            .ToList();
    }

    public static Version VersionKey(string version)
    {
        var digits = new string(version.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return System.Version.TryParse(digits.Contains('.') ? digits : digits + ".0", out var v) ? v : new Version(0, 0);
    }

    /// <summary>
    /// Schreibt einen Plan: erst Sicherung, dann System-PATH (falls nötig, mit UAC), dann Benutzer-PATH.
    /// Lehnt der Benutzer die UAC-Abfrage ab, bleibt alles unverändert. Liefert false bei Ablehnung.
    /// </summary>
    public bool Apply(PathPlan plan)
    {
        if (!plan.UserChanged && !plan.SystemChanged)
            return true;

        var currentUser = _paths.Read(PathScope.Benutzer);
        var currentSystem = _paths.Read(PathScope.System);
        Backup(plan.UserChanged ? currentUser : null, plan.SystemChanged ? currentSystem : null);

        if (plan.SystemChanged)
        {
            var newSystem = PathUtil.Join(plan.System);
            if (string.IsNullOrWhiteSpace(newSystem))
                throw new InvalidOperationException("The new system PATH would be empty. The operation was cancelled.");
            if (!_systemWriter.Write(newSystem))
                return false;
        }
        if (plan.UserChanged)
            _paths.Write(PathScope.Benutzer, PathUtil.Join(plan.User));
        return true;
    }

    public PathBackup? LatestBackup() => _backups.Latest();

    /// <summary>
    /// Plant das Zurückspielen der letzten Sicherung. Ermittelt dabei Einträge, die dadurch neu in den PATH kämen,
    /// deren Ordner es aber nicht mehr gibt (z. B. weil die Version inzwischen gelöscht wurde).
    /// Tote Einträge, die schon jetzt im PATH stehen, zählen nicht dazu.
    /// </summary>
    public RestorePlan? PlanRestore()
    {
        var backup = _backups.Latest();
        if (backup is null)
            return null;
        var currentUser = _paths.Read(PathScope.Benutzer);
        var currentSystem = _paths.Read(PathScope.System);

        List<string> Dead(string? restored, string current)
        {
            if (restored is null)
                return [];
            var now = PathUtil.Split(current);
            return PathUtil.Split(restored)
                .Where(e => !now.Any(n => PathUtil.SameDirectory(n, e)))
                .Where(e => !_directoryExists(PathUtil.Expand(e)))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return new RestorePlan(backup,
            Dead(backup.User, currentUser),
            Dead(backup.System, currentSystem),
            backup.User is not null && backup.User != currentUser,
            backup.System is not null && backup.System != currentSystem);
    }

    /// <summary>
    /// Spielt die letzte Sicherung zurück (vorher wird der aktuelle Stand ebenfalls gesichert).
    /// Mit omitDead werden die im Plan gefundenen toten Einträge weggelassen. Liefert false bei abgelehnter UAC-Abfrage.
    /// </summary>
    public bool RestoreLatest(bool omitDead = true)
    {
        var plan = PlanRestore() ?? throw new InvalidOperationException("There is no backup yet.");
        var user = plan.Backup.User is null ? null : omitDead ? Without(plan.Backup.User, plan.DeadUser) : plan.Backup.User;
        var system = plan.Backup.System is null ? null : omitDead ? Without(plan.Backup.System, plan.DeadSystem) : plan.Backup.System;

        var currentUser = _paths.Read(PathScope.Benutzer);
        var currentSystem = _paths.Read(PathScope.System);
        bool userChanged = user is not null && user != currentUser;
        bool systemChanged = system is not null && system != currentSystem;
        if (!userChanged && !systemChanged)
            return true;
        if (systemChanged && string.IsNullOrWhiteSpace(system))
            throw new InvalidOperationException("The restored system PATH would be empty. The operation was cancelled.");

        // Rohwert zurückschreiben (Schreibweise und leere Einträge bleiben erhalten), daher nicht über Apply/Join.
        Backup(userChanged ? currentUser : null, systemChanged ? currentSystem : null);
        if (systemChanged && !_systemWriter.Write(system!))
            return false;
        if (userChanged)
            _paths.Write(PathScope.Benutzer, user!);
        return true;
    }

    /// <summary>Entfernt die genannten Einträge aus einem rohen PATH-Wert; alles andere bleibt zeichengenau erhalten.</summary>
    internal static string Without(string raw, IReadOnlyCollection<string> remove)
    {
        if (remove.Count == 0)
            return raw;
        return string.Join(";", raw.Split(';').Where(e => !remove.Contains(e)));
    }
}

/// <summary>Was beim Zurückspielen der letzten Sicherung passieren würde.</summary>
public sealed record RestorePlan(
    PathBackup Backup,
    IReadOnlyList<string> DeadUser,
    IReadOnlyList<string> DeadSystem,
    bool UserChanged,
    bool SystemChanged)
{
    public bool HasDead => DeadUser.Count > 0 || DeadSystem.Count > 0;
}
