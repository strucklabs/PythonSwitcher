using System.Windows;
using PythonSwitcher.Core;

namespace PythonSwitcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Admin-Hilfsmodus: nur den System-PATH schreiben und sofort beenden, ohne Fenster.
        if (e.Args.Length == 2 && e.Args[0] == ElevatedSystemPathWriter.Argument)
        {
            Shutdown(ElevatedSystemPathWriter.RunElevated(e.Args[1]));
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "PythonSwitcher – Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var folders = DataFolders.Default;
        var store = new RegistryPathStore();
        var switcher = new Switcher(folders, store, new ElevatedSystemPathWriter(folders));
        new MainWindow(switcher).Show();
    }
}
