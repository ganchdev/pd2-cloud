using PD2Cloud.Core;

namespace PD2Cloud.Local;

/// <summary>Reads direct-child metadata only; never opens file contents or follows links.</summary>
public sealed class FolderInspector : IFolderInspector
{
    public Task<FolderInspection> InspectAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => Inspect(path, cancellationToken), cancellationToken);

    private static FolderInspection Inspect(string path, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            // Reject links in the root and its ancestors, including Windows junctions.
            for (var parent = directory; parent is not null; parent = parent.Parent)
            {
                token.ThrowIfCancellationRequested();
                if (IsLink(parent))
                    return new([], "Unsafe folder path: a folder or ancestor is a link. Select its physical location instead.");
            }

            var entries = new List<FolderEntry>();
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (IsLink(item))
                        entries.Add(new(item.Name, null, FolderEntryKind.UnsafeLink));
                    else if (item is FileInfo file)
                        entries.Add(new(item.Name, file.Length, FolderEntryKind.File));
                    else
                        entries.Add(new(item.Name, null, FolderEntryKind.Directory));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    entries.Add(new(item.Name, null, FolderEntryKind.Unavailable));
                }
            }

            token.ThrowIfCancellationRequested();
            return new(entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray());
        }
        catch (UnauthorizedAccessException)
        {
            return new([], "Cannot access this folder. Check permissions or select another folder, then Refresh.");
        }
        catch (DirectoryNotFoundException)
        {
            return new([], "Folder no longer exists. Select another folder or restore it, then Refresh.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return new([], "Cannot inspect this folder. Check that it is an available local directory, then Refresh.");
        }
    }

    private static bool IsLink(FileSystemInfo item)
    {
        if (item.LinkTarget is not null) return true;
        var attributes = item.Attributes;
        // Missing entries can report -1; that is not a reparse point.
        return attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0;
    }
}
