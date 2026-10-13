using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PD2Cloud.Desktop.ViewModels;
using PD2Cloud.Desktop.Views;
using PD2Cloud.Desktop.Services;
using PD2Cloud.Local;

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
            window.DataContext = new MainWindowViewModel(new FolderInspector(), new NativeFolderPicker(window.StorageProvider));
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
