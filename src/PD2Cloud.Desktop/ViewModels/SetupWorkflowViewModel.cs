using System.ComponentModel;

namespace PD2Cloud.Desktop.ViewModels;

public enum SetupScreen { Loading, Welcome, Connect, Authorize, Repository, Folder, Dashboard, Settings }
public enum SettingsSection { General, GitHub, Saves }
public enum ConnectionJourney { Onboarding, Connect, Reconnect, ChangeRepository }

/// <summary>Navigation only: authentication, preferences and inspection retain their own boundaries.</summary>
public sealed class SetupWorkflowViewModel : INotifyPropertyChanged, IDisposable
{
    public MainWindowViewModel Local { get; }
    public GitHubConnectionViewModel GitHub { get; }
    public SetupScreen Screen { get; private set; } = SetupScreen.Loading;
    public SettingsSection Section { get; private set; } = SettingsSection.GitHub;
    public ConnectionJourney Journey { get; private set; }
    public bool DisconnectConfirmation { get; private set; }
    public string BrowserNotice { get; private set; } = "";
    public bool HasBrowserNotice => BrowserNotice.Length > 0;
    private bool disposed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public SetupWorkflowViewModel(MainWindowViewModel local)
    {
        Local = local;
        GitHub = local.GitHub ?? throw new ArgumentException("GitHub connection is required.", nameof(local));
        Local.PropertyChanged += ChildChanged;
        GitHub.PropertyChanged += ChildChanged;
    }

    public bool IsLoading => Screen == SetupScreen.Loading;
    public bool IsWelcome => Screen == SetupScreen.Welcome;
    public bool IsConnect => Screen == SetupScreen.Connect;
    public bool IsAuthorize => Screen == SetupScreen.Authorize;
    public bool IsRepository => Screen == SetupScreen.Repository;
    public bool IsFolder => Screen == SetupScreen.Folder;
    public bool IsDashboard => Screen == SetupScreen.Dashboard;
    public bool IsSettings => Screen == SetupScreen.Settings;
    public bool IsGeneralSettings => IsSettings && Section == SettingsSection.General;
    public bool IsGitHubSettings => IsSettings && Section == SettingsSection.GitHub;
    public bool IsSavesSettings => IsSettings && Section == SettingsSection.Saves;
    public bool ShowFolder => IsFolder || IsDashboard || IsSavesSettings;
    public bool ShowFolderEditor => IsFolder || IsSavesSettings || Local.Manifest?.IsValid != true;
    public bool ShowGitHubNotice => !GitHub.LastOperationSucceeded || GitHub.IsBusy;
    public bool CanContinueFolder => Local.Manifest?.IsValid == true && !Local.IsBusy && GitHub.SelectedRepository is not null;
    public bool ShowAuthorizationRetry => IsAuthorize && !GitHub.IsBusy;
    public bool CanUseLocalOnly => !GitHub.IsConfigured;
    public bool ShowDisconnected => !GitHub.IsConnected;
    public string DashboardConnectionStatus => GitHub.IsConnected ? "GitHub Connected" : "GitHub Not Connected";
    public string SyncStatus => Local.Manifest?.IsValid != true || !GitHub.IsConnected || GitHub.SelectedRepository is null
        ? "Needs attention" : "Not yet synchronized";
    public string FolderExplanation => Local.ShowEmpty
        ? "This folder is empty. Downloading saves will be available when synchronization is added."
        : "Your files stay here. PD2 Cloud will compare them with the cloud before any future synchronization.";
    public string RepositoryName => GitHub.SelectedRepository?.FullName ?? "Choose a repository in Settings → GitHub.";
    public string RepositorySafety => "Only repository access has been checked. Its contents have not been checked yet. Future synchronization will check for existing PD2 Cloud saves or unrelated files before making changes.";

    public async Task InitializeAsync()
    {
        Journey = ConnectionJourney.Onboarding;
        var connectionRestore = GitHub.InitializeAsync();
        await Local.InitializeAsync();
        if (!disposed && Local.SetupCompleted) Navigate(SetupScreen.Dashboard);
        await connectionRestore;
        if (disposed || Local.SetupCompleted) return;
        Navigate(GitHub.SelectedRepository is not null
            ? SetupScreen.Folder : GitHub.IsConnected ? SetupScreen.Repository : Local.SetupStarted ? SetupScreen.Connect : SetupScreen.Welcome);
    }

    public async Task GetStartedAsync()
    {
        await Local.StartSetupAsync();
        Navigate(GitHub.IsConnected ? SetupScreen.Repository : SetupScreen.Connect);
    }
    public void OpenSettings(SettingsSection section = SettingsSection.GitHub) { Section = section; Navigate(SetupScreen.Settings); }
    public void Dashboard() => Navigate(SetupScreen.Dashboard);
    public void BeginConnection(bool reconnect = false)
    {
        if (IsSettings || Journey != ConnectionJourney.Onboarding)
            Journey = reconnect ? ConnectionJourney.Reconnect : ConnectionJourney.Connect;
        Navigate(SetupScreen.Connect);
    }
    public async Task ChangeRepositoryAsync()
    {
        Journey = ConnectionJourney.ChangeRepository;
        Navigate(SetupScreen.Repository);
        await GitHub.ReloadAsync();
    }
    public async Task AuthorizeAsync()
    {
        if (!GitHub.CanConnect) return;
        var previous = GitHub.SelectedRepository;
        Navigate(SetupScreen.Authorize);
        await GitHub.ConnectAsync();
        if (disposed || !GitHub.LastOperationSucceeded || !GitHub.IsConnected) { Notify(); return; }
        if (Journey == ConnectionJourney.Reconnect && previous is not null)
        {
            GitHub.Candidate = GitHub.Repositories.SingleOrDefault(r => r.Id == previous.Id);
            if (GitHub.Candidate is not null)
            {
                await GitHub.SelectAsync();
                if (GitHub.LastOperationSucceeded) { OpenSettings(); return; }
            }
        }
        Navigate(SetupScreen.Repository);
    }
    public async Task AcceptRepositoryAsync()
    {
        if (!GitHub.CanSelect) return;
        await GitHub.SelectAsync();
        if (disposed || !GitHub.LastOperationSucceeded || GitHub.SelectedRepository is null) return;
        // No comparison result survives pairing changes. No cloud verifier exists yet.
        if (Journey == ConnectionJourney.Onboarding) Navigate(SetupScreen.Folder);
        else OpenSettings();
    }
    public async Task CompleteAsync()
    {
        if (!CanContinueFolder) return;
        await Local.CompleteSetupAsync();
        if (!disposed && Local.SetupCompleted) Navigate(SetupScreen.Dashboard);
    }
    public async Task RefreshAsync() => await Task.WhenAll(Local.RefreshAsync(), GitHub.ReloadAsync());
    public void RequestDisconnect() { DisconnectConfirmation = true; Notify(); }
    public void CancelDisconnect() { DisconnectConfirmation = false; Notify(); }
    public async Task DisconnectAsync()
    {
        if (!DisconnectConfirmation || GitHub.IsBusy) return;
        await GitHub.DisconnectAsync();
        DisconnectConfirmation = false;
        Notify();
    }
    public async Task BackAsync()
    {
        if (GitHub.IsBusy) { GitHub.Cancel(); return; }
        if (Journey != ConnectionJourney.Onboarding) { OpenSettings(); return; }
        if (IsAuthorize)
        {
            // Authorization may have reached secure storage just as Cancel was pressed.
            await GitHub.ReloadAsync();
            Navigate(GitHub.IsConnected ? SetupScreen.Repository : SetupScreen.Connect);
        }
        else Navigate(IsFolder ? SetupScreen.Repository : IsRepository ? SetupScreen.Connect : SetupScreen.Welcome);
    }
    public void SetBrowserNotice(string message) { BrowserNotice = message; Notify(); }
    private void Navigate(SetupScreen screen) { if (disposed) return; Screen = screen; BrowserNotice = ""; DisconnectConfirmation = false; Notify(); }
    private void ChildChanged(object? sender, PropertyChangedEventArgs e) => Notify();
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose()
    {
        disposed = true;
        Local.PropertyChanged -= ChildChanged;
        GitHub.PropertyChanged -= ChildChanged;
        Local.Dispose();
    }
}
