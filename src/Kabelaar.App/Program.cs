namespace Kabelaar.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        WindowsIntegration.Configure(Velopack.VelopackApp.Build()).Run();
        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
