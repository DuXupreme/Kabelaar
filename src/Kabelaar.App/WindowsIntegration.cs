using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Velopack;
using Velopack.Locators;

namespace Kabelaar.App;

public static class WindowsIntegration
{
    private const string ProgId = "Kabelaar.Project";
    public static VelopackApp Configure(VelopackApp application) => application
        .SetAutoApplyOnStartup(false)
        .OnAfterInstallFastCallback(_ => Safely(RegisterProjectAssociation))
        .OnAfterUpdateFastCallback(_ => Safely(RegisterProjectAssociation))
        .OnBeforeUninstallFastCallback(_ => Safely(RemoveProjectAssociation));

    private static void RegisterProjectAssociation()
    {
        var root = VelopackLocator.Current.RootAppDir;
        if (string.IsNullOrWhiteSpace(root)) return;
        var executable = Path.Combine(root, "current", "Kabelaar.exe");
        using (var registration = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            registration.SetValue("", "Kabelaar kabelboomproject");
            using var icon = registration.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{executable}\",0");
            using var command = registration.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{executable}\" \"%1\"");
        }
        using (var extension = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.kabelaar"))
        {
            if (string.IsNullOrWhiteSpace(extension.GetValue("") as string)) extension.SetValue("", ProgId);
            using var openWith = extension.CreateSubKey("OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        NotifyAssociationsChanged();
    }
    private static void RemoveProjectAssociation()
    {
        using (var extension = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.kabelaar", true))
        {
            if (extension?.GetValue("") as string == ProgId) extension.DeleteValue("", false);
            using var openWith = extension?.OpenSubKey("OpenWithProgids", true);
            openWith?.DeleteValue(ProgId, false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
        NotifyAssociationsChanged();
    }
    private static void Safely(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            try { Directory.CreateDirectory(App.DataDirectory); App.Log(error); } catch { }
        }
    }
    private static void NotifyAssociationsChanged() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
