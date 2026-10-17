using System.ComponentModel;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;
using PD2Cloud.Remote;

namespace PD2Cloud.Desktop.ViewModels;

public enum SetupScreen { Loading, Welcome, Connect, Authorize, Repository, Folder, Dashboard, Files, Settings, UploadReview, Recovery }
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
    public bool IsUploadReview => Screen == SetupScreen.UploadReview;
    public bool IsRecovery => Screen == SetupScreen.Recovery;
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
    public IReadOnlyList<FileGroupHeader> ReviewFileGroups => FileBrowser.Group(Sync.ReviewFiles, null);

    // Shell chrome state.
    public bool IsSavesNavActive => IsDashboard || IsFiles || IsUploadReview || IsRecovery;
    public bool CanNavigateSaves => Local.SetupCompleted || IsDashboard || IsFiles || IsUploadReview || IsRecovery;
    public bool ShowTitleBarSettings => IsDashboard;
    public bool ShowTitleBarBack => IsFiles || IsSettings || IsUploadReview || IsRecovery;
    public bool ShowSidebar => true;

    public string BaselineLine => Sync.Baseline is { } baseline
        ? $"Last published revision {ShortCommit(baseline.LastSynchronizedCommit)} on {baseline.SynchronizedAt.ToLocalTime():dd/MM/yyyy HH:mm}"
        : "";
    public bool HasBaselineLine => BaselineLine.Length > 0;
    public bool IsSyncHealthy => Sync.HasBaseline && Sync.BaselineAncestryValid;
    public string CloudFileSummary => FileBrowser.CountText(CloudFiles.Count);
    public string LocalFileSummary => FileBrowser.CountText(Local.Files.Count);
    public bool HasCloudNotice => Cloud.Notice.Length > 0;
    public string CloudRevisionLine => Cloud.Revision.Length > 0 ? Cloud.Revision : "";
    public bool HasCloudRevisionLine => CloudRevisionLine.Length > 0;
    private static string ShortCommit(string commit) => string.IsNullOrEmpty(commit) ? "" : commit[..Math.Min(7, commit.Length)];
    public string DashboardConnectionStatus => GitHub.IsConnected ? "GitHub Connected" : "GitHub Not Connected";
    public string SyncStatus => Sync.HasBaseline
        ? Sync.BaselineAncestryValid ? "Published — not a verified synchronization" : "Uploaded revision no longer in the remote history"
        : Local.Manifest?.IsValid != true || !GitHub.IsConnected || GitHub.SelectedRepository is null || Cloud.Result is null
            ? "Needs attention"
        : Cloud.Result.Content == RepositoryContent.Incompatible ? "Needs attention"
        : Sync.IsAvailable && Sync.BootstrapCase != RepositoryBootstrapCase.None ? Sync.BootstrapCase switch
        {
            RepositoryBootstrapCase.UnbornRepository => "Ready to initialize after review",
            RepositoryBootstrapCase.EmptyTree => "Ready to create the first collection after review",
            RepositoryBootstrapCase.Unrelated => "Ready to adopt after review",
            _ => "Not yet synchronized"
        }
        : "Not yet synchronized";
    public CollectionComparison? Comparison => Cloud.Compare(Local.Manifest, GitHub.SelectedRepository);
    public string ComparisonStatus => Comparison?.Decision switch
    {
        ComparisonDecision.NoTransfer => "Local and cloud files match at the checked revision. This does not establish a successful synchronization.",
        ComparisonDecision.InitialUpload => "Local files are ready for an initial upload. Sync All shows exactly what will be published before anything is sent.",
        ComparisonDecision.InitialDownload => "Cloud files are available for a future initial download. The local folder is empty.",
        ComparisonDecision.Conflict => "Local and cloud collections differ. Both must be preserved for explicit review before synchronization.",
        ComparisonDecision.Blocked => "Comparison is blocked by invalid or unverified inputs.",
        _ => Sync.BootstrapCase != RepositoryBootstrapCase.None
            ? "This repository can be bootstrapped after an explicit review. Nothing is written until you confirm."
            : "No verified collection comparison is available. Synchronization remains disabled."
    };
    public string FolderExplanation => Local.ShowEmpty
        ? "This folder is empty. Downloading saves will be available when synchronization is added."
        : "Your files stay here. PD2 Cloud will compare them with the cloud before any future synchronization.";
    public string RepositoryName => GitHub.SelectedRepository?.FullName ?? "Choose a repository in Settings → GitHub.";
    public string RepositorySafety => "Selecting a repository saves the choice and checks its contents without making changes. Sync All only ever publishes a reviewed initial upload into a verified empty managed collection, or explicitly initializes/adopts an unborn, empty-tree or unrelated repository after you review and confirm the exact case. Incompatible PD2 Cloud data stays blocked and your local files are never replaced.";
    public string SyncActionLabel => Sync.BootstrapCase switch
    {
        RepositoryBootstrapCase.UnbornRepository => "Initialize Repository",
        RepositoryBootstrapCase.EmptyTree => "Create First Collection",
        RepositoryBootstrapCase.Unrelated => "Review Adoption",
        _ => "Sync All"
    };

    // Reviewed upload: Sync All stays enabled only for a populated local folder into a verified empty managed collection.
    public bool ShowSyncAction => Sync.IsAvailable;
    public bool CanSyncAll => !disposed && Sync.CanOfferUpload;
    public string SyncAllReason => disposed ? "" : Sync.OfferBlockedReason;
    public bool HasSyncAllReason => SyncAllReason.Length > 0;
    public string SyncNotice => Sync.StatusLine;
    public bool HasSyncNotice => Sync.HasStatusLine;
    public bool HasRecoveryEvidence => Sync.HasRecoveryEvidence;
    public bool CanAssessRecovery => !disposed && IsRecovery && Sync.IsAvailable && Sync.HasRecoveryEvidence && !Sync.IsBusy && GitHub.SelectedRepository is not null;

    public void BeginUpload()
    {
        if (!Sync.StartReview()) { Notify(); return; }
        Navigate(SetupScreen.UploadReview);
    }
    public async Task ConfirmUploadAsync()
    {
        await Sync.ConfirmAsync();
        if (disposed) return;
        // A publication may have changed the remote revision and local baseline; refresh both honestly.
        await Local.RefreshAsync();
        await CheckCloudAsync();
        await Sync.RefreshStateAsync();
        Notify();
    }
    public async Task OpenRecoveryAsync()
    {
        Navigate(SetupScreen.Recovery);
        await Sync.RefreshStateAsync();
        if (disposed || !Sync.HasRecoveryEvidence) { Notify(); return; }
        await Sync.AssessRecoveryAsync();
        Notify();
    }
    public async Task AssessRecoveryAsync()
    {
        if (!CanAssessRecovery) return;
        await Sync.AssessRecoveryAsync();
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
    }

    public async Task GetStartedAsync()
    {
        await Local.StartSetupAsync();
        Navigate(GitHub.IsConnected ? SetupScreen.Repository : SetupScreen.Connect);
    }
    public void OpenSettings(SettingsSection section = SettingsSection.GitHub) { Section = section; Navigate(SetupScreen.Settings); }
    public void OpenLocalFiles() { if (!CanOpenLocalFiles) return; FilesScope = FileScope.Local; Navigate(SetupScreen.Files); }
    public void OpenCloudFiles() { if (!CanOpenCloudFiles) return; FilesScope = FileScope.Cloud; Navigate(SetupScreen.Files); }
    public void Dashboard() { Sync.CloseReview(); Navigate(SetupScreen.Dashboard); }
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
        Cloud.Invalidate();
        var checkConnection = !GitHub.IsBusy;
        await Task.WhenAll(Local.RefreshAsync(), GitHub.ReloadAsync());
        if (checkConnection) await CheckCloudAsync();
        if (!disposed) await Sync.RefreshStateAsync();
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
        if (IsUploadReview) { if (Sync.IsBusy) { Sync.Cancel(); return; } Dashboard(); return; }
        if (IsRecovery) { if (Sync.IsBusy) { Sync.Cancel(); return; } Dashboard(); return; }
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
