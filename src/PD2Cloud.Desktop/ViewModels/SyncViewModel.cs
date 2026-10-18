using System.ComponentModel;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;
using PD2Cloud.GitHub;
using PD2Cloud.Local;
using PD2Cloud.Remote;

namespace PD2Cloud.Desktop.ViewModels;

public enum SyncOutcome { None, Uploaded, UploadedSourceChanged, NotPublished, ConcurrentUpdate, Interrupted, Blocked, Cancelled, Pulled }
public enum ResolveChoice { None, Pull, Push }

/// <summary>
/// Explicit reviewed initial upload plus read-only publication-recovery review. It never writes source
/// files, never re-uploads on its own, never fabricates a baseline and never reports synchronization success.
/// </summary>
public sealed class SyncViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MainWindowViewModel local;
    private readonly CloudInspectionViewModel cloud;
    private readonly GitHubConnectionViewModel gitHub;
    private readonly IUploadOperations? operations;
    private CancellationTokenSource? operation;
    private bool disposed;

    private string reviewRoot = "";
    private string reviewRepository = "";
    private string reviewBranch = "";
    private string reviewCommit = "";
    private string reviewFingerprint = "";
    private IReadOnlyList<FileEntry> reviewFiles = [];
    private long reviewTotalBytes;
    private string reviewNotice = "";
    private string recoveryNotice = "";
    private RepositoryBootstrapCase reviewBootstrapCase = RepositoryBootstrapCase.None;
    private bool adoptionAcknowledged;
    private SyncOutcome outcome;
    private string resultTitle = "";
    private string resultMessage = "";
    private string resultCommit = "";
    private string resolveNotice = "";
    private string resolveResultTitle = "";
    private string resolveResultMessage = "";
    private bool hasResolveResult;

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

    // Published baseline ("last uploaded" note), always ancestry-verified before it is trusted.
    public LocalSyncState? Baseline { get; private set; }
    public bool BaselineAncestryValid { get; private set; }
    public string BaselineNotice { get; private set; } = "";
    public bool HasBaseline => Baseline is not null;

    // Retained publication evidence awaiting review.
    public bool HasRecoveryEvidence { get; private set; }
    public string RecoveryOperationId { get; private set; } = "";
    public UploadStatus? RecoveryStatus { get; private set; }
    private string recoveryMessage = "";
    public string RecoveryMessage => recoveryMessage;
    public string RecoveryNotice => recoveryNotice;
    public bool HasRecoveryStatus => RecoveryStatus is not null;
    public bool HasRecoveryNotice => recoveryNotice.Length > 0;
    public string RecoveryStatusTitle => RecoveryStatus switch
    {
        UploadStatus.Published => "Published (remote publication only)",
        UploadStatus.PublishedSourceChanged => "Published, but your files changed",
        UploadStatus.NotPublished => "Not published",
        UploadStatus.ConcurrentUpdate => "Repository changed",
        UploadStatus.Interrupted => "Interrupted",
        null => "",
        _ => "Reviewed"
    };
    public string RecoveryEvidenceLine => HasRecoveryEvidence
        ? $"Retained upload evidence for operation {RecoveryOperationId}." : "No retained upload evidence was found.";

    public bool IsReviewOpen { get; private set; }
    public string ReviewLocation => reviewRoot;
    public string ReviewRepository => reviewRepository;
    public string ReviewBranch => reviewBranch;
    public string ReviewCommit => reviewCommit;
    public IReadOnlyList<FileEntry> ReviewFiles => reviewFiles;
    public string ReviewFingerprint => reviewFingerprint;
    public long ReviewTotalBytes => reviewTotalBytes;
    public string ReviewSummary => $"{reviewFiles.Count} {(reviewFiles.Count == 1 ? "file" : "files")} · {reviewTotalBytes} bytes";
    public string ReviewRepositoryLine => $"{reviewRepository} · branch {reviewBranch} · revision {Short(reviewCommit)}";
    public string ReviewNotice => reviewNotice;
    public bool HasReviewNotice => reviewNotice.Length > 0;
    public bool IsBootstrapCase => reviewBootstrapCase != RepositoryBootstrapCase.None;
    public string ReviewBootstrapTitle => reviewBootstrapCase switch
    {
        RepositoryBootstrapCase.UnbornRepository => "Initialize this empty repository",
        RepositoryBootstrapCase.EmptyTree => "Create the first collection",
        RepositoryBootstrapCase.Unrelated => "Adopt this repository",
        _ => ""
    };
    public string ReviewBootstrapMessage => reviewBootstrapCase switch
    {
        RepositoryBootstrapCase.UnbornRepository => "This repository has no commits and no main branch. Confirming creates its first commit with the PD2 Cloud collection and then creates the main branch. This does not replace your local files.",
        RepositoryBootstrapCase.EmptyTree => "This repository's branch points at a commit with no files. Confirming adds the PD2 Cloud collection as a child of that commit without rewriting existing history.",
        RepositoryBootstrapCase.Unrelated => "This repository already contains unrelated files. Confirming adopts it by adding the PD2 Cloud collection while preserving every existing file, folder, mode and Git object exactly. Nothing unrelated is deleted or replaced.",
        _ => ""
    };
    public bool RequiresAdoptionAcknowledgement => reviewBootstrapCase == RepositoryBootstrapCase.Unrelated;
    public string ReviewRequirementNotice => IsReviewOpen && RequiresAdoptionAcknowledgement && !adoptionAcknowledged
        ? "Tick the adoption acknowledgement above to enable Confirm Upload." : "";
    public bool HasReviewRequirementNotice => ReviewRequirementNotice.Length > 0;
    public bool AdoptionAcknowledged
    {
        get => adoptionAcknowledged;
        set { if (adoptionAcknowledged == value) return; adoptionAcknowledged = value; Notify(); }
    }
    public SyncOutcome Outcome => outcome;
    public string ResultTitle => resultTitle;
    public string ResultMessage => resultMessage;
    public string ResultCommit => resultCommit;
    public bool HasResult => outcome is not SyncOutcome.None;

    public CollectionComparison? Comparison => cloud.Compare(local.Manifest, gitHub.SelectedRepository);

    /// <summary>The reviewed repository classification; None is the ordinary managed-empty initial upload.</summary>
    public RepositoryBootstrapCase BootstrapCase
    {
        get
        {
            if (gitHub.SelectedRepository is not { } repository || cloud.Result is not { } result || result.Repository != repository) return RepositoryBootstrapCase.None;
            if (result.Content == RepositoryContent.Unrelated) return RepositoryBootstrapCase.Unrelated;
            if (result.Content == RepositoryContent.EmptyRepository)
                return result.Commit is null ? RepositoryBootstrapCase.UnbornRepository : RepositoryBootstrapCase.EmptyTree;
            return RepositoryBootstrapCase.None;
        }
    }

    /// <summary>Enables the reviewed upload. Requires a populated local folder and a verified empty managed collection or an explicit bootstrap case.</summary>
    public bool CanOfferUpload => operations is not null && !IsBusy && !IsReviewOpen && !HasBaseline && !HasRecoveryEvidence
        && local.Manifest?.IsValid == true && local.Files.Count > 0 && local.SelectedPath is not null
        && gitHub.SelectedRepository is not null
        && (Comparison?.Decision == ComparisonDecision.InitialUpload || BootstrapCase != RepositoryBootstrapCase.None);

    public string OfferBlockedReason
    {
        get
        {
            if (operations is null) return "Upload is not configured in this build.";
            if (IsBusy) return "An upload is already in progress.";
            if (IsReviewOpen) return "Review the pending upload before starting another.";
            if (HasBaseline)
                return BaselineAncestryValid
                    ? "This folder already has a published upload. Additional uploads are not available yet."
                    : "The last uploaded revision is no longer part of the remote history. Review the retained evidence before uploading.";
            if (HasRecoveryEvidence) return "An earlier upload is waiting for review. Open Upload Recovery.";
            if (local.Manifest?.IsValid != true) return "Inspect a valid local folder first.";
            if (local.Files.Count == 0) return "The local folder is empty; there is nothing to upload.";
            if (gitHub.SelectedRepository is null) return "Choose a private repository in Settings → GitHub first.";
            if (cloud.Result is null) return "Check cloud files before uploading.";
            if (Comparison?.Decision == ComparisonDecision.InitialUpload || BootstrapCase != RepositoryBootstrapCase.None) return "";
            if (cloud.Result.Content == RepositoryContent.Incompatible)
                return "The PD2 Cloud namespace is incompatible, unsafe or incomplete. This repository is blocked.";
            return "Upload is only available for a populated local folder into an empty, verified cloud collection or an explicitly reviewed bootstrap.";
        }
    }

    /// <summary>True when the verified cloud collection and the local folder differ and a reviewed replacement can be offered.</summary>
    public bool CanResolve => operations is not null && !IsBusy && !IsReviewOpen && !HasRecoveryEvidence
        && (!HasBaseline || BaselineAncestryValid)
        && local.Manifest?.IsValid == true && local.SelectedPath is not null
        && gitHub.SelectedRepository is { } repository
        && cloud.Result is { Content: RepositoryContent.ManagedCollection, Collection.Manifest: not null } result
        && result.Repository == repository
        && Comparison?.Decision is ComparisonDecision.Conflict or ComparisonDecision.Upload or ComparisonDecision.Download
            or ComparisonDecision.MissingFileReview or ComparisonDecision.InitialDownload;

    public bool CanResolvePull => CanResolve && (cloud.Result?.Collection?.Manifest?.Files.Count ?? 0) > 0;
    public bool CanResolvePush => CanResolve && local.Files.Count > 0;

    public string ResolveReason
    {
        get
        {
            if (operations is null) return "Resolution is not configured in this build.";
            if (IsBusy) return "An operation is already in progress.";
            if (HasRecoveryEvidence) return "An earlier upload is waiting for review. Open Upload Recovery first.";
            if (HasBaseline && !BaselineAncestryValid) return "The last published revision is no longer part of the remote history. Review the retained evidence first.";
            if (local.Manifest?.IsValid != true) return "Inspect a valid local folder first.";
            if (gitHub.SelectedRepository is null) return "Choose a private repository in Settings → GitHub first.";
            if (cloud.Result is null) return "Check cloud files before resolving differences.";
            if (cloud.Result.Content != RepositoryContent.ManagedCollection) return "Resolve is available only for a verified managed cloud collection.";
            if (Comparison is { } comparison && comparison.Decision == ComparisonDecision.NoTransfer) return "Local and cloud files already match.";
            return "Local and cloud files do not differ in a way that Resolve can handle.";
        }
    }

    public string ResolveSummary
    {
        get
        {
            var localCount = local.Files.Count;
            var remoteCount = cloud.Result?.Collection?.Manifest?.Files.Count ?? 0;
            return $"{localCount} local {(localCount == 1 ? "file" : "files")} · {remoteCount} cloud {(remoteCount == 1 ? "file" : "files")} at revision {Short(cloud.Result?.Commit)}";
        }
    }
    public string ResolveBackupNotice => "Pull always writes a complete backup of the current local folder before replacing it. The backup is kept on this device.";
    public string ResolveNotice => resolveNotice;
    public bool HasResolveNotice => resolveNotice.Length > 0;
    public bool HasResolveResult => hasResolveResult;
    public string ResolveResultTitle => resolveResultTitle;
    public string ResolveResultMessage => resolveResultMessage;

    public string StatusLine
    {
        get
        {
            if (operations is null) return "";
            if (HasBaseline)
                return BaselineAncestryValid
                    ? $"Last synchronized revision {Short(Baseline!.LastSynchronizedCommit)} on {Baseline.SynchronizedAt.ToLocalTime():g}. Refresh to compare your current files with the checked cloud revision."
                    : "The last synchronized revision is no longer part of the remote history. Review the retained upload evidence.";
            if (HasRecoveryEvidence) return "An earlier upload is waiting for review. Open Upload Recovery.";
            if (CanOfferUpload) return BootstrapCase switch
            {
                RepositoryBootstrapCase.UnbornRepository => "This repository is empty. Sync All can create its first PD2 Cloud collection after your review.",
                RepositoryBootstrapCase.EmptyTree => "This repository's branch has no files. Sync All can add the first PD2 Cloud collection after your review.",
                RepositoryBootstrapCase.Unrelated => "This repository contains unrelated files. Sync All can adopt it only after your explicit confirmation.",
                _ => "Ready for your first upload. Sync All shows exactly what will be published."
            };
            return "";
        }
    }

    public bool HasStatusLine => StatusLine.Length > 0;

    /// <summary>Loads the published baseline (verifying ancestry) and any retained recovery evidence.</summary>
    public async Task RefreshStateAsync(CancellationToken ct = default)
    {
        if (operations is null || disposed) return;
        try
        {
            var (state, _) = await operations.LoadSyncStateAsync(ct);
            if (disposed) return;
            Baseline = state;
            BaselineAncestryValid = false;
            BaselineNotice = "";
            if (state is not null)
            {
                if (gitHub.IsConnected)
                {
                    try
                    {
                        var (ancestor, notice) = await operations.VerifyBaselineAncestryAsync(state, ct);
                        if (disposed) return;
                        BaselineAncestryValid = ancestor;
                        BaselineNotice = notice ?? "";
                    }
                    catch (Exception ex) when (ex is ConnectionException or RecoveryBlockedException)
                    {
                        BaselineNotice = "Could not verify the last published revision. Refresh after checking GitHub access.";
                    }
                }
                else
                {
                    BaselineNotice = "Reconnect to GitHub to verify the last published revision.";
                }
            }
            var recovery = await operations.ListRecoveryAsync(ct);
            if (disposed) return;
            HasRecoveryEvidence = recovery.Count > 0;
            RecoveryOperationId = recovery.Count > 0 ? recovery[0].OperationId : "";
            if (!HasRecoveryEvidence)
            {
                RecoveryStatus = null;
                recoveryMessage = "";
                recoveryNotice = "";
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or ConnectionException or UnauthorizedAccessException or InvalidOperationException)
        {
            RecoveryNoticeFromStorage(ex);
        }
        Notify();
    }

    private void RecoveryNoticeFromStorage(Exception ex)
    {
        recoveryNotice = ex is ConnectionException
            ? ex.Message
            : "Retained upload evidence could not be read. Close other app instances, check access, then refresh.";
    }

    /// <summary>Explicit reviewed pull: verified cloud bytes replace the local folder after a complete backup.</summary>
    public async Task ResolvePullAsync()
    {
        if (operations is null || disposed) return;
        if (!CanResolvePull)
        {
            SetResolveNotice(ResolveReason);
            Notify();
            return;
        }
        var repository = gitHub.SelectedRepository!;
        var cloudResult = cloud.Result!;
        var sourceRoot = local.SelectedPath!;
        var commit = cloudResult.Commit!;
        var fingerprint = cloudResult.Collection!.Manifest!.CollectionFingerprint!;
        using var current = new CancellationTokenSource();
        operation = current;
        resolveNotice = "Backing up your local files, then installing the verified cloud revision…";
        hasResolveResult = false;
        resolveResultTitle = "";
        resolveResultMessage = "";
        Notify();
        try
        {
            var result = await operations.PullAsync(new PullRequest(sourceRoot, repository, commit, fingerprint), current.Token);
            if (disposed) return;
            outcome = SyncOutcome.Pulled;
            hasResolveResult = true;
            resolveResultTitle = "Pulled cloud files";
            resolveResultMessage = $"Installed {result.FileCount} {(result.FileCount == 1 ? "file" : "files")} from revision {Short(result.Commit)} into your save folder. Your previous local files were backed up to {result.BackupDirectory}."
                + (result.BaselineRecorded ? " The checked revision was recorded locally as the last synchronized revision." : " The cloud files were installed, but the local sync note could not be saved.");
            resolveNotice = "";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            if (!disposed) SetResolveNotice("Pull cancelled. A complete backup was retained before any replacement.");
        }
        catch (RecoveryBlockedException ex)
        {
            if (!disposed) SetResolveNotice(ex.Message);
        }
        catch (ConnectionException ex)
        {
            if (!disposed) SetResolveNotice(ex.Message);
        }
        catch (Exception ex)
        {
            if (!disposed) SetResolveNotice("Pull did not finish. Your backup was retained." + Diagnostic(ex));
        }
        finally
        {
            if (ReferenceEquals(operation, current)) operation = null;
            if (!disposed)
            {
                await RefreshStateAsync();
                Notify();
            }
        }
    }

    /// <summary>Explicit reviewed push: the captured local collection replaces the managed cloud collection.</summary>
    public async Task ResolvePushAsync()
    {
        if (operations is null || disposed) return;
        if (!CanResolvePush)
        {
            SetResolveNotice(CanResolve ? "The local folder is empty; there is nothing to push." : ResolveReason);
            Notify();
            return;
        }
        var repository = gitHub.SelectedRepository!;
        var sourceRoot = local.SelectedPath!;
        var fingerprint = local.Manifest!.CollectionFingerprint!;
        var pinnedCommit = cloud.Result!.Commit!;
        using var current = new CancellationTokenSource();
        operation = current;
        resolveNotice = "Preparing a reviewed push of your local files…";
        hasResolveResult = false;
        resolveResultTitle = "";
        resolveResultMessage = "";
        Notify();
        try
        {
            var prepared = await operations.PrepareAsync(sourceRoot, current.Token);
            if (disposed || current.IsCancellationRequested) return;
            if (prepared.CollectionFingerprint != fingerprint)
            {
                SetResolveNotice("Your files changed while the push was being prepared. Refresh and resolve again.");
                return;
            }
            var result = await operations.UploadAsync(new UploadRequest(prepared.OperationId, prepared.SourceRoot,
                prepared.CollectionFingerprint, repository, pinnedCommit, RepositoryBootstrapCase.None, UploadMode.ReplaceManaged), current.Token);
            if (disposed) return;
            ApplyResolvePushResult(result);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            if (!disposed) SetResolveNotice("Push cancelled. Retained evidence is preserved; review Upload Recovery before trying again.");
        }
        catch (RecoveryBlockedException ex)
        {
            if (!disposed) SetResolveNotice(ex.Message);
        }
        catch (ConnectionException ex)
        {
            if (!disposed) SetResolveNotice(ex.Message);
        }
        catch (Exception ex)
        {
            if (!disposed) SetResolveNotice("Push did not finish. Retained evidence is preserved; review Upload Recovery. Your local files were not changed." + Diagnostic(ex));
        }
        finally
        {
            if (ReferenceEquals(operation, current)) operation = null;
            if (!disposed)
            {
                await RefreshStateAsync();
                Notify();
            }
        }
    }

    private void ApplyResolvePushResult(UploadResult result)
    {
        resultCommit = result.CandidateCommit ?? "";
        hasResolveResult = true;
        switch (result.Status)
        {
            case UploadStatus.Published:
                outcome = SyncOutcome.Uploaded;
                resolveResultTitle = "Pushed local files";
                resolveResultMessage = $"Published revision {Short(resultCommit)} to {reviewRepositoryOrSelected()} · {gitHub.SelectedRepository?.DefaultBranch}. The cloud collection now matches the reviewed local snapshot. This records remote publication, not a verified two-device synchronization.";
                break;
            case UploadStatus.PublishedSourceChanged:
                outcome = SyncOutcome.UploadedSourceChanged;
                resolveResultTitle = "Pushed, but your files changed";
                resolveResultMessage = $"Published revision {Short(resultCommit)}, but your local files changed during the push. The remote copy matches the reviewed snapshot; your newer edits were preserved.";
                break;
            case UploadStatus.NotPublished:
                outcome = SyncOutcome.NotPublished;
                resolveResultTitle = "Not pushed";
                resolveResultMessage = "The remote branch still points at the reviewed revision, so nothing was published. Review Upload Recovery before trying again.";
                break;
            case UploadStatus.ConcurrentUpdate:
                outcome = SyncOutcome.ConcurrentUpdate;
                resolveResultTitle = "Repository changed during push";
                resolveResultMessage = "Another change reached the repository, so the reviewed local snapshot was not published. Both versions are preserved. Review Upload Recovery, then refresh.";
                break;
            default:
                outcome = SyncOutcome.Interrupted;
                resolveResultTitle = "Push did not finish";
                resolveResultMessage = "The push was interrupted. Retained evidence is preserved; review Upload Recovery before trying again. Your local files were not changed.";
                break;
        }
    }

    private string reviewRepositoryOrSelected() => gitHub.SelectedRepository?.FullName ?? "your repository";

    private void SetResolveNotice(string message)
    {
        resolveNotice = message;
        hasResolveResult = false;
        resolveResultTitle = "";
        resolveResultMessage = "";
    }

    public bool BeginResolve()
    {
        if (!CanResolve) return false;
        resolveNotice = "";
        hasResolveResult = false;
        resolveResultTitle = "";
        resolveResultMessage = "";
        outcome = SyncOutcome.None;
        resultTitle = "";
        resultMessage = "";
        resultCommit = "";
        Notify();
        return true;
    }

    public bool StartReview()
    {
        if (!CanOfferUpload) return false;
        var repository = gitHub.SelectedRepository!;
        reviewRoot = local.SelectedPath!;
        reviewRepository = repository.FullName;
        reviewBranch = repository.DefaultBranch;
        reviewCommit = cloud.Result!.Commit ?? "";
        reviewFingerprint = local.Manifest!.CollectionFingerprint!;
        reviewFiles = local.Files.ToArray();
        reviewTotalBytes = local.Files.Sum(f => f.SizeBytes);
        reviewBootstrapCase = BootstrapCase;
        adoptionAcknowledged = false;
        reviewNotice = "";
        outcome = SyncOutcome.None;
        resultTitle = "";
        resultMessage = "";
        resultCommit = "";
        IsReviewOpen = true;
        Notify();
        return true;
    }

    public void CloseReview()
    {
        IsReviewOpen = false;
        outcome = SyncOutcome.None;
        resultTitle = "";
        resultMessage = "";
        resultCommit = "";
        reviewNotice = "";
        reviewBootstrapCase = RepositoryBootstrapCase.None;
        adoptionAcknowledged = false;
        Notify();
    }

    /// <summary>Confirmation stays available only while every reviewed input is unchanged (and, for adoption, explicitly acknowledged).</summary>
    public bool CanConfirm => IsReviewOpen && !IsBusy && outcome == SyncOutcome.None && !HasRecoveryEvidence
        && local.Manifest?.IsValid == true && local.SelectedPath == reviewRoot
        && local.Manifest.CollectionFingerprint == reviewFingerprint
        && gitHub.SelectedRepository is { } repository && repository.FullName == reviewRepository
        && repository.DefaultBranch == reviewBranch
        && cloud.Result is { } result && result.Repository == repository && (result.Commit ?? "") == reviewCommit
        && MatchesReviewedContent(result)
        && (!RequiresAdoptionAcknowledgement || adoptionAcknowledged);

    private bool MatchesReviewedContent(CloudReadResult result) => reviewBootstrapCase switch
    {
        RepositoryBootstrapCase.None => result.Content == RepositoryContent.ManagedCollection && result.Collection?.Manifest is { Files.Count: 0 },
        RepositoryBootstrapCase.UnbornRepository => result.Content == RepositoryContent.EmptyRepository && result.Commit is null,
        RepositoryBootstrapCase.EmptyTree => result.Content == RepositoryContent.EmptyRepository && result.Commit is not null,
        RepositoryBootstrapCase.Unrelated => result.Content == RepositoryContent.Unrelated,
        _ => false
    };

    public async Task ConfirmAsync()
    {
        if (operations is null || disposed) return;
        if (!CanConfirm)
        {
            if (IsReviewOpen && !IsBusy && outcome == SyncOutcome.None)
            {
                SetReviewNotice("Reviewed inputs changed. Refresh and review the upload again.");
                Notify();
            }
            return;
        }
        var repository = gitHub.SelectedRepository!;
        var sourceRoot = reviewRoot;
        var pinnedCommit = reviewCommit;
        var fingerprint = reviewFingerprint;
        using var current = new CancellationTokenSource();
        operation = current;
        reviewNotice = "";
        Notify();
        try
        {
            var prepared = await operations.PrepareAsync(sourceRoot, current.Token);
            if (disposed || current.IsCancellationRequested) return;
            if (prepared.CollectionFingerprint != fingerprint)
            {
                SetReviewNotice("Your files changed while the upload was being prepared. Refresh and review again.");
                return;
            }
            var result = await operations.UploadAsync(new UploadRequest(prepared.OperationId, prepared.SourceRoot, prepared.CollectionFingerprint, repository, pinnedCommit, reviewBootstrapCase), current.Token);
            if (disposed) return;
            ApplyResult(result);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            if (!disposed) ApplyCancelled();
        }
        catch (RecoveryBlockedException ex)
        {
            if (!disposed) ApplyBlocked(ex.Message);
        }
        catch (ConnectionException ex)
        {
            if (!disposed) ApplyBlocked(ex.Message);
        }
        catch (Exception ex)
        {
            if (!disposed) ApplyInterrupted(ex);
        }
        finally
        {
            if (ReferenceEquals(operation, current)) operation = null;
            if (!disposed)
            {
                await RefreshStateAsync();
                Notify();
            }
        }
    }

    private void SetReviewNotice(string message)
    {
        reviewNotice = message;
        outcome = SyncOutcome.None;
        resultTitle = "";
        resultMessage = "";
        resultCommit = "";
    }

    private void ApplyResult(UploadResult result)
    {
        resultCommit = result.CandidateCommit ?? "";
        switch (result.Status)
        {
            case UploadStatus.Published:
                outcome = SyncOutcome.Uploaded;
                resultTitle = reviewBootstrapCase switch
                {
                    RepositoryBootstrapCase.UnbornRepository => "Repository initialized",
                    RepositoryBootstrapCase.EmptyTree => "First collection created",
                    RepositoryBootstrapCase.Unrelated => "Repository adopted",
                    _ => "Published to GitHub"
                };
                resultMessage = $"{(reviewBootstrapCase == RepositoryBootstrapCase.None ? "Published" : "Created")} revision {Short(resultCommit)} on {reviewRepository} · {reviewBranch}. This confirms remote publication only; it is not a verified two-device synchronization. The published revision was recorded locally as the last upload.";
                break;
            case UploadStatus.PublishedSourceChanged:
                outcome = SyncOutcome.UploadedSourceChanged;
                resultTitle = "Published, but your files changed";
                resultMessage = $"Published revision {Short(resultCommit)}, but your local files changed during the upload. The remote copy matches the reviewed snapshot and your newer edits were preserved; no local file was replaced. Refresh and review before uploading again.";
                break;
            case UploadStatus.NotPublished:
                outcome = SyncOutcome.NotPublished;
                resultTitle = "Not published";
                resultMessage = "The remote branch still points at the previously reviewed revision, so nothing was published. Retained evidence is preserved; review Upload Recovery. Do not retry blindly.";
                break;
            case UploadStatus.ConcurrentUpdate:
                outcome = SyncOutcome.ConcurrentUpdate;
                resultTitle = "Repository changed during upload";
                resultMessage = "Another change reached the repository, so the reviewed snapshot was not published. Both versions are preserved. Review Upload Recovery, then refresh.";
                break;
            default:
                ApplyInterrupted();
                return;
        }
    }

    private void ApplyCancelled()
    {
        outcome = SyncOutcome.Cancelled;
        resultTitle = "Upload cancelled";
        resultMessage = "The upload was cancelled before it could be confirmed. Any retained evidence is preserved; review Upload Recovery before uploading again. Your local files were not changed.";
    }

    private void ApplyBlocked(string message)
    {
        outcome = SyncOutcome.Blocked;
        resultTitle = "Upload blocked";
        resultMessage = message;
    }

    private void ApplyInterrupted(Exception? error = null)
    {
        outcome = SyncOutcome.Interrupted;
        resultTitle = "Upload did not finish";
        var detail = error is null ? "" : Diagnostic(error);
        resultMessage = "The upload was interrupted. Retained evidence has been preserved; review Upload Recovery before trying again. Your local files were not changed." + detail;
    }

    // Bounded, non-secret diagnostic text so a failure is never silent. GitHub errors are already sanitized.
    private static string Diagnostic(Exception error)
    {
        var text = error.Message;
        return string.IsNullOrWhiteSpace(text) ? "" : " Details: " + (text.Length > 240 ? text[..240] : text);
    }

    /// <summary>Read-only assessment of retained publication evidence. Never re-uploads or writes a baseline.</summary>
    public async Task AssessRecoveryAsync()
    {
        if (operations is null || disposed || IsBusy) return;
        if (gitHub.SelectedRepository is not { } repository)
        {
            recoveryNotice = "Choose the repository this upload targeted, then re-check.";
            Notify();
            return;
        }
        if (string.IsNullOrEmpty(RecoveryOperationId))
        {
            recoveryNotice = "No retained upload evidence was found.";
            Notify();
            return;
        }
        using var current = new CancellationTokenSource();
        operation = current;
        recoveryNotice = "Checking the retained upload evidence…";
        Notify();
        try
        {
            var result = await operations.AssessUploadAsync(RecoveryOperationId, repository, current.Token);
            if (disposed) return;
            RecoveryStatus = result.Status;
            recoveryMessage = result.Status switch
            {
                UploadStatus.Published => $"The reviewed snapshot is published at revision {Short(result.CandidateCommit)}. This is remote publication evidence only, not synchronization success.",
                UploadStatus.PublishedSourceChanged => $"Revision {Short(result.CandidateCommit)} is published, but the local source changed or cannot be verified. Your edits are preserved.",
                UploadStatus.NotPublished => "The remote branch still points at the previously reviewed revision, so nothing was published. This is an observation; it is not proof that publication never happened before a history rewrite.",
                UploadStatus.ConcurrentUpdate => "The repository changed, so the reviewed snapshot is not the current remote revision. Both versions are preserved.",
                UploadStatus.Interrupted => "The upload stopped before publication could be attempted. Retained evidence is preserved.",
                _ => "Retained upload evidence reviewed."
            };
            recoveryNotice = "";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            if (!disposed) recoveryNotice = "Recovery check cancelled.";
        }
        catch (RecoveryBlockedException ex)
        {
            if (!disposed) { RecoveryStatus = null; recoveryMessage = ""; recoveryNotice = ex.Message; }
        }
        catch (ConnectionException ex)
        {
            if (!disposed) recoveryNotice = ex.Message;
        }
        catch (Exception)
        {
            if (!disposed) recoveryNotice = "Could not check the retained upload evidence. Check connectivity and GitHub access, then re-check.";
        }
        finally
        {
            if (ReferenceEquals(operation, current)) operation = null;
            if (!disposed) Notify();
        }
    }

    public void Cancel() => operation?.Cancel();
    private void ChildChanged(object? sender, PropertyChangedEventArgs e) => Notify();
    private static string Short(string? commit) => string.IsNullOrEmpty(commit) ? "" : commit[..Math.Min(7, commit.Length)];
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
