using PD2Cloud.Core;
using PD2Cloud.GitHub;
using PD2Cloud.Local;
using PD2Cloud.Remote;

namespace PD2Cloud.Desktop.Services;

/// <summary>Wires the desktop review seam to the concrete coordinated publication/recovery APIs.</summary>
public sealed class SyncOperations : IUploadOperations
{
    private readonly OperationPreparationStore preparations;
    private readonly LocalSyncStateStore syncState;
    private readonly CollectionUploader uploader;
    private readonly CollectionPuller puller;

    public SyncOperations(GitHubConnection connection, string? applicationDirectory = null)
    {
        // Preparation and the sync-state store must share one application directory; the uploader rejects
        // an upload whenever a successful-sync baseline already exists in that directory.
        var directory = Path.GetFullPath(applicationDirectory ?? ApplicationDataPaths.DirectoryPath);
        preparations = new OperationPreparationStore(directory);
        syncState = new LocalSyncStateStore(directory);
        uploader = new CollectionUploader(preparations, connection, syncState);
        puller = new CollectionPuller(preparations, connection, syncState);
    }

    public Task<PreparedOperation> PrepareAsync(string sourceRoot, CancellationToken ct) =>
        preparations.PrepareAsync(sourceRoot, ct);
    public Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken ct) =>
        uploader.UploadAsync(request, ct);
    public Task<PullResult> PullAsync(PullRequest request, CancellationToken ct) =>
        puller.PullAsync(request, ct);
    public Task<UploadResult> AssessUploadAsync(string operationId, RepositoryChoice repository, CancellationToken ct) =>
        uploader.AssessRecoveryAsync(operationId, repository, ct);
    public Task<IReadOnlyList<RecoveryAssessment>> ListRecoveryAsync(CancellationToken ct) =>
        preparations.AssessRecoveryAsync(ct);
    public Task<(LocalSyncState? State, string? Notice)> LoadSyncStateAsync(CancellationToken ct) =>
        syncState.LoadAsync();
    public Task<(bool IsAncestor, string? Notice)> VerifyBaselineAncestryAsync(LocalSyncState state, CancellationToken ct) =>
        uploader.VerifyBaselineAncestryAsync(state, ct);
}
