using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PythonSwitcher.Core;
using PythonSwitcher.Core.VersionCheck;

namespace PythonSwitcher;

public sealed class CheckRow(CheckItem item)
{
    public CheckItem Item => item;
    public string Group => item.Group;
    public string Name => item.Name;
    public string Installed => item.Installed ?? "";
    public string Latest => item.Latest ?? "";
    public string Hint => item.Hint;
    public string? Note => item.Note;
    public string? Detail => item.Detail;
    public bool IsUpdate => item.Status == CheckStatus.UpdateVerfuegbar;

    public string StatusText => item.Status switch
    {
        CheckStatus.UpdateVerfuegbar => "update available",
        CheckStatus.Aktuell => "up to date",
        CheckStatus.Unbekannt => "unknown",
        _ => "note",
    };

    public Brush StatusBrush => item.Status switch
    {
        CheckStatus.UpdateVerfuegbar => new SolidColorBrush(Color.FromRgb(0xB3, 0x5A, 0x00)),
        CheckStatus.Aktuell => new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
        CheckStatus.Hinweis => new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
        _ => Brushes.Gray,
    };
}

public partial class VersionCheckPanel : UserControl
{
    private readonly Switcher _switcher;
    private readonly MainWindow _main;
    private readonly VersionChecker _checker = new(new RegistryPathStore());
    private CancellationTokenSource? _cts;
    private List<CheckRow> _rows = [];
    private bool _busy;

    public VersionCheckPanel(Switcher switcher, MainWindow main)
    {
        _switcher = switcher;
        _main = main;
        InitializeComponent();
        ProgressText.Text = "Not checked yet.";
    }

    /// <summary>Momentaufnahme: die Liste kann sich in anderen Reitern ändern, während die Prüfung läuft.</summary>
    private List<Installation> Snapshot() => _switcher.Settings.Installations.Select(i => new Installation
    {
        Tool = i.Tool, Root = i.Root, Version = i.Version, Source = i.Source, InstallerPath = i.InstallerPath,
    }).ToList();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true);
        var started = DateTime.Now;
        try
        {
            var snapshot = Snapshot();
            var progress = new Progress<string>(t => ProgressText.Text = t);
            var token = _cts.Token;
            var items = await Task.Run(() => _checker.RunAsync(snapshot, progress, token), token);

            _rows = items.Select(i => new CheckRow(i)).ToList();
            ApplyFilter();
            TakeOverVersions(items);
            int updates = _rows.Count(r => r.IsUpdate && r.Item.Category != CheckCategory.PipPaket);
            int pipUpdates = _rows.Count(r => r.IsUpdate && r.Item.Category == CheckCategory.PipPaket && r.Name != PipScan.SummaryName);
            var text = $"Checked on {started.ToString(MainWindow.TimeFormat)} ({(DateTime.Now - started).TotalSeconds:0} s): " +
                       $"{updates} program(s) with updates, {pipUpdates} outdated pip package(s).";
            ProgressText.Text = text;
            _main.SetStatus("Version check finished. " + text);
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            ProgressText.Text = "Error during the version check: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Frisch ausgelesene Python-/Git-Versionen in die Liste der Reiter Python und Git übernehmen.</summary>
    private void TakeOverVersions(IEnumerable<CheckItem> items)
    {
        bool changed = false;
        foreach (var item in items.Where(i => i.Target is { Kind: TargetKind.Runtime } && i.Installed is not null))
            changed |= _switcher.UpdateVersion(item.Target!.Tool!.Value, item.Target.Root!, item.Installed);
        if (changed)
            _main.RefreshAll();
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || List.SelectedItem is not CheckRow { Item.Target: not null } row)
            return;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        ProgressText.Text = $"Checking \"{row.Name}\" again …";
        try
        {
            var snapshot = Snapshot();
            var token = _cts.Token;
            var item = row.Item;
            var replacement = await Task.Run(() => _checker.RecheckAsync(item, snapshot, token), token);
            Replace(item, replacement);
            TakeOverVersions(replacement);

            var main = replacement.FirstOrDefault(r => r.Name == item.Name) ?? replacement.FirstOrDefault();
            var result = main is null ? "no result"
                : main.Status switch
                {
                    CheckStatus.Aktuell => $"up to date ({main.Installed})",
                    CheckStatus.UpdateVerfuegbar => $"update still available ({main.Installed} → {main.Latest})",
                    _ => main.Hint,
                };
            ProgressText.Text = $"Checked \"{row.Name}\" again at {DateTime.Now.ToString(MainWindow.TimeFormat)}: {result}";
            _main.SetStatus(ProgressText.Text);
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"Error while checking \"{row.Name}\" again: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Ersetzt die Zeilen mit demselben Prüfschlüssel an der Stelle der ersten davon.</summary>
    private void Replace(CheckItem old, IReadOnlyList<CheckItem> replacement)
    {
        var key = old.Target!.ReplaceKey;
        bool Matches(CheckRow r) => r.Item.Target is { } t && (t.ReplaceKey == key
            // Neuprüfung der pip-Übersicht ersetzt auch die einzelnen Paketzeilen dieser Python-Version
            || (old.Target.Kind == TargetKind.PipSummary && t.Kind == TargetKind.PipPackage
                && PathUtil.SameDirectory(t.Root ?? "", old.Target.Root ?? "")));

        int index = _rows.FindIndex(Matches);
        if (index < 0)
            index = _rows.Count;
        _rows.RemoveAll(Matches);
        _rows.InsertRange(Math.Min(index, _rows.Count), replacement.Select(i => new CheckRow(i)));
        ApplyFilter();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ProgressText.Text = "Cancelling …";
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var rows = OnlyUpdates.IsChecked == true
            ? _rows.Where(r => r.IsUpdate || r.Item.Status == CheckStatus.Hinweis).ToList()
            : _rows.ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CheckRow.Group)));
        List.ItemsSource = view;
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = List.SelectedItem as CheckRow;
        RecheckItem.IsEnabled = !_busy && row?.Item.Target is not null;
        CopyCommandItem.IsEnabled = !string.IsNullOrEmpty(row?.Item.UpdateCommand);
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not CheckRow { Item.UpdateCommand: { Length: > 0 } command })
            return;
        try
        {
            Clipboard.SetText(command);
            _main.SetStatus("Update command copied to the clipboard: " + command);
        }
        catch (Exception)
        {
            _main.SetStatus("The clipboard is busy. Please try again.");
        }
    }
}
