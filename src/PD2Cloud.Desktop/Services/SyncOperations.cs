using PD2Cloud.Core;
using PD2Cloud.GitHub;
using PD2Cloud.Local;
using PD2Cloud.Remote;

namespace PD2Cloud.Desktop.Services;

/// <summary>Wires the desktop sync seam to the concrete coordinated upload/download APIs.</summary>
public sealed class SyncOperations : IUploadOperations
{
    private readonly OperationPreparationStore preparations;
    private readonly LocalSyncStateStore syncState;
    private readonly CollectionUploader uploader;
    private readonly CollectionPuller puller;

    public SyncOperations(GitHubConnection connection, string? applicationDirectory = null)
    {
        // Preparation, backups and the sync-state store share one application directory.
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
    public Task<(LocalSyncState? State, string? Notice)> LoadSyncStateAsync(CancellationToken ct) =>
        syncState.LoadAsync();
    public Task SaveSyncStateAsync(LocalSyncState state, CancellationToken ct) =>
        syncState.SaveSyncStateAsync(state);
}
