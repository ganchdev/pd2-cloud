using Avalonia.Controls;
using Avalonia.Interactivity;
using PD2Cloud.Desktop.ViewModels;

namespace PD2Cloud.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
}
