namespace PD2Cloud.Core;

public enum ValidationCode { UnsafePath, NameCollision, UnsupportedEntry, AccessError, FolderError, Unstable, ReadError }
public sealed record ValidationIssue(ValidationCode Code, string Message, string? RelativePath = null);
public sealed record FileEntry(string RelativePath, long SizeBytes, string Sha256);
public sealed record CollectionManifest(int SchemaVersion, string RootPath, IReadOnlyList<FileEntry> Files,
    string? CollectionFingerprint, DateTimeOffset InspectedAt, bool IsStable, IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => IsStable && Issues.Count == 0 && CollectionFingerprint is not null;
}

public interface ICollectionScanner
{
    Task<CollectionManifest> ScanAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Future successful-sync record. Inspection alone must never create this record.</summary>
public sealed record LocalSyncState(int SchemaVersion, string RepositoryIdentity, string Branch,
    string LastSynchronizedCommit, string LocalRootPath, IReadOnlyList<FileEntry> Files, DateTimeOffset SynchronizedAt);
