using System.Diagnostics;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Logging;
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
        AppLog.Info("Storage", $"Sync operations will use application data at {directory}");
        preparations = new OperationPreparationStore(directory);
        syncState = new LocalSyncStateStore(directory);
        uploader = new CollectionUploader(preparations, connection, syncState);
        puller = new CollectionPuller(preparations, connection, syncState);
    }

    public async Task<PreparedOperation> PrepareAsync(string sourceRoot, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await preparations.PrepareAsync(sourceRoot, ct).ConfigureAwait(false);
            AppLog.Info("Storage", $"Preparation finished in {watch.ElapsedMilliseconds} ms; operation={result.OperationId}; files={result.Files.Count}; fingerprint={result.CollectionFingerprint}");
            return result;
        }
        catch (Exception error)
        {
            AppLog.Warn("Storage", $"Preparation failed after {watch.ElapsedMilliseconds} ms; source={sourceRoot}", error);
            throw;
        }
    }

    public async Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        AppLog.Info("Upload", $"Started; repository={request.Repository.FullName}; bootstrap={request.Bootstrap}; pinned={request.PinnedCommit}; fingerprint={request.CollectionFingerprint}");
        try
        {
            var result = await uploader.UploadAsync(request, ct).ConfigureAwait(false);
            AppLog.Info("Upload", $"Finished in {watch.ElapsedMilliseconds} ms; status={result.Status}; candidate={result.CandidateCommit ?? "<none>"}");
            return result;
        }
        catch (Exception error)
        {
            AppLog.Warn("Upload", $"Failed after {watch.ElapsedMilliseconds} ms; repository={request.Repository.FullName}; fingerprint={request.CollectionFingerprint}", error);
            throw;
        }
    }

    public async Task<PullResult> PullAsync(PullRequest request, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        AppLog.Info("Download", $"Started; repository={request.Repository.FullName}; commit={request.PinnedCommit}; fingerprint={request.CollectionFingerprint}");
        try
        {
            var result = await puller.PullAsync(request, ct).ConfigureAwait(false);
            AppLog.Info("Download", $"Finished in {watch.ElapsedMilliseconds} ms; files={result.FileCount}; backup={result.BackupDirectory}; baselineRecorded={result.BaselineRecorded}");
            return result;
        }
        catch (Exception error)
        {
            AppLog.Warn("Download", $"Failed after {watch.ElapsedMilliseconds} ms; repository={request.Repository.FullName}; commit={request.PinnedCommit}", error);
            throw;
        }
    }

    public async Task<(LocalSyncState? State, string? Notice)> LoadSyncStateAsync(CancellationToken ct)
    {
        try
        {
            var result = await syncState.LoadAsync().ConfigureAwait(false);
            AppLog.Info("Storage", $"Sync state loaded; baseline={result.State?.LastSynchronizedCommit ?? "<none>"}; notice={result.Notice}");
            return result;
        }
        catch (Exception error)
        {
            AppLog.Warn("Storage", "Sync state could not be loaded.", error);
            throw;
        }
    }

    public async Task SaveSyncStateAsync(LocalSyncState state, CancellationToken ct)
    {
        try
        {
            await syncState.SaveSyncStateAsync(state).ConfigureAwait(false);
            AppLog.Info("Storage", $"Sync state saved; repository={state.RepositoryIdentity}; commit={state.LastSynchronizedCommit}; files={state.Files.Count}");
        }
        catch (Exception error)
        {
            AppLog.Warn("Storage", $"Sync state could not be saved; repository={state.RepositoryIdentity}; commit={state.LastSynchronizedCommit}", error);
            throw;
        }
    }
}
