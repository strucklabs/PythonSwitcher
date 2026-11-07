using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PythonSwitcher.Core;

namespace PythonSwitcher;

/// <summary>Eine Zeile der Liste.</summary>
public sealed class InstallationRow(InstallationState state)
{
    public Installation Installation => state.Installation;
    public bool IsActive => state.IsActive;
    public bool Missing => state.Missing;
    public string StatusText => state.Missing ? "missing" : state.IsActive ? "● active" : "";
    public string Version => state.Installation.Version;
    public string Root => state.Installation.Root;
    public string SourceText => state.Installation.Source switch
    {
        InstallSource.Gefunden => "found",
        InstallSource.Manuell => "added",
        InstallSource.Heruntergeladen => "downloaded",
        _ => "",
    };
}

public partial class ToolPanel : UserControl
{
    private readonly Switcher _switcher;
    private readonly ToolDefinition _tool;
    private readonly MainWindow _main;

    public ToolPanel(Switcher switcher, ToolDefinition tool, MainWindow main)
    {
        _switcher = switcher;
        _tool = tool;
        _main = main;
        InitializeComponent();
        Refresh();
    }

    private InstallationRow? Selected => List.SelectedItem as InstallationRow;

    public void Refresh()
    {
        var selectedRoot = Selected?.Root;
        var rows = _switcher.States(_tool).Select(s => new InstallationRow(s)).ToList();
        List.ItemsSource = rows;
        List.SelectedItem = rows.FirstOrDefault(r => r.Root == selectedRoot);

        var eff = _switcher.Effective(_tool);
        WarningBox.Visibility = Visibility.Collapsed;
        if (eff.Entry is null)
        {
            ActiveText.Text = $"No {_tool.DisplayName} is active at the moment.";
            ActiveDetail.Text = $"Windows does not find \"{Path.GetFileNameWithoutExtension(_tool.MainExecutable)}\" via any PATH entry.";
        }
        else if (eff.Installation is not null)
        {
            ActiveText.Text = $"Active: {_tool.DisplayName} {eff.Installation.Version}";
            ActiveDetail.Text = $"{eff.Installation.Root}   ·   via the {ScopeName(eff.Scope)}";
        }
        else if (_tool.IsAlwaysCompetitor(eff.Entry))
        {
            ActiveText.Text = $"No {_tool.DisplayName} is active at the moment.";
            ActiveDetail.Text = "\"python\" currently only opens the Microsoft Store redirect (a Windows placeholder, not a real Python).";
        }
        else
        {
            ActiveText.Text = $"Active: a {_tool.DisplayName} installation that is not in the list";
            ActiveDetail.Text = $"{eff.Entry}   ·   via the {ScopeName(eff.Scope)}";
            ShowWarning("Use \"Add folder …\" to add this installation to the list.");
        }
        UpdateButtons();
    }

    private static string ScopeName(PathScope? scope) => scope == PathScope.System ? "system PATH" : "user PATH";

    private void ShowWarning(string text)
    {
        WarningText.Text = text;
        WarningBox.Visibility = Visibility.Visible;
    }

    private void UpdateButtons()
    {
        var sel = Selected;
        ActivateButton.IsEnabled = sel is not null && !sel.Missing && !sel.IsActive;
        RemoveButton.IsEnabled = sel is not null;
        OpenButton.IsEnabled = sel is not null && !sel.Missing;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ActivateButton.IsEnabled)
            Activate_Click(sender, e);
    }

    /// <summary>Sperrt das Fenster während einer Aktion und meldet Fehler verständlich.</summary>
    private async Task RunBusy(Func<Task> action)
    {
        _main.SetBusy(true);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(_main, ex.Message, "PythonSwitcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _main.SetBusy(false);
            _main.RefreshAll();
        }
    }

    public async Task ScanAsync(bool initial)
    {
        await RunBusy(async () =>
        {
            _main.SetStatus($"Searching for {_tool.DisplayName} installations …");
            int added = await Task.Run(() => _switcher.Scan(_tool));
            if (!initial || added > 0)
                _main.SetStatus(added switch
                {
                    0 => $"Search finished: no new {_tool.DisplayName} installations found.",
                    1 => $"Search finished: 1 new {_tool.DisplayName} installation found.",
                    _ => $"Search finished: {added} new {_tool.DisplayName} installations found.",
                });
            else
                _main.SetStatus("Ready.");
        });
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync(initial: false);

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        var inst = row.Installation;
        await RunBusy(async () =>
        {
            var planner = _switcher.Planner(_tool);
            var user = _switcher.Read(PathScope.Benutzer);
            var system = _switcher.Read(PathScope.System);
            var plan = planner.PlanActivation(inst.Root, user, system, allowSystem: false);
            bool systemDeclined = false;

            if (plan.SystemConflicts.Count > 0)
            {
                var answer = MessageBox.Show(_main,
                    $"The system PATH contains another {_tool.DisplayName} entry, which always takes precedence over the user PATH:\n\n" +
                    string.Join("\n", plan.SystemConflicts.Select(c => "   " + c)) +
                    $"\n\nFor {_tool.DisplayName} {inst.Version} to actually be used, the system PATH has to be reordered. It applies to all users of this computer and can only be changed with administrator rights. A backup is created first.\n\n" +
                    "Yes: reorder the system PATH (Windows asks for administrator rights)\n" +
                    "No: change only the user PATH (the other version then stays active)\n" +
                    "Cancel: change nothing",
                    "Change system PATH?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel)
                    return;
                if (answer == MessageBoxResult.Yes)
                    plan = planner.PlanActivation(inst.Root, user, system, allowSystem: true);
                else
                    systemDeclined = true;
            }

            if (!plan.UserChanged && !plan.SystemChanged)
            {
                _main.SetStatus($"{_tool.DisplayName} {inst.Version} is already first. No change was needed.");
                return;
            }

            if (!await Task.Run(() => _switcher.Apply(plan)))
            {
                _main.SetStatus("The administrator prompt was declined. Nothing was changed.");
                return;
            }

            var eff = _switcher.Effective(_tool);
            if (ReferenceEquals(eff.Installation, inst))
                _main.SetStatus($"{_tool.DisplayName} {inst.Version} is now active. This applies to newly opened terminals and programs.");
            else if (systemDeclined)
                _main.SetStatus($"The user PATH was changed, but the system PATH entry still takes precedence. Still active: {eff.Entry}");
            else
                _main.SetStatus($"The PATH was changed, but Windows finds this first: {eff.Entry}");
        });
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"Select the folder of a {_tool.DisplayName} installation (the folder containing {_tool.MainExecutable})",
        };
        if (dialog.ShowDialog(_main) != true)
            return;
        await RunBusy(async () =>
        {
            Installation? inst = null;
            var error = await Task.Run(() => _switcher.AddManual(_tool, dialog.FolderName, out inst));
            if (error is not null)
                MessageBox.Show(_main, error, "Add folder", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                _main.SetStatus($"{_tool.DisplayName} {inst!.Version} was added. Use \"Activate\" to use it.");
        });
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row && Directory.Exists(row.Root))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{row.Root}\"") { UseShellExecute = true });
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var window = new DownloadWindow(_switcher, _tool) { Owner = _main };
        window.ShowDialog();
        if (window.Result is { } message)
            _main.SetStatus(message);
        _main.RefreshAll();
        await Task.CompletedTask;
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        var inst = row.Installation;
        var name = $"{_tool.DisplayName} {inst.Version}";

        if (row.IsActive)
        {
            MessageBox.Show(_main, $"{name} is currently active. Please activate another version first and then remove this one.",
                "Remove", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var own = inst.Source == InstallSource.Heruntergeladen;
        var intro = MessageBox.Show(_main,
            $"Remove {name} from the list?\n\n{inst.Root}\n\n" +
            (own
                ? "This version was downloaded with PythonSwitcher. In the next step you can also delete it."
                : "The files are kept. To uninstall the version completely, use Windows Settings > Apps."),
            "Remove", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (intro != MessageBoxResult.OK)
            return;

        bool deleteFiles = false;
        if (own && Directory.Exists(inst.Root))
        {
            var del = MessageBox.Show(_main,
                $"Also delete the files of {name}?\n\n{inst.Root}\n\n" +
                "Packages installed later are lost as well. This cannot be undone. " +
                "The PATH entries of this version are removed too.",
                "Delete files?", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (del == MessageBoxResult.Cancel)
                return;
            deleteFiles = del == MessageBoxResult.Yes;
        }

        await RunBusy(async () =>
        {
            var planner = _switcher.Planner(_tool);
            var user = _switcher.Read(PathScope.Benutzer);
            var system = _switcher.Read(PathScope.System);
            var plan = planner.PlanRemoval(inst.Root, user, system, allowSystem: false);
            bool hasEntries = plan.UserChanged || plan.SystemConflicts.Count > 0;

            bool removeEntries = deleteFiles;
            if (hasEntries && !deleteFiles)
            {
                var answer = MessageBox.Show(_main,
                    $"{name} has entries in the PATH. Remove them as well?\n\n" +
                    "Yes: remove the entries (the version can then no longer be reached via the PATH)\n" +
                    "No: keep the entries",
                    "PATH entries", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel)
                    return;
                removeEntries = answer == MessageBoxResult.Yes;
            }

            if (removeEntries && hasEntries)
            {
                if (plan.SystemConflicts.Count > 0)
                {
                    var sys = MessageBox.Show(_main,
                        "Some entries are in the system PATH:\n\n" + string.Join("\n", plan.SystemConflicts.Select(c => "   " + c)) +
                        "\n\nRemove these as well? Windows will ask for administrator rights.",
                        "Change system PATH?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (sys == MessageBoxResult.Yes)
                        plan = planner.PlanRemoval(inst.Root, user, system, allowSystem: true);
                }
                if (!await Task.Run(() => _switcher.Apply(plan)))
                {
                    _main.SetStatus("The administrator prompt was declined. Nothing was changed.");
                    return;
                }
            }

            if (deleteFiles)
            {
                _main.SetStatus($"Deleting {name} …");
                await new InstallService(_switcher).DeleteDownloadedAsync(inst);
            }

            _switcher.Unregister(inst);
            _main.SetStatus(deleteFiles ? $"{name} was removed and deleted." : $"{name} was removed from the list.");
        });
    }
}
