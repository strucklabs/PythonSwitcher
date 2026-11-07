using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PythonSwitcher.Core;

namespace PythonSwitcher;

public sealed class ReleaseRow
{
    public required AvailableRelease Release { get; init; }
    public required string Note { get; init; }
    public required bool CanInstall { get; init; }
    public string Version => Release.Version;
    public string SizeText => Release.Size > 0 ? $"{Release.Size / 1024.0 / 1024.0:0} MB" : "";
}

public partial class DownloadWindow : Window
{
    private readonly Switcher _switcher;
    private readonly ToolDefinition _tool;
    private readonly CancellationTokenSource _cts = new();
    private List<ReleaseRow> _all = [];
    private bool _busy;

    /// <summary>Statusmeldung für das Hauptfenster, wenn etwas installiert wurde.</summary>
    public string? Result { get; private set; }

    public DownloadWindow(Switcher switcher, ToolDefinition tool)
    {
        _switcher = switcher;
        _tool = tool;
        InitializeComponent();
        Title = $"{tool.DisplayName} herunterladen";
        IntroText.Text = tool.Kind == ToolKind.Python
            ? "Offizielle Python-Versionen von python.org (64 Bit). Die Installation erfolgt nur für den aktuellen Benutzer, " +
              "ohne Änderungen an Dateizuordnungen und ohne den py-Launcher. Die neue Version wird im PATH hinter der aktiven eingetragen und ist danach mit „Aktivieren“ wählbar."
            : "Offizielle Git-für-Windows-Versionen (PortableGit, 64 Bit) von GitHub. Sie werden in einen eigenen Ordner entpackt, " +
              "ohne Installation im System. Die neue Version wird im PATH hinter der aktiven eingetragen und ist danach mit „Aktivieren“ wählbar.";
        Loaded += async (_, _) => await LoadAsync();
        Closing += OnClosing;
    }

    private async Task LoadAsync()
    {
        StatusText.Text = "Versionsliste wird geladen …";
        Progress.IsIndeterminate = true;
        try
        {
            var releases = _tool.Kind == ToolKind.Python
                ? await ReleaseCatalog.PythonAsync(_cts.Token)
                : await ReleaseCatalog.GitAsync(_cts.Token);
            _all = releases.Select(Describe).ToList();
            ApplyFilter();
            StatusText.Text = _all.Count == 0 ? "Es wurden keine passenden Versionen gefunden." : "Bitte eine Version wählen.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "Die Versionsliste konnte nicht geladen werden: " + ex.Message;
        }
        finally
        {
            Progress.IsIndeterminate = false;
        }
    }

    private ReleaseRow Describe(AvailableRelease r)
    {
        var target = Path.Combine(_switcher.Folders.InstallRoot(_tool.Kind), r.Version);
        var sameVersion = _switcher.Settings.For(_tool.Kind)
            .FirstOrDefault(i => i.Version == r.Version || PathUtil.SameDirectory(i.Root, target));
        if (sameVersion is not null)
            return new ReleaseRow { Release = r, CanInstall = false, Note = "bereits in der Liste" };
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return new ReleaseRow { Release = r, CanInstall = false, Note = $"Zielordner ist nicht leer: {target}" };

        if (_tool.Kind == ToolKind.Python && Installers.ExistingPythonForMinor(r.SortKey) is { } existing)
        {
            return new ReleaseRow
            {
                Release = r, CanInstall = false,
                Note = $"nicht möglich: Python {r.SortKey.Major}.{r.SortKey.Minor} ist bereits per Installer eingerichtet ({existing}). " +
                       "Der Installer würde diese Version ersetzen statt eine zweite anzulegen.",
            };
        }
        return new ReleaseRow { Release = r, CanInstall = true, Note = "" };
    }

    private void ApplyFilter()
    {
        IEnumerable<ReleaseRow> rows = _all;
        if (ShowAll.IsChecked != true)
        {
            rows = _all.GroupBy(r => _tool.Kind == ToolKind.Python
                    ? $"{r.Release.SortKey.Major}.{r.Release.SortKey.Minor}"
                    : $"{r.Release.SortKey.Major}.{r.Release.SortKey.Minor}.{r.Release.SortKey.Build}")
                .Select(g => g.First()); // Liste ist schon absteigend sortiert
            if (_tool.Kind == ToolKind.Git)
                rows = rows.Take(10);
        }
        List.ItemsSource = rows.ToList();
    }

    private void ShowAll_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InstallButton.IsEnabled = !_busy && List.SelectedItem is ReleaseRow { CanInstall: true };
        if (List.SelectedItem is ReleaseRow { CanInstall: false } row)
            StatusText.Text = row.Note;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not ReleaseRow { CanInstall: true } row)
            return;
        var r = row.Release;
        SetBusy(true);
        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress.IsIndeterminate = false;
                Progress.Value = p;
            });
            var inst = await new InstallService(_switcher).DownloadAndInstallAsync(_tool, r, progress, text =>
            {
                StatusText.Text = text;
                if (!text.Contains("heruntergeladen"))
                    Progress.IsIndeterminate = true;
            }, _cts.Token);

            var nowActive = ReferenceEquals(_switcher.Effective(_tool).Installation, inst);
            Result = nowActive
                ? $"{_tool.DisplayName} {inst.Version} wurde installiert und ist aktiv, weil vorher keine andere Version aktiv war."
                : $"{_tool.DisplayName} {inst.Version} wurde installiert und im PATH hinter der aktiven Version eingetragen. Mit „Aktivieren“ wird sie verwendet.";
            StatusText.Text = Result;
            Progress.IsIndeterminate = false;
            Progress.Value = 1;
            _all = _all.Select(x => Describe(x.Release)).ToList();
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            Progress.IsIndeterminate = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallButton.IsEnabled = !busy && List.SelectedItem is ReleaseRow { CanInstall: true };
        List.IsEnabled = !busy;
        ShowAll.IsEnabled = !busy;
        CloseButton.Content = busy ? "Abbrechen" : "Schließen";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            _cts.Cancel();
        else
            Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            // Während Installation/Entpacken nicht schließen, damit nichts halb fertig zurückbleibt.
            e.Cancel = true;
            _cts.Cancel();
            StatusText.Text = "Der laufende Schritt wird noch beendet …";
        }
    }
}
