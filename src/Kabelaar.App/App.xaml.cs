using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Kabelaar.App;

public partial class App : Application
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kabelaar.UserData", "Data");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(DataDirectory);
        DispatcherUnhandledException += OnUnexpectedException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };
        var window = new MainWindow();
        MainWindow = window;
        if (e.Args.Contains("--smoke-test"))
        {
            window.Loaded += async (_, _) =>
            {
                try { await window.RunSmokeTestAsync(e.Args); Shutdown(0); }
                catch (Exception ex) { Log(ex); Shutdown(2); }
            };
        }
        else
        {
            var path = e.Args.FirstOrDefault(x => x.EndsWith(".kabelaar", StringComparison.OrdinalIgnoreCase));
            window.Loaded += async (_, _) => await window.InitializeAsync(path);
        }
        window.Show();
    }
    private void OnUnexpectedException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show("De bewerking kon niet worden voltooid. Sla je werk op onder een nieuwe naam.\n\n" +
                        e.Exception.Message, "Kabelaar", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
    public static void Log(Exception exception)
    {
        try { File.AppendAllText(Path.Combine(DataDirectory, "kabelaar.log"),
            $"{DateTimeOffset.Now:O} {exception}\n"); } catch { }
    }
}
