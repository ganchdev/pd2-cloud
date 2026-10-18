using System.ComponentModel;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;
using PD2Cloud.Remote;

namespace PD2Cloud.Desktop.ViewModels;

public enum SetupScreen { Loading, Welcome, Connect, Authorize, Repository, Folder, Dashboard, Files, Settings }
public enum SettingsSection { General, GitHub, Saves }
public enum ConnectionJourney { Onboarding, Connect, Reconnect, ChangeRepository }

/// <summary>Navigation only: authentication, preferences and inspection retain their own boundaries.</summary>
public sealed class SetupWorkflowViewModel : INotifyPropertyChanged, IDisposable
{
    public MainWindowViewModel Local { get; }
    public GitHubConnectionViewModel GitHub { get; }
    public CloudInspectionViewModel Cloud { get; }
    public SyncViewModel Sync { get; }
    public SetupScreen Screen { get; private set; } = SetupScreen.Loading;
    public SettingsSection Section { get; private set; } = SettingsSection.GitHub;
    public ConnectionJourney Journey { get; private set; }
    public bool DisconnectConfirmation { get; private set; }
    public string BrowserNotice { get; private set; } = "";
    public bool HasBrowserNotice => BrowserNotice.Length > 0;
    private bool disposed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public SetupWorkflowViewModel(MainWindowViewModel local, IUploadOperations? uploads = null)
    {
        Local = local;
        GitHub = local.GitHub ?? throw new ArgumentException("GitHub connection is required.", nameof(local));
        Cloud = new CloudInspectionViewModel(GitHub.ReadCloudAsync);
        Sync = new SyncViewModel(Local, Cloud, GitHub, uploads);
        Local.PropertyChanged += ChildChanged;
        GitHub.PropertyChanged += ChildChanged;
        Cloud.PropertyChanged += CloudChanged;
        Sync.PropertyChanged += CloudChanged;
    }

    public bool IsLoading => Screen == SetupScreen.Loading;
    public bool IsWelcome => Screen == SetupScreen.Welcome;
    public bool IsConnect => Screen == SetupScreen.Connect;
    public bool IsAuthorize => Screen == SetupScreen.Authorize;
    public bool IsRepository => Screen == SetupScreen.Repository;
    public bool IsFolder => Screen == SetupScreen.Folder;
    public bool IsDashboard => Screen == SetupScreen.Dashboard;
    public bool IsSettings => Screen == SetupScreen.Settings;
    public bool IsFiles => Screen == SetupScreen.Files;
    public bool IsGeneralSettings => IsSettings && Section == SettingsSection.General;
    public bool IsGitHubSettings => IsSettings && Section == SettingsSection.GitHub;
    public bool IsSavesSettings => IsSettings && Section == SettingsSection.Saves;
    public bool ShowFolder => IsFolder || IsDashboard || IsFiles || IsSavesSettings;
    public bool ShowCloud => ShowFolder || IsGitHubSettings || IsRepository;
    public bool ShowFolderEditor => IsFolder || IsSavesSettings || Local.Manifest?.IsValid != true;
    public bool ShowInspectAction => Local.HasFolder && ShowFolderEditor;
    public bool ShowGitHubNotice => !GitHub.LastOperationSucceeded || GitHub.IsBusy;
    public bool CanContinueFolder => Local.Manifest?.IsValid == true && !Local.IsBusy && GitHub.SelectedRepository is not null;
    public bool ShowAuthorizationRetry => IsAuthorize && !GitHub.IsBusy;
    public bool CanUseLocalOnly => !GitHub.IsConfigured;
    public bool ShowDisconnected => !GitHub.IsConnected;

    // Read-only file browser, opened from the dashboard tiles and Saves settings.
    public FileScope FilesScope { get; private set; } = FileScope.Local;
    public bool IsLocalFiles => IsFiles && FilesScope == FileScope.Local;
    public bool IsCloudFiles => IsFiles && FilesScope == FileScope.Cloud;
    public bool CanOpenLocalFiles => Local.HasFolder;
    public bool CanOpenCloudFiles => Cloud.Result?.Collection?.Manifest is not null;
    public IReadOnlyList<FileEntry> CloudFiles => Cloud.Result?.Collection?.Manifest?.Files ?? [];
    public string FilesTitle => IsCloudFiles ? "Cloud Collection" : "Save Folder";
    public string FilesSubtitle => IsCloudFiles
        ? "These are the files in the verified cloud collection at the checked revision. PD2 Cloud only reads them."
        : "These are the files in your PD2 save folder. PD2 Cloud treats them as save data and does not modify them.";
    public string FilesPath => IsCloudFiles
        ? GitHub.SelectedRepository?.FullName ?? "No verified repository"
        : Local.SelectedPath ?? "No save folder selected";
    public bool HasFilesPath => IsCloudFiles ? GitHub.SelectedRepository is not null : Local.SelectedPath is not null;
    public string FilesInspectionLine => IsCloudFiles
        ? (Cloud.Revision.Length > 0 ? Cloud.Revision : "Cloud files have not been checked yet.")
        : Local.LastInspection;
    public bool HasFilesInspectionLine => FilesInspectionLine.Length > 0;
    public string? FilesFingerprint => IsCloudFiles ? Cloud.Result?.Collection?.Manifest?.CollectionFingerprint : Local.CollectionFingerprint;
    public bool HasFilesFingerprint => FilesFingerprint is not null;
    public string FilesEmptyText => IsCloudFiles
        ? "No verified cloud files are available. Check cloud files from Settings → GitHub, then refresh."
        : "This folder has no files yet. Add your Project Diablo 2 saves here, then run inspection again.";
    public IReadOnlyList<ValidationIssue> FilesIssues => IsCloudFiles ? [] : Local.Issues;
    public bool HasFilesIssues => FilesIssues.Count > 0;

    private object? fileGroupKey;
    private IReadOnlyList<FileGroupHeader> fileGroups = [];
    public IReadOnlyList<FileGroupHeader> FileGroups
    {
        get
        {
            var key = IsCloudFiles ? (object?)Cloud.Result : Local.Manifest;
            if (ReferenceEquals(key, fileGroupKey)) return fileGroups;
            fileGroupKey = key;
            fileGroups = IsCloudFiles ? FileBrowser.Group(CloudFiles, null) : FileBrowser.Group(Local.Files, Local.SelectedPath);
            return fileGroups;
        }
    }
    public bool HasFiles => FileGroups.Count > 0;
    public bool IsFilesEmpty => IsFiles && !HasFiles;

    private object? localGroupKey;
    private IReadOnlyList<FileGroupHeader> localFileGroups = [];
    public IReadOnlyList<FileGroupHeader> LocalFileGroups
    {
        get
        {
            if (ReferenceEquals(Local.Manifest, localGroupKey)) return localFileGroups;
            localGroupKey = Local.Manifest;
            localFileGroups = FileBrowser.Group(Local.Files, Local.SelectedPath);
            return localFileGroups;
        }
    }
    public bool HasLocalFiles => LocalFileGroups.Count > 0;
    public IReadOnlyList<FileRow> LocalRows => LocalFileGroups.SelectMany(group => group.Rows).ToArray();

    // Shell chrome state.
    public bool IsSavesNavActive => IsDashboard || IsFiles;
    public bool CanNavigateSaves => Local.SetupCompleted || IsDashboard || IsFiles;
    public bool ShowTitleBarSettings => IsDashboard;
    public bool ShowTitleBarBack => IsFiles || IsSettings;
    public bool ShowSidebar => true;

    public string BaselineLine => Sync.LastSyncedLine;
    public bool HasBaselineLine => BaselineLine.Length > 0;
    public bool IsSyncHealthy => Sync.IsInSync;
    public string CloudFileSummary => FileBrowser.CountText(CloudFiles.Count);
    public string LocalFileSummary => FileBrowser.CountText(Local.Files.Count);
    public bool HasCloudNotice => Cloud.Notice.Length > 0;
    public string CloudRevisionLine => Cloud.Revision.Length > 0 ? Cloud.Revision : "";
    public bool HasCloudRevisionLine => CloudRevisionLine.Length > 0;
    public string DashboardConnectionStatus => GitHub.IsConnected ? "GitHub Connected" : "GitHub Not Connected";
    public string SyncStatus => Sync.StatusTitle;
    public string SyncDetail => Sync.StatusDetail;
    public string FolderExplanation => Local.ShowEmpty
        ? "This folder is empty. Sync downloads your saves from the cloud when there are any."
        : "Your files stay here. Sync keeps them and the cloud copy the same.";
    public string RepositoryName => GitHub.SelectedRepository?.FullName ?? "Choose a repository in Settings → GitHub.";
    public string RepositorySafety => "Selecting a repository saves the choice and checks its contents without making changes. Sync stores your saves in a .pd2cloud folder in it; any other files in the repository are kept.";
    public bool IsDashboardChecking => IsDashboard && !disposed && (Local.IsBusy || Cloud.IsBusy || GitHub.IsBusy || Sync.IsBusy);

    // One Sync action: upload, download or set up cloud storage, as the comparison calls for.
    public bool ShowSyncAction => Sync.IsAvailable;
    public bool CanSync => !disposed && Sync.CanSync;
    public string SyncReason => disposed || Sync.CanSync ? "" : Sync.BlockedReason;
    public bool IsConflictChoiceOpen => Sync.IsConflictChoiceOpen;
    public string SyncMessage => Sync.Message;
    public bool HasSyncMessage => Sync.HasMessage;
    public bool IsSyncMessageError => Sync.IsMessageError;

    public async Task SyncAsync() => await AfterSyncAsync(await Sync.SyncAsync());
    public async Task KeepLocalAsync() => await AfterSyncAsync(await Sync.KeepLocalAsync());
    public async Task KeepCloudAsync() => await AfterSyncAsync(await Sync.KeepCloudAsync());
    public void CancelConflict() => Sync.CloseConflictChoice();

    // Re-inspect both sides so the dashboard shows the state actually reached.
    private async Task AfterSyncAsync(bool transferred)
    {
        if (disposed || !transferred) { Notify(); return; }
        await Local.RefreshAsync();
        await CheckCloudAsync();
        await Sync.RefreshStateAsync();
        await Sync.RecordMatchAsync();
        Notify();
    }

    public async Task InitializeAsync()
    {
        Journey = ConnectionJourney.Onboarding;
        var connectionRestore = GitHub.InitializeAsync();
        await Local.InitializeAsync();
        if (!disposed && Local.SetupCompleted) Navigate(SetupScreen.Dashboard);
        await connectionRestore;
        if (disposed) return;
        if (!Local.SetupCompleted) Navigate(GitHub.SelectedRepository is not null
            ? SetupScreen.Folder : GitHub.IsConnected ? SetupScreen.Repository : Local.SetupStarted ? SetupScreen.Connect : SetupScreen.Welcome);
        await CheckCloudAsync();
        await Sync.RefreshStateAsync();
        await Sync.RecordMatchAsync();
    }

    public async Task GetStartedAsync()
    {
        await Local.StartSetupAsync();
        Navigate(GitHub.IsConnected ? SetupScreen.Repository : SetupScreen.Connect);
    }
    public void OpenSettings(SettingsSection section = SettingsSection.GitHub) { Section = section; Navigate(SetupScreen.Settings); }
    public void OpenLocalFiles() { if (!CanOpenLocalFiles) return; FilesScope = FileScope.Local; Navigate(SetupScreen.Files); }
    public void OpenCloudFiles() { if (!CanOpenCloudFiles) return; FilesScope = FileScope.Cloud; Navigate(SetupScreen.Files); }
    public void Dashboard() { Sync.CloseConflictChoice(); Navigate(SetupScreen.Dashboard); }
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
                if (GitHub.LastOperationSucceeded) { OpenSettings(); await CheckCloudAsync(); return; }
            }
        }
        Navigate(SetupScreen.Repository);
    }
    public async Task AcceptRepositoryAsync()
    {
        if (!GitHub.CanSelect) return;
        await GitHub.SelectAsync();
        if (disposed || !GitHub.LastOperationSucceeded || GitHub.SelectedRepository is null) return;
        // Metadata operations invalidate previous cloud evidence before selection changes.
        if (Journey == ConnectionJourney.Onboarding) Navigate(SetupScreen.Folder);
        else OpenSettings();
        await CheckCloudAsync();
    }
    public async Task CompleteAsync()
    {
        if (!CanContinueFolder) return;
        await Local.CompleteSetupAsync();
        if (!disposed && Local.SetupCompleted) Navigate(SetupScreen.Dashboard);
    }
    public async Task RefreshAsync()
    {
        Sync.ClearMessage();
        Sync.CloseConflictChoice();
        Cloud.Invalidate();
        var checkConnection = !GitHub.IsBusy;
        await Task.WhenAll(Local.RefreshAsync(), GitHub.ReloadAsync());
        if (checkConnection) await CheckCloudAsync();
        if (disposed) return;
        await Sync.RefreshStateAsync();
        await Sync.RecordMatchAsync();
    }
    public bool CanCheckCloud => !disposed && !GitHub.IsBusy && !Cloud.IsBusy && GitHub.SelectedRepository is not null;
    public Task CheckCloudAsync() => CanCheckCloud && GitHub.SelectedRepository is { } selected
        ? Cloud.RefreshAsync(selected) : Task.CompletedTask;
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
        if (IsFiles) { Dashboard(); return; }
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
    private void ChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, GitHub) && (GitHub.IsBusy || Cloud.Result is { } result && result.Repository != GitHub.SelectedRepository)) Cloud.Invalidate();
        Notify();
    }
    private void CloudChanged(object? sender, PropertyChangedEventArgs e) => Notify();
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose()
    {
        disposed = true;
        Local.PropertyChanged -= ChildChanged;
        GitHub.PropertyChanged -= ChildChanged;
        Cloud.PropertyChanged -= CloudChanged;
        Sync.PropertyChanged -= CloudChanged;
        Sync.Dispose();
        Cloud.Dispose();
        Local.Dispose();
    }
}
