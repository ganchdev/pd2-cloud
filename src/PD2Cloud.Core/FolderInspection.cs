namespace PD2Cloud.Core;

public enum FolderEntryKind { File, Directory, UnsafeLink, Unavailable }

public sealed record FolderEntry(string Name, long? SizeBytes, FolderEntryKind Kind)
{
    public string SizeDisplay => SizeBytes is long size ? $"{size:N0} bytes" : "—";
    public string Description => Kind switch
    {
        FolderEntryKind.File => "File",
        FolderEntryKind.Directory => "Folder (not inspected)",
        FolderEntryKind.UnsafeLink => "Unsafe link — skipped",
        _ => "Unavailable — refresh to retry"
    };
}

public sealed record FolderInspection(IReadOnlyList<FolderEntry> Entries, string? Error = null);

public interface IFolderInspector
{
    Task<FolderInspection> InspectAsync(string path, CancellationToken cancellationToken);
}
