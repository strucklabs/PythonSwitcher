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
        CheckStatus.UpdateVerfuegbar => "Update verfügbar",
        CheckStatus.Aktuell => "aktuell",
        CheckStatus.Unbekannt => "unbekannt",
        _ => "Hinweis",
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
    private CancellationTokenSource? _cts;
    private List<CheckRow> _rows = [];

    public VersionCheckPanel(Switcher switcher, MainWindow main)
    {
        _switcher = switcher;
        _main = main;
        InitializeComponent();
        ProgressText.Text = "Noch nicht geprüft.";
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
            int pipUpdates = _rows.Count(r => r.IsUpdate && r.Item.Category == CheckCategory.PipPaket && r.Name != "Übersicht");
            var text = $"Geprüft am {started:dd.MM.yyyy HH:mm} ({(DateTime.Now - started).TotalSeconds:0} s): " +
                       $"{updates} Programme mit Update, {pipUpdates} veraltete pip-Pakete.";
            ProgressText.Text = text;
            _main.SetStatus("Versionscheck abgeschlossen. " + text);
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            ProgressText.Text = "Fehler beim Versionscheck: " + ex.Message;
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
        ProgressText.Text = "Wird abgebrochen …";
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
        var text = row.Hint.StartsWith("Aktualisieren: ") ? row.Hint["Aktualisieren: ".Length..] : row.Hint;
        try
        {
            Clipboard.SetText(text);
            _main.SetStatus("Hinweis in die Zwischenablage kopiert.");
        }
        catch (Exception)
        {
            _main.SetStatus("Die Zwischenablage ist gerade belegt. Bitte erneut versuchen.");
        }
    }
}
