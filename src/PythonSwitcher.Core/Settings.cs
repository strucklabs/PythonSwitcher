using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PythonSwitcher.Core;

public enum InstallSource
{
    /// <summary>Automatisch auf dem Rechner gefunden.</summary>
    Gefunden,
    /// <summary>Vom Benutzer als Ordner hinzugefügt.</summary>
    Manuell,
    /// <summary>Vom Tool heruntergeladen und installiert – nur diese darf das Tool löschen.</summary>
    Heruntergeladen,
}

public sealed class Installation
{
    public ToolKind Tool { get; set; }
    public string Root { get; set; } = "";
    public string Version { get; set; } = "";
    public InstallSource Source { get; set; }

    /// <summary>Bei heruntergeladenen Python-Versionen: aufbewahrter Installer, mit dem sauber deinstalliert wird.</summary>
    public string? InstallerPath { get; set; }
}

public sealed class AppSettings
{
    public List<Installation> Installations { get; set; } = [];

    /// <summary>Ausgetragene Stammordner; die automatische Suche nimmt sie nicht wieder auf.</summary>
    public List<string> Ignored { get; set; } = [];

    public IEnumerable<Installation> For(ToolKind tool) => Installations.Where(i => i.Tool == tool);

    public Installation? Find(ToolKind tool, string root) =>
        Installations.FirstOrDefault(i => i.Tool == tool && PathUtil.SameDirectory(i.Root, root));

    public bool IsIgnored(string root) => Ignored.Any(r => PathUtil.SameDirectory(r, root));
}

/// <summary>Speicherorte des Tools unter %LOCALAPPDATA%\PythonSwitcher.</summary>
public sealed class DataFolders
{
    public DataFolders(string root)
    {
        Root = root;
    }

    public static DataFolders Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PythonSwitcher"));

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "einstellungen.json");
    public string Backups => Path.Combine(Root, "Sicherungen");
    public string Downloads => Path.Combine(Root, "Downloads");
    public string Temp => Path.Combine(Root, "Temp");
    public string InstallRoot(ToolKind tool) => Path.Combine(Root, tool.ToString().ToLowerInvariant());
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DataFolders _folders;

    public SettingsStore(DataFolders folders)
    {
        _folders = folders;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_folders.SettingsFile))
            return new AppSettings();
        try
        {
            var json = File.ReadAllText(_folders.SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Defekte Datei nicht überschreiben, sondern beiseitelegen.
            var broken = _folders.SettingsFile + ".defekt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(_folders.SettingsFile, broken, overwrite: true);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_folders.Root);
        var tmp = _folders.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, _folders.SettingsFile, overwrite: true);
    }
}
