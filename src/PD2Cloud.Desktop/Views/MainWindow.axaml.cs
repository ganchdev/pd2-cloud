using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using PD2Cloud.Desktop.ViewModels;
using System.ComponentModel;

namespace PD2Cloud.Desktop.Views;

public partial class MainWindow : Window
{
    private SetupWorkflowViewModel? Model => DataContext as SetupWorkflowViewModel;
    private SetupScreen lastScreen = SetupScreen.Loading;
    private string openedCode = "";
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (Model is { } model)
            {
                model.PropertyChanged += WorkflowChanged;
                model.GitHub.PropertyChanged += GitHubChanged;
                await model.InitializeAsync();
            }
        };
        Closed += (_, _) =>
        {
            if (Model is { } model) { model.PropertyChanged -= WorkflowChanged; model.GitHub.PropertyChanged -= GitHubChanged; model.Dispose(); }
        };
    }
    private void WorkflowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Model is { } model && lastScreen != model.Screen)
        {
            lastScreen = model.Screen;
            PageScroll.Offset = default;
        }
    }
    private async void GitHubChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Model is { IsAuthorize: true } model && model.GitHub.HasCode && openedCode != model.GitHub.UserCode)
        {
            openedCode = model.GitHub.UserCode;
            await OpenBrowserAsync(model.GitHub.VerificationAddress);
        }
    }
    private async Task OpenBrowserAsync(string address)
    {
        try
        {
            var uri = new Uri(address);
            if (uri.Scheme != "https" || uri.Host != "github.com") throw new InvalidOperationException();
            if (!await Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException();
            Model?.SetBrowserNotice("");
        }
        catch { Model?.SetBrowserNotice("Could not open your browser. Open github.com manually, or try again."); }
    }
    private async void GetStarted_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.GetStartedAsync(); }
    private void Settings_Click(object? sender, RoutedEventArgs e) => Model?.OpenSettings();
    private void GeneralSettings_Click(object? sender, RoutedEventArgs e) => Model?.OpenSettings(SettingsSection.General);
    private void SavesSettings_Click(object? sender, RoutedEventArgs e) => Model?.OpenSettings(SettingsSection.Saves);
    private void Dashboard_Click(object? sender, RoutedEventArgs e) => Model?.Dashboard();
    private void SettingsConnect_Click(object? sender, RoutedEventArgs e) => Model?.BeginConnection();
    private void Reconnect_Click(object? sender, RoutedEventArgs e) => Model?.BeginConnection(reconnect: true);
    private async void Back_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.BackAsync(); }
    private async void Connect_Click(object? sender, RoutedEventArgs e) { openedCode = ""; if (Model is { } m) await m.AuthorizeAsync(); }
    private async void ChangeRepository_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.ChangeRepositoryAsync(); }
    private async void SelectFolder_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.Local.SelectFolderAsync(); }
    private async void Inspect_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.Local.RefreshAsync(); }
    private async void Refresh_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.RefreshAsync(); }
    private async void ReloadRepositories_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.GitHub.ReloadAsync(); }
    private async void ChooseRepository_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.AcceptRepositoryAsync(); }
    private async void CompleteSetup_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.CompleteAsync(); }
    private async void Disconnect_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || model.GitHub.IsBusy) return;
        model.RequestDisconnect();
        if (await new DisconnectDialog().ShowDialog<bool>(this)) await model.DisconnectAsync();
        else model.CancelDisconnect();
    }
    private void CancelGitHub_Click(object? sender, RoutedEventArgs e) => Model?.GitHub.Cancel();
    private void SyncAll_Click(object? sender, RoutedEventArgs e) => Model?.BeginUpload();
    private async void ConfirmUpload_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.ConfirmUploadAsync(); }
    private void CancelUpload_Click(object? sender, RoutedEventArgs e) => Model?.Sync.Cancel();
    private async void OpenRecovery_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.OpenRecoveryAsync(); }
    private async void AssessRecovery_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.AssessRecoveryAsync(); }
    private void CancelCloud_Click(object? sender, RoutedEventArgs e) => Model?.Cloud.Cancel();
    private async void CheckCloud_Click(object? sender, RoutedEventArgs e) { if (Model is { } m) await m.CheckCloudAsync(); }
    private async void OpenAuthorization_Click(object? sender, RoutedEventArgs e) { if (Model is { } m && m.GitHub.HasCode) await OpenBrowserAsync(m.GitHub.VerificationAddress); }
    private async void CreateRepository_Click(object? sender, RoutedEventArgs e) => await OpenBrowserAsync("https://github.com/new?visibility=private&name=pd2-saves");
    private async void ManageAccess_Click(object? sender, RoutedEventArgs e) => await OpenBrowserAsync("https://github.com/settings/installations");
    private async void OpenRepository_Click(object? sender, RoutedEventArgs e)
    {
        if (Model?.GitHub.SelectedRepository is { } repository)
            await OpenBrowserAsync("https://github.com/" + string.Join('/', repository.FullName.Split('/').Select(Uri.EscapeDataString)));
    }
    private async void CopyCode_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || !model.GitHub.HasCode) return;
        try
        {
            if (Clipboard is null) throw new InvalidOperationException();
            await Clipboard.SetTextAsync(model.GitHub.UserCode);
            model.SetBrowserNotice("Code copied. Paste it on GitHub to continue.");
        }
        catch { model.SetBrowserNotice("Could not copy the code. Select it and copy it manually."); }
    }
}
