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
    private const string UpdatePrefix = "Update: ";

    private readonly Switcher _switcher;
    private readonly MainWindow _main;
    private CancellationTokenSource? _cts;
    private List<CheckRow> _rows = [];

    public VersionCheckPanel(Switcher switcher, MainWindow main)
    {
        _switcher = switcher;
        _main = main;
        InitializeComponent();
        ProgressText.Text = "Not checked yet.";
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        CheckButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        var started = DateTime.Now;
        try
        {
            // Momentaufnahme: die Liste kann sich in anderen Reitern ändern, während die Prüfung läuft.
            var snapshot = _switcher.Settings.Installations.Select(i => new Installation
            {
                Tool = i.Tool, Root = i.Root, Version = i.Version, Source = i.Source, InstallerPath = i.InstallerPath,
            }).ToList();
            var progress = new Progress<string>(t => ProgressText.Text = t);
            var checker = new VersionChecker(new RegistryPathStore());
            var token = _cts.Token;
            var items = await Task.Run(() => checker.RunAsync(snapshot, progress, token), token);

            _rows = items.Select(i => new CheckRow(i)).ToList();
            ApplyFilter();
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
            CheckButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _cts.Dispose();
            _cts = null;
        }
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
            : _rows;
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CheckRow.Group)));
        List.ItemsSource = view;
    }

    private void CopyHint_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not CheckRow row || string.IsNullOrEmpty(row.Hint))
            return;
        var text = row.Hint.StartsWith(UpdatePrefix, StringComparison.Ordinal) ? row.Hint[UpdatePrefix.Length..] : row.Hint;
        try
        {
            Clipboard.SetText(text);
            _main.SetStatus("Hint copied to the clipboard.");
        }
        catch (Exception)
        {
            _main.SetStatus("The clipboard is busy. Please try again.");
        }
    }
}
