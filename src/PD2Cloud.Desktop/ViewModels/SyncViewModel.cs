using System.ComponentModel;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;
using PD2Cloud.GitHub;
using PD2Cloud.Local;
using PD2Cloud.Remote;

namespace PD2Cloud.Desktop.ViewModels;

public enum SyncState { Unavailable, NothingToSync, InSync, Upload, Download, Initialize, Conflict }

/// <summary>
/// One Sync action. The local folder and the verified cloud collection are compared with the files recorded at the
/// last sync: only local changes upload, only cloud changes download (after a local backup), and changes on both
/// sides are left to Restore. Empty cloud storage is set up by the first upload. Restore pulls or pushes on request,
/// and push can replace cloud saves that can no longer be verified.
/// </summary>
public sealed class SyncViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MainWindowViewModel local;
    private readonly CloudInspectionViewModel cloud;
    private readonly GitHubConnectionViewModel gitHub;
    private readonly IUploadOperations? operations;
    private CancellationTokenSource? operation;
    private bool disposed;
    private string message = "";
    private bool messageIsError;

    public SyncViewModel(MainWindowViewModel local, CloudInspectionViewModel cloud, GitHubConnectionViewModel gitHub, IUploadOperations? operations = null)
    {
        this.local = local;
        this.cloud = cloud;
        this.gitHub = gitHub;
        this.operations = operations;
        local.PropertyChanged += ChildChanged;
        cloud.PropertyChanged += ChildChanged;
        gitHub.PropertyChanged += ChildChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsAvailable => operations is not null;
    public bool IsBusy => operation is not null;

    /// <summary>The stored record of the last sync, whichever folder or repository it belongs to.</summary>
    public LocalSyncState? LastSynced { get; private set; }

    /// <summary>The last sync record for the current folder and repository, or null.</summary>
    public LocalSyncState? Baseline => LastSynced is { } state && gitHub.SelectedRepository is { } repository
        && local.SelectedPath is { } root && state.RepositoryIdentity == repository.FullName
        && state.Branch == repository.DefaultBranch && state.LocalRootPath == Canonical(root) ? state : null;

    public SyncState State => Evaluate().State;
    public string BlockedReason => Evaluate().Reason;
    public bool CanSync => operations is not null && !IsBusy
        && State is SyncState.Upload or SyncState.Download or SyncState.Initialize or SyncState.Conflict;
    public bool IsInSync => !IsBusy && State == SyncState.InSync;

    public string StatusTitle => IsBusy ? "Syncing…"
        : local.IsBusy || cloud.IsBusy ? "Checking…"
        : State switch
        {
            SyncState.InSync => "In sync",
            SyncState.NothingToSync => "Nothing to sync yet",
            SyncState.Unavailable => "Needs attention",
            _ => "Unsynced"
        };

    public string StatusDetail => State switch
    {
        SyncState.InSync => "Your saves match the cloud copy.",
        SyncState.Upload => "Your saves changed on this PC. Sync uploads them to the cloud.",
        SyncState.Download => "The cloud has newer saves. Sync downloads them to this PC; your current files are backed up first.",
        SyncState.Initialize => "Cloud storage is not set up yet. Sync sets it up and uploads your saves.",
        SyncState.Conflict => "Your saves changed on this PC and in the cloud since the last sync. Sync opens Restore so you can choose which copy to keep.",
        _ => BlockedReason
    };

    public string LastSyncedLine => Baseline is { } baseline
        ? $"Last synced {baseline.SynchronizedAt.ToLocalTime():dd/MM/yyyy HH:mm}" : "";

    // Restore: an explicit whole-collection pull or push, whatever Sync would choose.
    private CloudReadResult? CurrentCloud => gitHub.SelectedRepository is { } repository && !cloud.IsBusy
        && cloud.Result is { } result && result.Repository == repository ? result : null;
    private bool LocalReady => local.Manifest?.IsValid == true && local.SelectedPath is not null && !local.IsBusy;

    public bool CanPull => operations is not null && !IsBusy && LocalReady
        && CurrentCloud is { Content: RepositoryContent.ManagedCollection, Collection.Manifest: not null };
    public bool CanPush => operations is not null && !IsBusy && LocalReady && local.Files.Count > 0
        && CurrentCloud is
        {
            Content: RepositoryContent.ManagedCollection or RepositoryContent.EmptyRepository
            or RepositoryContent.Unrelated or RepositoryContent.Incompatible
        };
    public bool CanRestore => CanPull || CanPush;
    public bool IsCloudUnreadable => CurrentCloud?.Content == RepositoryContent.Incompatible;

    public string PullReason => CanPull || IsBusy ? ""
        : !LocalReady ? "Check your save folder first."
        : CurrentCloud is null ? "Refresh to check the cloud saves."
        : CurrentCloud.Content == RepositoryContent.Incompatible ? "The cloud saves can't be verified, so they can't be downloaded."
        : "There are no cloud saves to download yet.";
    public string PushReason => CanPush || IsBusy ? ""
        : !LocalReady ? "Check your save folder first."
        : local.Files.Count == 0 ? "Your save folder is empty, so there is nothing to upload."
        : CurrentCloud is null ? "Refresh to check the cloud saves."
        : "The repository can't be written right now. Refresh and try again.";
    public string RestoreSummary
    {
        get
        {
            var cloudFiles = CurrentCloud?.Collection?.Manifest?.Files.Count;
            return $"{Files(local.Files.Count)} on this PC · " + (cloudFiles is { } count ? $"{Files(count)} in the cloud"
                : IsCloudUnreadable ? "cloud saves can't be read" : "no cloud saves yet");
        }
    }

    public string Message => message;
    public bool HasMessage => message.Length > 0;
    public bool IsMessageError => messageIsError;

    private (SyncState State, string Reason) Evaluate()
    {
        if (local.Manifest?.IsValid != true || local.SelectedPath is null) return (SyncState.Unavailable, "Check your save folder first.");
        if (gitHub.SelectedRepository is not { } repository) return (SyncState.Unavailable, "Choose a repository in Settings → GitHub.");
        if (cloud.IsBusy) return (SyncState.Unavailable, "Checking the cloud saves…");
        if (cloud.Result is not { } result || result.Repository != repository) return (SyncState.Unavailable, cloud.Notice);
        switch (result.Content)
        {
            case RepositoryContent.EmptyRepository or RepositoryContent.Unrelated:
                return local.Files.Count > 0
                    ? (SyncState.Initialize, "")
                    : (SyncState.NothingToSync, "Your save folder and cloud storage are both empty. Add your saves, then sync.");
            case RepositoryContent.ManagedCollection when result.Collection?.Manifest is { } remote:
                return (SyncPlanner.Plan(local.Files, remote.Files, Baseline?.Files) switch
                {
                    SyncAction.InSync => SyncState.InSync,
                    SyncAction.Upload => SyncState.Upload,
                    SyncAction.Download => SyncState.Download,
                    _ => SyncState.Conflict
                }, "");
            case RepositoryContent.Incompatible:
                return (SyncState.Unavailable, "The cloud saves can't be read: they were changed outside PD2 Cloud (for example, edited on GitHub) or by a newer version. Use Restore to replace them with this PC's saves.");
            default:
                return (SyncState.Unavailable, result.Message);
        }
    }

    public async Task RefreshStateAsync(CancellationToken ct = default)
    {
        if (operations is null || disposed) return;
        try
        {
            var (state, _) = await operations.LoadSyncStateAsync(ct);
            if (disposed) return;
            LastSynced = state;
        }
        catch (OperationCanceledException) { return; }
        Notify();
    }

    /// <summary>
    /// Records a sync when both sides already match but the record is missing or stale, e.g. after an upload whose
    /// response was lost, or when another PC already uploaded identical saves.
    /// </summary>
    public async Task RecordMatchAsync()
    {
        if (operations is null || disposed || IsBusy || State != SyncState.InSync) return;
        var repository = gitHub.SelectedRepository!;
        var result = cloud.Result!;
        var remote = result.Collection!.Manifest!;
        if (Baseline is { } baseline && baseline.LastSynchronizedCommit == result.Commit && SyncPlanner.SameFiles(baseline.Files, remote.Files)) return;
        try
        {
            await operations.SaveSyncStateAsync(new LocalSyncState(1, repository.FullName, repository.DefaultBranch, result.Commit!,
                Canonical(local.SelectedPath!), remote.Files, DateTimeOffset.UtcNow, remote.CollectionFingerprint!), CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return; }
        await RefreshStateAsync();
    }

    /// <summary>
    /// Runs the action the current state calls for. Returns true when files may have been transferred. A conflict is
    /// never decided here; the caller opens Restore instead.
    /// </summary>
    public async Task<bool> SyncAsync()
    {
        if (!CanSync || disposed || State == SyncState.Conflict) return false;
        return await RunAsync(State == SyncState.Download ? DownloadAsync : UploadAsync);
    }

    /// <summary>Replaces this PC's saves with the verified cloud saves, after a local backup.</summary>
    public async Task<bool> PullAsync() => CanPull && !disposed && await RunAsync(DownloadAsync);

    /// <summary>Replaces the cloud saves with this PC's saves, setting up or repairing cloud storage as needed.</summary>
    public async Task<bool> PushAsync() => CanPush && !disposed && await RunAsync(UploadAsync);

    public void ClearMessage()
    {
        if (!HasMessage) return;
        SetMessage("", false);
        Notify();
    }

    private async Task<bool> RunAsync(Func<CancellationToken, Task> action)
    {
        if (operations is null || disposed) return false;
        using var current = new CancellationTokenSource();
        operation = current;
        SetMessage("", false);
        Notify();
        try
        {
            await action(current.Token);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            if (!disposed) SetMessage("Sync cancelled.", true);
        }
        catch (Exception ex) when (ex is RecoveryBlockedException or ConnectionException)
        {
            if (!disposed) SetMessage(ex.Message, true);
        }
        catch (Exception ex)
        {
            if (!disposed) SetMessage("Sync did not finish." + Diagnostic(ex), true);
        }
        finally
        {
            if (ReferenceEquals(operation, current)) operation = null;
            if (!disposed) Notify();
        }
        return true;
    }

    private async Task UploadAsync(CancellationToken ct)
    {
        if (local.Files.Count == 0)
        {
            SetMessage("Your save folder is empty, so there is nothing to upload.", true);
            return;
        }
        var repository = gitHub.SelectedRepository!;
        var result = cloud.Result!;
        var fingerprint = local.Manifest!.CollectionFingerprint!;
        var bootstrap = result.Content switch
        {
            RepositoryContent.Unrelated => RepositoryBootstrapCase.Unrelated,
            RepositoryContent.EmptyRepository => result.Commit is null ? RepositoryBootstrapCase.UnbornRepository : RepositoryBootstrapCase.EmptyTree,
            RepositoryContent.Incompatible => RepositoryBootstrapCase.Repair,
            _ => RepositoryBootstrapCase.None
        };
        var prepared = await operations!.PrepareAsync(local.SelectedPath!, ct);
        if (disposed) return;
        if (prepared.CollectionFingerprint != fingerprint)
        {
            SetMessage("Your saves changed while syncing. Sync again.", true);
            return;
        }
        var upload = await operations.UploadAsync(new UploadRequest(prepared.OperationId, prepared.SourceRoot,
            prepared.CollectionFingerprint, repository, result.Commit ?? "", bootstrap), ct);
        if (disposed) return;
        var count = Files(prepared.Files.Count);
        switch (upload.Status)
        {
            case UploadStatus.Published:
                SetMessage($"Uploaded {count} to the cloud.", false);
                break;
            case UploadStatus.PublishedSourceChanged:
                SetMessage($"Uploaded {count}, but your saves changed during the upload. Sync again to upload the newest files.", true);
                break;
            default:
                SetMessage("The cloud saves changed during the upload, so nothing was overwritten. Sync again.", true);
                break;
        }
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        var result = cloud.Result!;
        var remote = result.Collection!.Manifest!;
        var pulled = await operations!.PullAsync(new PullRequest(local.SelectedPath!, gitHub.SelectedRepository!,
            result.Commit!, remote.CollectionFingerprint!), ct);
        if (disposed) return;
        SetMessage(pulled.BaselineRecorded
            ? $"Downloaded {Files(pulled.FileCount)} from the cloud. Your previous saves were backed up to {pulled.BackupDirectory}."
            : $"Downloaded {Files(pulled.FileCount)} from the cloud, but the sync record could not be saved. Your previous saves were backed up to {pulled.BackupDirectory}.",
            !pulled.BaselineRecorded);
    }

    private void SetMessage(string text, bool error)
    {
        message = text;
        messageIsError = error;
    }

    // Bounded, non-secret diagnostic text so a failure is never silent. GitHub errors are already sanitized.
    private static string Diagnostic(Exception error)
    {
        var text = error.Message;
        return string.IsNullOrWhiteSpace(text) ? "" : " Details: " + (text.Length > 240 ? text[..240] : text);
    }

    private static string Files(int count) => $"{count} {(count == 1 ? "file" : "files")}";
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public void Cancel() => operation?.Cancel();
    private void ChildChanged(object? sender, PropertyChangedEventArgs e) => Notify();
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public void Dispose()
    {
        disposed = true;
        local.PropertyChanged -= ChildChanged;
        cloud.PropertyChanged -= ChildChanged;
        gitHub.PropertyChanged -= ChildChanged;
        operation?.Cancel();
        operation = null;
    }
}
