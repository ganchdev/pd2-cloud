namespace PD2Cloud.Core;

public enum RemoteVerification { Unavailable, Unverified, Verified }

/// <summary>Verified means a complete inventory from one pinned revision, never an offline cache or missing branch.</summary>
public sealed record RemoteCollection(string RepositoryIdentity, string Branch, string? Commit,
    CollectionManifest? Manifest, RemoteVerification Verification);

public enum SyncAction { InSync, Upload, Download, Conflict }

/// <summary>Whole-collection three-way planning against the files recorded at the last successful sync.</summary>
public static class SyncPlanner
{
    public static SyncAction Plan(IReadOnlyList<FileEntry> local, IReadOnlyList<FileEntry> remote, IReadOnlyList<FileEntry>? lastSynced)
    {
        if (SameFiles(local, remote)) return SyncAction.InSync;
        if (lastSynced is null)
            return local.Count == 0 ? SyncAction.Download : remote.Count == 0 ? SyncAction.Upload : SyncAction.Conflict;
        var localChanged = !SameFiles(local, lastSynced);
        var remoteChanged = !SameFiles(remote, lastSynced);
        if (localChanged && !remoteChanged) return SyncAction.Upload;
        if (remoteChanged && !localChanged) return SyncAction.Download;
        return SyncAction.Conflict;
    }

    public static bool SameFiles(IReadOnlyList<FileEntry> left, IReadOnlyList<FileEntry> right) =>
        left.OrderBy(f => f.RelativePath, StringComparer.Ordinal).SequenceEqual(right.OrderBy(f => f.RelativePath, StringComparer.Ordinal));
}
