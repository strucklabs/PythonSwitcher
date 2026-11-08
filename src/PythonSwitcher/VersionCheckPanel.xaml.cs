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
        CheckStatus.Problem => "conflict",
        _ => "note",
    };

    public Brush StatusBrush => item.Status switch
    {
        CheckStatus.UpdateVerfuegbar => new SolidColorBrush(Color.FromRgb(0xB3, 0x5A, 0x00)),
        CheckStatus.Problem => new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
        CheckStatus.Aktuell => new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
        CheckStatus.Hinweis => new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
        _ => Brushes.Gray,
    };
}

public partial class VersionCheckPanel : UserControl
{
    private readonly Switcher _switcher;
    private readonly MainWindow _main;
    private readonly VersionChecker _checker;
    private CancellationTokenSource? _cts;
    private List<CheckRow> _rows = [];
    private bool _busy;

    public VersionCheckPanel(Switcher switcher, MainWindow main)
    {
        _switcher = switcher;
        _main = main;
        // Festgehaltene Einträge als Kopie übergeben: die Prüfung läuft im Hintergrund
        _checker = new VersionChecker(new RegistryPathStore(), holds: () => _switcher.Settings.Holds.ToList());
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
            int pipUpdates = _rows.Count(r => r.IsUpdate && r.Item.Target?.Kind == TargetKind.PipPackage);
            int problems = _rows.Count(r => r.Item.Status == CheckStatus.Problem && r.Name != PipScan.DependenciesName);
            var text = $"Checked on {started.ToString(MainWindow.TimeFormat)} ({(DateTime.Now - started).TotalSeconds:0} s): " +
                       $"{updates} program(s) with updates, {pipUpdates} outdated pip package(s)" +
                       (problems > 0 ? $", {problems} dependency problem(s)." : ".");
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

    /// <summary>Einzelprüfung einer Zeile (auch nach Festhalten/Freigeben, damit Status und Übersicht stimmen).</summary>
    private async Task RecheckRowAsync(CheckRow row)
    {
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

            if (item.Target!.Kind == TargetKind.PipPackage)
            {
                // Übersicht dieser Python-Version an die neuen Zahlen anpassen
                RefreshSummary(item.Target.Root!);
                // Ein Update kann Abhängigkeiten brechen oder reparieren: pip check für diese Version wiederholen
                if (_rows.FirstOrDefault(r => r.Item.Target is { Kind: TargetKind.PipCheck } t
                        && PathUtil.SameDirectory(t.Root ?? "", item.Target.Root!)) is { } deps)
                {
                    ProgressText.Text = "Checking dependencies again …";
                    var depsItem = deps.Item;
                    Replace(depsItem, await Task.Run(() => _checker.RecheckAsync(depsItem, snapshot, token), token));
                }
            }

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
            // Neuprüfung der pip-Übersicht ersetzt alle Zeilen dieser Python-Version (Pakete und Abhängigkeiten)
            || (old.Target.Kind == TargetKind.PipSummary && t.Kind is TargetKind.PipPackage or TargetKind.PipCheck
                && PathUtil.SameDirectory(t.Root ?? "", old.Target.Root ?? "")));

        int index = _rows.FindIndex(Matches);
        if (index < 0)
            index = _rows.Count;
        _rows.RemoveAll(Matches);
        _rows.InsertRange(Math.Min(index, _rows.Count), replacement.Select(i => new CheckRow(i)));
        ApplyFilter();
    }

    /// <summary>Baut die Übersichtszeile der pip-Pakete einer Python-Version aus den aktuellen Zeilen neu auf.</summary>
    private void RefreshSummary(string root)
    {
        bool SameRoot(CheckRow r) => PathUtil.SameDirectory(r.Item.Target?.Root ?? "", root);
        int index = _rows.FindIndex(r => r.Item.Target?.Kind == TargetKind.PipSummary && r.Name == PipScan.SummaryName && SameRoot(r));
        if (index < 0)
            return;
        var updated = PipScan.UpdateSummary(_rows[index].Item, _rows.Where(SameRoot).Select(r => r.Item));
        _rows[index] = new CheckRow(updated);
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
            ? _rows.Where(r => r.IsUpdate || r.Item.Status is CheckStatus.Hinweis or CheckStatus.Problem).ToList()
            : _rows.ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CheckRow.Group)));
        List.ItemsSource = view;
    }

    /// <summary>Pakete mit neuerer Version in derselben Python-Version (für den Trockenlauf der Übersicht).</summary>
    private List<CheckItem> GroupRows(CheckItem item) => _rows
        .Where(r => PathUtil.SameDirectory(r.Item.Target?.Root ?? "", item.Target?.Root ?? "-"))
        .Select(r => r.Item).ToList();

    private bool CanDryRun(CheckItem? item)
    {
        if (item?.Target is not { } t || item.Hint.Contains("Not updatable with pip"))
            return false;
        if (t.Kind == TargetKind.PipPackage)
            return item.Latest is not null && VersionCompare.Compare(item.Installed, item.Latest) < 0;
        if (t.Kind == TargetKind.PipSummary && item.Name == PipScan.SummaryName)
            return GroupRows(item).Any(r => r.Target?.Kind == TargetKind.PipPackage && r.Latest is not null
                                            && VersionCompare.Compare(r.Installed, r.Latest) < 0
                                            && !r.Hint.Contains("Not updatable with pip"));
        return false;
    }

    /// <summary>Markierte Zeilen in Listenreihenfolge.</summary>
    private List<CheckRow> SelectedRows() =>
        List.SelectedItems.Cast<CheckRow>().OrderBy(r => _rows.IndexOf(r)).ToList();

    /// <summary>Aktuelle Zeile zu einem Eintrag (nach einer Einzelprüfung wurde die Zeile ggf. ersetzt).</summary>
    private CheckRow? CurrentRow(CheckItem item) =>
        _rows.FirstOrDefault(r => r.Item.Target?.ReplaceKey == item.Target?.ReplaceKey && r.Name == item.Name)
        ?? _rows.FirstOrDefault(r => r.Item.Target?.ReplaceKey == item.Target?.ReplaceKey);

    private static string Count(string text, int n) => n > 1 ? $"{text} ({n})" : text;

    private async void DryRun_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (_busy || rows.Count == 0)
            return;
        var items = rows.Select(r => r.Item).ToList();
        bool single = rows.Count == 1;
        if (single ? !CanDryRun(items[0]) : !VersionChecker.CanDryRunTogether(items))
            return;

        var label = single ? $"\"{rows[0].Name}\"" : $"{rows.Count} packages ({string.Join(", ", rows.Select(r => r.Name))})";
        _cts = new CancellationTokenSource();
        SetBusy(true);
        ProgressText.Text = $"Dry run for {label} – pip resolves the update without installing anything. This can take a while …";
        try
        {
            var snapshot = Snapshot();
            var token = _cts.Token;
            var item = items[0];
            var groupRows = GroupRows(item);
            var result = single
                ? await Task.Run(() => _checker.DryRunAsync(item, groupRows, snapshot, token), token)
                : await Task.Run(() => _checker.DryRunPackagesAsync(items, snapshot, token), token);
            var text = PipScan.DescribeDryRun(result);
            ProgressText.Text = $"Dry run for {label} finished at {DateTime.Now.ToString(MainWindow.TimeFormat)}. Nothing was installed.";
            MessageBox.Show(_main, text, $"Dry run – {rows[0].Group}", MessageBoxButton.OK,
                result.Error is not null || result.NotInstallable.Count > 0 || result.Breaks is { Count: > 0 }
                || result.Changes.Any(c => c.Kind == PipScan.ChangeKind.Downgrade)
                    ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"Error during the dry run: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var rows = SelectedRows();
        var items = rows.Select(r => r.Item).ToList();
        int n = rows.Count;

        RecheckItem.Header = n > 1 ? Count("Check these entries again", n) : "Check this entry again";
        RecheckItem.IsEnabled = !_busy && items.Any(i => i.Target is not null);

        DryRunItem.Header = n > 1 ? $"Test updates together ({n}, dry run)" : "Test update (dry run)";
        DryRunItem.IsEnabled = !_busy && (n == 1 ? CanDryRun(items[0]) : VersionChecker.CanDryRunTogether(items));

        var holdable = items.Where(i => HoldRules.KeyFor(i) is not null).ToList();
        bool allHeld = holdable.Count > 0 && holdable.All(i => HoldRules.Find(i, _switcher.Settings.Holds) is not null);
        HoldItem.Header = allHeld
            ? Count("Release hold", holdable.Count)
            : Count(holdable.Count > 1 ? "Hold these versions …" : "Hold this version …", holdable.Count);
        HoldItem.IsEnabled = !_busy && holdable.Count > 0;

        int commands = VersionChecker.CombineCommands(items).Count;
        CopyCommandItem.Header = commands > 1 ? Count("Copy update commands", commands) : "Copy update command";
        CopyCommandItem.IsEnabled = commands > 0;
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedRows().Select(r => r.Item).Where(i => i.Target is not null).ToList();
        if (_busy || items.Count == 0)
            return;
        for (int k = 0; k < items.Count; k++)
        {
            // Zeilen werden beim Prüfen ersetzt – jeweils die aktuelle Zeile suchen
            if (CurrentRow(items[k]) is { } row)
            {
                if (items.Count > 1)
                    _main.SetStatus($"Checking entry {k + 1} of {items.Count} …");
                await RecheckRowAsync(row);
            }
        }
        if (items.Count > 1)
            _main.SetStatus($"{items.Count} entries checked again at {DateTime.Now.ToString(MainWindow.TimeFormat)}.");
    }

    private async void Hold_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedRows().Select(r => r.Item).Where(i => HoldRules.KeyFor(i) is not null).ToList();
        if (_busy || items.Count == 0)
            return;
        bool allHeld = items.All(i => HoldRules.Find(i, _switcher.Settings.Holds) is not null);
        if (allHeld)
        {
            foreach (var i in items)
            {
                var key = HoldRules.KeyFor(i)!.Value;
                _switcher.Release(key.Kind, key.Root, key.Package);
            }
            _main.SetStatus(items.Count == 1 ? $"\"{items[0].Name}\" is no longer held." : $"{items.Count} entries are no longer held.");
        }
        else
        {
            var toHold = items.Where(i => HoldRules.Find(i, _switcher.Settings.Holds) is null).ToList();
            var dialog = toHold.Count == 1
                ? new HoldDialog(toHold[0].Name, toHold[0].Installed)
                : new HoldDialog($"{toHold.Count} entries ({string.Join(", ", toHold.Select(i => i.Name))})", null);
            dialog.Owner = _main;
            if (dialog.ShowDialog() != true)
                return;
            foreach (var i in toHold)
            {
                var key = HoldRules.KeyFor(i)!.Value;
                _switcher.Hold(key.Kind, key.Root, key.Package, i.Installed, dialog.Reason);
            }
            _main.SetStatus(toHold.Count == 1 ? $"\"{toHold[0].Name}\" is now held at {toHold[0].Installed}." : $"{toHold.Count} entries are now held.");
            items = toHold;
        }
        foreach (var i in items)
        {
            if (CurrentRow(i) is { } row)
                await RecheckRowAsync(row);
        }
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        var commands = VersionChecker.CombineCommands(SelectedRows().Select(r => r.Item));
        if (commands.Count == 0)
            return;
        var text = string.Join(Environment.NewLine, commands);
        try
        {
            Clipboard.SetText(text);
            _main.SetStatus(commands.Count == 1
                ? "Update command copied to the clipboard: " + text
                : $"{commands.Count} update commands copied to the clipboard (one per line).");
        }
        catch (Exception)
        {
            _main.SetStatus("The clipboard is busy. Please try again.");
        }
    }
}
