using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PD2Cloud.Desktop.Views;

public partial class AlreadyRunningWindow : Window
{
    public AlreadyRunningWindow() => InitializeComponent();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
