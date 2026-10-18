using Avalonia;
using PD2Cloud.Local;

namespace PD2Cloud.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Recovery/preparation leases coordinate cooperating processes, so the desktop itself must
        // be a single instance. A second launch explains this instead of failing later on a push.
        var state = SingleInstanceGuard.TryAcquire(ApplicationDataPaths.DirectoryPath, out var guard);
        App.SecondInstance = state == SingleInstanceState.AlreadyRunning;
        using (guard)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
