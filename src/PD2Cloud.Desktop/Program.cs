using Avalonia;
using PD2Cloud.Desktop.Logging;
using PD2Cloud.Local;

namespace PD2Cloud.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, error) =>
            AppLog.Error("App", "Unhandled exception", error.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, error) =>
        {
            AppLog.Error("App", "Unobserved task exception", error.Exception);
            error.SetObserved();
        };

        try
        {
            AppLog.Info("App", $"Command line: {string.Join(' ', args)}");
            // Recovery/preparation leases coordinate cooperating processes, so the desktop itself must
            // be a single instance. A second launch explains this instead of failing later on a push.
            var state = SingleInstanceGuard.TryAcquire(ApplicationDataPaths.DirectoryPath, out var guard);
            App.SecondInstance = state == SingleInstanceState.AlreadyRunning;
            if (state == SingleInstanceState.AlreadyRunning)
                AppLog.Warn("App", "A second desktop instance was launched; showing the already-running notice.");
            else if (state == SingleInstanceState.Unavailable)
                AppLog.Warn("App", "The single-instance lock could not be acquired; recovery storage may contend.");
            using (guard)
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
        }
        catch (Exception error)
        {
            AppLog.Error("App", "Fatal desktop error", error);
            throw;
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
