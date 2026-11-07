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
    private bool _downloading;

    /// <summary>Statusmeldung für das Hauptfenster, wenn etwas installiert wurde.</summary>
    public string? Result { get; private set; }

    public DownloadWindow(Switcher switcher, ToolDefinition tool)
    {
        _switcher = switcher;
        _tool = tool;
        InitializeComponent();
        Title = $"Download {tool.DisplayName}";
        IntroText.Text = tool.Kind == ToolKind.Python
            ? "Official Python versions from python.org (64-bit). They are installed for the current user only, " +
              "without changing file associations and without the py launcher. The new version is added to the PATH behind the active one and can then be selected with \"Activate\"."
            : "Official Git for Windows versions (PortableGit, 64-bit) from GitHub. They are extracted into a separate folder " +
              "without a system installation. The new version is added to the PATH behind the active one and can then be selected with \"Activate\".";
        Loaded += async (_, _) => await LoadAsync();
        Closing += OnClosing;
    }

    private async Task LoadAsync()
    {
        StatusText.Text = "Loading version list …";
        Progress.IsIndeterminate = true;
        try
        {
            var releases = _tool.Kind == ToolKind.Python
                ? await ReleaseCatalog.PythonAsync(_cts.Token)
                : await ReleaseCatalog.GitAsync(_cts.Token);
            _all = releases.Select(Describe).ToList();
            ApplyFilter();
            StatusText.Text = _all.Count == 0 ? "No matching versions were found." : "Please select a version.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "The version list could not be loaded: " + ex.Message;
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
            return new ReleaseRow { Release = r, CanInstall = false, Note = "already in the list" };
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return new ReleaseRow { Release = r, CanInstall = false, Note = $"target folder is not empty: {target}" };

        if (_tool.Kind == ToolKind.Python && Installers.ExistingPythonForMinor(r.SortKey) is { } existing)
        {
            return new ReleaseRow
            {
                Release = r, CanInstall = false,
                Note = $"not possible: Python {r.SortKey.Major}.{r.SortKey.Minor} is already set up by its installer ({existing}). " +
                       "The installer would replace that version instead of adding a second one.",
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
            _downloading = true;
            var progress = new Progress<double>(p =>
            {
                if (!_downloading)
                    return;
                Progress.IsIndeterminate = false;
                Progress.Value = p;
            });
            var inst = await new InstallService(_switcher).DownloadAndInstallAsync(_tool, r, progress, text =>
            {
                StatusText.Text = text;
                // Nur während des Downloads gibt es einen echten Fortschritt; danach läuft die Anzeige unbestimmt.
                _downloading = text.StartsWith("Downloading", StringComparison.Ordinal);
                if (!_downloading)
                    Progress.IsIndeterminate = true;
            }, _cts.Token);

            var nowActive = ReferenceEquals(_switcher.Effective(_tool).Installation, inst);
            Result = nowActive
                ? $"{_tool.DisplayName} {inst.Version} was installed and is active because no other version was active before."
                : $"{_tool.DisplayName} {inst.Version} was installed and added to the PATH behind the active version. Use \"Activate\" to use it.";
            StatusText.Text = Result;
            Progress.IsIndeterminate = false;
            Progress.Value = 1;
            _all = _all.Select(x => Describe(x.Release)).ToList();
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
        }
        finally
        {
            _downloading = false;
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
        CloseButton.Content = busy ? "Cancel" : "Close";
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
            StatusText.Text = "Finishing the current step …";
        }
    }
}
