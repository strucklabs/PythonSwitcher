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
    public string StatusText => state.Missing ? "fehlt" : state.IsActive ? "● aktiv" : "";
    public string Version => state.Installation.Version;
    public string Root => state.Installation.Root;
    public string SourceText => state.Installation.Source switch
    {
        InstallSource.Gefunden => "gefunden",
        InstallSource.Manuell => "hinzugefügt",
        InstallSource.Heruntergeladen => "heruntergeladen",
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
            ActiveText.Text = $"Derzeit ist kein {_tool.DisplayName} aktiv.";
            ActiveDetail.Text = $"Windows findet „{Path.GetFileNameWithoutExtension(_tool.MainExecutable)}“ über keinen PATH-Eintrag.";
        }
        else if (eff.Installation is not null)
        {
            ActiveText.Text = $"Aktiv: {_tool.DisplayName} {eff.Installation.Version}";
            ActiveDetail.Text = $"{eff.Installation.Root}   ·   über den {ScopeName(eff.Scope)}";
        }
        else if (_tool.IsAlwaysCompetitor(eff.Entry))
        {
            ActiveText.Text = $"Derzeit ist kein {_tool.DisplayName} aktiv.";
            ActiveDetail.Text = "„python“ öffnet derzeit nur die Weiterleitung zum Microsoft Store (ein Platzhalter von Windows, kein echtes Python).";
        }
        else
        {
            ActiveText.Text = $"Aktiv: eine {_tool.DisplayName}-Installation, die nicht in der Liste steht";
            ActiveDetail.Text = $"{eff.Entry}   ·   über den {ScopeName(eff.Scope)}";
            ShowWarning("Mit „Ordner hinzufügen …“ lässt sich diese Installation in die Liste aufnehmen.");
        }
        UpdateButtons();
    }

    private static string ScopeName(PathScope? scope) => scope == PathScope.System ? "System-PATH" : "Benutzer-PATH";

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
            _main.SetStatus($"Suche nach {_tool.DisplayName}-Installationen …");
            int added = await Task.Run(() => _switcher.Scan(_tool));
            if (!initial || added > 0)
                _main.SetStatus(added switch
                {
                    0 => $"Suche abgeschlossen: keine neuen {_tool.DisplayName}-Installationen gefunden.",
                    1 => $"Suche abgeschlossen: 1 neue {_tool.DisplayName}-Installation gefunden.",
                    _ => $"Suche abgeschlossen: {added} neue {_tool.DisplayName}-Installationen gefunden.",
                });
            else
                _main.SetStatus("Bereit.");
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
                    $"Im System-PATH steht ein anderer {_tool.DisplayName}-Eintrag, der immer Vorrang vor dem Benutzer-PATH hat:\n\n" +
                    string.Join("\n", plan.SystemConflicts.Select(c => "   " + c)) +
                    $"\n\nDamit {_tool.DisplayName} {inst.Version} wirklich verwendet wird, muss der System-PATH umsortiert werden. Er gilt für alle Benutzer dieses Computers und lässt sich nur mit Administratorrechten ändern. Vorher wird eine Sicherung angelegt.\n\n" +
                    "Ja: System-PATH umsortieren (Windows fragt nach Administratorrechten)\n" +
                    "Nein: nur den Benutzer-PATH ändern (die andere Version bleibt dann aktiv)\n" +
                    "Abbrechen: nichts ändern",
                    "System-PATH ändern?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel)
                    return;
                if (answer == MessageBoxResult.Yes)
                    plan = planner.PlanActivation(inst.Root, user, system, allowSystem: true);
                else
                    systemDeclined = true;
            }

            if (!plan.UserChanged && !plan.SystemChanged)
            {
                _main.SetStatus($"{_tool.DisplayName} {inst.Version} steht bereits vorn. Es war keine Änderung nötig.");
                return;
            }

            if (!await Task.Run(() => _switcher.Apply(plan)))
            {
                _main.SetStatus("Die Administrator-Abfrage wurde abgelehnt. Es wurde nichts geändert.");
                return;
            }

            var eff = _switcher.Effective(_tool);
            if (ReferenceEquals(eff.Installation, inst))
                _main.SetStatus($"{_tool.DisplayName} {inst.Version} ist jetzt aktiv. Das gilt für neu geöffnete Terminals und Programme.");
            else if (systemDeclined)
                _main.SetStatus($"Der Benutzer-PATH wurde geändert, aber der Eintrag im System-PATH hat weiterhin Vorrang. Aktiv bleibt: {eff.Entry}");
            else
                _main.SetStatus($"Der PATH wurde geändert, aber Windows findet zuerst: {eff.Entry}");
        });
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"Ordner einer {_tool.DisplayName}-Installation wählen (der Ordner mit {_tool.MainExecutable})",
        };
        if (dialog.ShowDialog(_main) != true)
            return;
        await RunBusy(async () =>
        {
            Installation? inst = null;
            var error = await Task.Run(() => _switcher.AddManual(_tool, dialog.FolderName, out inst));
            if (error is not null)
                MessageBox.Show(_main, error, "Ordner hinzufügen", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                _main.SetStatus($"{_tool.DisplayName} {inst!.Version} wurde hinzugefügt. Mit „Aktivieren“ wird sie verwendet.");
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
            MessageBox.Show(_main, $"{name} ist gerade aktiv. Bitte zuerst eine andere Version aktivieren und dann entfernen.",
                "Entfernen", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var own = inst.Source == InstallSource.Heruntergeladen;
        var intro = MessageBox.Show(_main,
            $"{name} aus der Liste entfernen?\n\n{inst.Root}\n\n" +
            (own
                ? "Diese Version wurde mit PythonSwitcher heruntergeladen. Im nächsten Schritt kann sie auch gelöscht werden."
                : "Die Dateien bleiben erhalten. Wer die Version ganz deinstallieren möchte, kann das über die Windows-Einstellungen unter „Apps“ tun."),
            "Entfernen", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (intro != MessageBoxResult.OK)
            return;

        bool deleteFiles = false;
        if (own && Directory.Exists(inst.Root))
        {
            var del = MessageBox.Show(_main,
                $"Die Dateien von {name} auch löschen?\n\n{inst.Root}\n\n" +
                "Dabei gehen auch nachträglich installierte Pakete verloren. Das lässt sich nicht rückgängig machen. " +
                "Die PATH-Einträge dieser Version werden dann ebenfalls entfernt.",
                "Dateien löschen?", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
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
                    $"{name} hat Einträge im PATH. Sollen diese auch entfernt werden?\n\n" +
                    "Ja: Einträge entfernen (die Version ist dann nicht mehr über den PATH erreichbar)\n" +
                    "Nein: Einträge stehen lassen",
                    "PATH-Einträge", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel)
                    return;
                removeEntries = answer == MessageBoxResult.Yes;
            }

            if (removeEntries && hasEntries)
            {
                if (plan.SystemConflicts.Count > 0)
                {
                    var sys = MessageBox.Show(_main,
                        "Einige Einträge stehen im System-PATH:\n\n" + string.Join("\n", plan.SystemConflicts.Select(c => "   " + c)) +
                        "\n\nDiese ebenfalls entfernen? Windows fragt dafür nach Administratorrechten.",
                        "System-PATH ändern?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (sys == MessageBoxResult.Yes)
                        plan = planner.PlanRemoval(inst.Root, user, system, allowSystem: true);
                }
                if (!await Task.Run(() => _switcher.Apply(plan)))
                {
                    _main.SetStatus("Die Administrator-Abfrage wurde abgelehnt. Es wurde nichts geändert.");
                    return;
                }
            }

            if (deleteFiles)
            {
                _main.SetStatus($"{name} wird gelöscht …");
                await new InstallService(_switcher).DeleteDownloadedAsync(inst);
            }

            _switcher.Unregister(inst);
            _main.SetStatus(deleteFiles ? $"{name} wurde entfernt und gelöscht." : $"{name} wurde aus der Liste entfernt.");
        });
    }
}
