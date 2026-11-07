using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PythonSwitcher.Core;

namespace PythonSwitcher;

public partial class MainWindow : Window
{
    private readonly Switcher _switcher;
    private readonly List<ToolPanel> _panels = [];

    public MainWindow(Switcher switcher)
    {
        _switcher = switcher;
        InitializeComponent();
        foreach (var tool in ToolDefinition.All)
        {
            var panel = new ToolPanel(switcher, tool, this);
            _panels.Add(panel);
            Tabs.Items.Add(new TabItem { Header = tool.DisplayName, Content = panel, Padding = new Thickness(14, 4, 14, 4) });
        }
        Loaded += async (_, _) =>
        {
            foreach (var p in _panels)
                await p.ScanAsync(initial: true);
        };
    }

    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Sperrt die Bedienung, solange eine Aktion läuft (die Einstellungen sind nicht threadsicher).</summary>
    public void SetBusy(bool busy)
    {
        Tabs.IsEnabled = !busy;
        UndoButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    public void RefreshAll()
    {
        foreach (var p in _panels)
            p.Refresh();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var backup = _switcher.LatestBackup();
        if (backup is null)
        {
            MessageBox.Show(this, "Es gibt noch keine Sicherung. Sicherungen entstehen automatisch vor jeder Änderung am PATH.",
                "Rückgängig", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var scopes = string.Join(" und ", new[] { backup.User is null ? null : "Benutzer-PATH", backup.System is null ? null : "System-PATH" }
            .Where(s => s is not null));
        var answer = MessageBox.Show(this,
            $"Den {scopes} auf den Stand vom {backup.Time:dd.MM.yyyy HH:mm:ss} zurücksetzen?\n\n" +
            "Der aktuelle Stand wird vorher ebenfalls gesichert. Ein zweiter Klick macht das Zurücksetzen also wieder rückgängig." +
            (backup.System is null ? "" : "\n\nFür den System-PATH sind Administratorrechte nötig."),
            "Rückgängig", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;

        if (_switcher.RestoreLatest())
            SetStatus($"Der PATH wurde auf den Stand vom {backup.Time:dd.MM.yyyy HH:mm:ss} zurückgesetzt. Das gilt für neu geöffnete Terminals.");
        else
            SetStatus("Die Administrator-Abfrage wurde abgelehnt. Es wurde nichts geändert.");
        RefreshAll();
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_switcher.Folders.Backups);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_switcher.Folders.Backups}\"") { UseShellExecute = true });
    }
}
