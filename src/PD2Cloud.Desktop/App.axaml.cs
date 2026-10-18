using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PD2Cloud.Desktop.Logging;
using PD2Cloud.Desktop.ViewModels;
using PD2Cloud.Desktop.Views;
using PD2Cloud.Desktop.Services;
using PD2Cloud.Local;
using PD2Cloud.GitHub;
using PD2Cloud.Platform;

namespace PD2Cloud.Desktop;

public partial class App : Application
{
    internal static bool SecondInstance { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, error) =>
            AppLog.Error("UI", "Unhandled UI exception", error.Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (SecondInstance)
            {
                AppLog.Warn("App", "Opening the already-running window.");
                desktop.MainWindow = new AlreadyRunningWindow();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            AppLog.Info("App", "Creating the main window.");
            var window = new MainWindow();
            var clientId = ClientId.Resolve();
            var connection = new GitHubConnection(new GitHubApi(GitHubApi.CreateHttpClient(), clientId), new NativeCredentialStore(clientId));
            var github = new GitHubConnectionViewModel(connection);
            window.DataContext = new SetupWorkflowViewModel(new MainWindowViewModel(new CollectionScanner(), new NativeFolderPicker(window.StorageProvider), new FolderSettingsStore(), github), new SyncOperations(connection));
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
