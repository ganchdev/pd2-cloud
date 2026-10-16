using System.ComponentModel;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;
using PD2Cloud.GitHub;
using PD2Cloud.Local;
using PD2Cloud.Remote;

namespace PD2Cloud.Desktop.ViewModels;

public enum SyncOutcome { None, Uploaded, UploadedSourceChanged, NotPublished, ConcurrentUpdate, Interrupted, Blocked, Cancelled }

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

    public string StatusLine
    {
        get
        {
            if (operations is null) return "";
            if (HasBaseline)
                return BaselineAncestryValid
                    ? $"Last upload published revision {Short(Baseline!.LastSynchronizedCommit)} on {Baseline.SynchronizedAt.ToLocalTime():g}. This records remote publication, not verified synchronization."
                    : "The last uploaded revision is no longer part of the remote history. Review the retained upload evidence.";
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
