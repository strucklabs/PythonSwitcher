using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PythonSwitcher.Core;

namespace PythonSwitcher;

public partial class MainWindow : Window
{
    /// <summary>Datumsformat für alle Anzeigen (eindeutig, unabhängig von der Windows-Sprache).</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly Switcher _switcher;
    private readonly List<ToolPanel> _panels = [];

    public MainWindow(Switcher switcher)
    {
        _switcher = switcher;
        InitializeComponent();
        BackupLimitBox.Text = switcher.Settings.BackupLimit.ToString();
        foreach (var tool in ToolDefinition.All)
        {
            var panel = new ToolPanel(switcher, tool, this);
            _panels.Add(panel);
            Tabs.Items.Add(new TabItem { Header = tool.DisplayName, Content = panel, Padding = new Thickness(14, 4, 14, 4) });
        }
        Tabs.Items.Add(new TabItem
        {
            Header = "Version check",
            Content = new VersionCheckPanel(switcher, this),
            Padding = new Thickness(14, 4, 14, 4),
        });
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
        var plan = _switcher.PlanRestore();
        if (plan is null)
        {
            MessageBox.Show(this, "There is no backup yet. Backups are created automatically before every PATH change.",
                "Undo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var backup = plan.Backup;
        var scopes = string.Join(" and ", new[] { backup.User is null ? null : "user PATH", backup.System is null ? null : "system PATH" }
            .Where(s => s is not null));
        var text =
            $"Restore the {scopes} to the state of {backup.Time.ToString(TimeFormat)}?\n\n" +
            "The current state is backed up first, so clicking again undoes the restore." +
            (backup.System is null ? "" : "\n\nChanging the system PATH requires administrator rights.");

        bool omitDead = true;
        if (plan.HasDead)
        {
            var dead = plan.DeadUser.Concat(plan.DeadSystem).Select(d => "   " + d);
            var answer = MessageBox.Show(this,
                text + "\n\nThe backup contains entries for folders that no longer exist " +
                "(for example because a version has been deleted in the meantime):\n\n" + string.Join("\n", dead) +
                "\n\nYes: restore and leave out these entries (recommended)\n" +
                "No: restore and keep these entries anyway\n" +
                "Cancel: change nothing",
                "Undo", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel)
                return;
            omitDead = answer == MessageBoxResult.Yes;
        }
        else if (MessageBox.Show(this, text, "Undo", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        if (_switcher.RestoreLatest(omitDead))
            SetStatus($"The PATH was restored to the state of {backup.Time.ToString(TimeFormat)}. This applies to newly opened terminals.");
        else
            SetStatus("The administrator prompt was declined. Nothing was changed.");
        RefreshAll();
    }

    private void BackupLimit_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            ApplyBackupLimit();
    }

    private void BackupLimit_LostFocus(object sender, RoutedEventArgs e) => ApplyBackupLimit();

    private bool _applyingLimit;

    /// <summary>Übernimmt die eingegebene Grenze; vor dem Löschen älterer Sicherungen wird nachgefragt.</summary>
    private void ApplyBackupLimit()
    {
        // Eine Rückfrage nimmt dem Feld den Fokus und löst LostFocus erneut aus – nicht doppelt ausführen.
        if (_applyingLimit)
            return;
        _applyingLimit = true;
        try
        {
            ApplyBackupLimitCore();
        }
        finally
        {
            _applyingLimit = false;
        }
    }

    private void ApplyBackupLimitCore()
    {
        var current = _switcher.Settings.BackupLimit;
        if (!int.TryParse(BackupLimitBox.Text.Trim(), out var value)
            || value < AppSettings.MinBackupLimit || value > AppSettings.MaxBackupLimit)
        {
            MessageBox.Show(this,
                $"Please enter a whole number from {AppSettings.MinBackupLimit} to {AppSettings.MaxBackupLimit}.",
                "Backups to keep", MessageBoxButton.OK, MessageBoxImage.Information);
            BackupLimitBox.Text = current.ToString();
            return;
        }
        if (value == current)
            return;

        int toDelete = _switcher.BackupsToDelete(value);
        if (toDelete > 0)
        {
            var answer = MessageBox.Show(this,
                $"Keep {value} backups from now on?\n\nThis deletes {toDelete} older backup(s) now. " +
                "The most recent ones are kept.",
                "Backups to keep", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                BackupLimitBox.Text = current.ToString();
                return;
            }
        }
        int deleted = _switcher.SetBackupLimit(value);
        SetStatus(deleted > 0
            ? $"{value} backups will be kept from now on; {deleted} older backup(s) were deleted."
            : $"{value} backups will be kept from now on.");
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_switcher.Folders.Backups);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_switcher.Folders.Backups}\"") { UseShellExecute = true });
    }
}
