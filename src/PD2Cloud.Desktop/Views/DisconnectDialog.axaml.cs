using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PD2Cloud.Desktop.Views;

public partial class DisconnectDialog : Window
{
    public DisconnectDialog() => InitializeComponent();
    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
