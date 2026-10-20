using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Kabelaar.App;

public partial class App : Application
{
    private bool _backgroundAcceptance;
    internal static bool IsBackgroundAcceptance { get; private set; }
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kabelaar.UserData", "Data");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsBackgroundAcceptance = e.Args.Contains("--smoke-test") || e.Args.Contains("--installed-acceptance") ||
            e.Args.Contains("--acceptance-hold") ||
            (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KABELAAR_ACCEPTANCE_LAUNCH_OUTPUT")) &&
             !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KABELAAR_ACCEPTANCE_NONCE")));
        Directory.CreateDirectory(DataDirectory);
        DispatcherUnhandledException += OnUnexpectedException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };
        if (e.Args.Contains("--acceptance-hold"))
        {
            _backgroundAcceptance = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = HoldAcceptanceProcessAsync(e.Args);
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        var launchOutput = Environment.GetEnvironmentVariable("KABELAAR_ACCEPTANCE_LAUNCH_OUTPUT");
        var launchProbe = !string.IsNullOrWhiteSpace(launchOutput) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KABELAAR_ACCEPTANCE_NONCE"));
        var backgroundTest = e.Args.Contains("--smoke-test") || e.Args.Contains("--installed-acceptance") || launchProbe;
        var interactiveSmoke = e.Args.Contains("--smoke-test") && e.Args.Contains("--interactive-smoke");
        _backgroundAcceptance = backgroundTest;
        if ((backgroundTest && !interactiveSmoke) || e.Args.Contains("--no-activate")) window.ShowActivated = false;
        if (backgroundTest && !interactiveSmoke)
        {
            window.ShowInTaskbar = false;
            // Exercise a fully laid-out WPF window without obscuring the user's applications.
            window.Opacity = 0;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
        }
        if (e.Args.Contains("--smoke-test"))
        {
            window.Loaded += async (_, _) =>
            {
                try { await window.RunSmokeTestAsync(e.Args); Shutdown(0); }
                catch (Exception ex) { Log(ex); Shutdown(2); }
            };
        }
        else if (e.Args.Contains("--installed-acceptance"))
        {
            // The clean-close stage must observe Closed and write its result before exit.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Loaded += async (_, _) =>
            {
                try { await window.RunInstalledAcceptanceAsync(e.Args); Shutdown(0); }
                catch (Exception ex) { Log(ex); Shutdown(2); }
            };
        }
        else if (launchProbe)
        {
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await window.RunLaunchAcceptanceAsync(e.Args, launchOutput!,
                        Environment.GetEnvironmentVariable("KABELAAR_ACCEPTANCE_NONCE")!);
                    Shutdown(0);
                }
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
        if (_backgroundAcceptance) { e.Handled = true; Shutdown(2); return; }
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
