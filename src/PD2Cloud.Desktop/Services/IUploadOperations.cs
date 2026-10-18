using PD2Cloud.Core;
using PD2Cloud.GitHub;
using PD2Cloud.Local;
using PD2Cloud.Remote;

namespace PD2Cloud.Desktop.Services;

/// <summary>
/// Sync seam over the coordinated Remote/Local APIs; the desktop never writes source files, remote objects or
/// evidence itself.
/// </summary>
public interface IUploadOperations
{
    Task<PreparedOperation> PrepareAsync(string sourceRoot, CancellationToken ct);
    Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken ct);
    Task<PullResult> PullAsync(PullRequest request, CancellationToken ct);
    Task<(LocalSyncState? State, string? Notice)> LoadSyncStateAsync(CancellationToken ct);
    Task SaveSyncStateAsync(LocalSyncState state, CancellationToken ct);
}
