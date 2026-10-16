using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PD2Cloud.Desktop.ViewModels;
using PD2Cloud.Desktop.Views;
using PD2Cloud.Desktop.Services;
using PD2Cloud.Local;
using PD2Cloud.GitHub;
using PD2Cloud.Platform;

namespace PD2Cloud.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var clientId = Environment.GetEnvironmentVariable("PD2CLOUD_GITHUB_CLIENT_ID") ?? "";
            var github = new GitHubConnectionViewModel(new GitHubConnection(new GitHubApi(GitHubApi.CreateHttpClient(), clientId), new NativeCredentialStore(clientId)));
            window.DataContext = new SetupWorkflowViewModel(new MainWindowViewModel(new CollectionScanner(), new NativeFolderPicker(window.StorageProvider), new FolderSettingsStore(), github));
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
