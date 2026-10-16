using Avalonia.Controls;
using Avalonia.Interactivity;
using PD2Cloud.Desktop.ViewModels;

namespace PD2Cloud.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel model)
                await Task.WhenAll(model.InitializeAsync(), model.GitHub?.InitializeAsync() ?? Task.CompletedTask);
        };
        Closed += (_, _) => (DataContext as MainWindowViewModel)?.Dispose();
    }

    private async void SelectFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel model) await model.SelectFolderAsync();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel model) await model.RefreshAsync();
    }
    private async void Connect_Click(object? sender, RoutedEventArgs e) { if (DataContext is MainWindowViewModel m && m.GitHub is { } g) await g.ConnectAsync(); }
    private async void ReloadRepositories_Click(object? sender, RoutedEventArgs e) { if (DataContext is MainWindowViewModel m && m.GitHub is { } g) await g.ReloadAsync(); }
    private async void ChooseRepository_Click(object? sender, RoutedEventArgs e) { if (DataContext is MainWindowViewModel m && m.GitHub is { } g) await g.SelectAsync(); }
    private async void Disconnect_Click(object? sender, RoutedEventArgs e) { if (DataContext is MainWindowViewModel m && m.GitHub is { } g) await g.DisconnectAsync(); }
    private void CancelGitHub_Click(object? sender, RoutedEventArgs e) { if (DataContext is MainWindowViewModel m) m.GitHub?.Cancel(); }
}
