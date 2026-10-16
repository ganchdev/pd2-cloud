using PD2Cloud.Core;

namespace PD2Cloud.Local;

/// <summary>Recursive read-only inspection with two complete hash passes and three inventory checks.</summary>
public sealed class CollectionScanner(IFileFingerprintReader? reader = null, string? applicationDirectory = null) : ICollectionScanner
{
    private readonly IFileFingerprintReader reader = reader ?? new FileFingerprintReader();

    public Task<CollectionManifest> ScanAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(path, cancellationToken), cancellationToken);

    // Only application-owned snapshot verification may bypass the source/storage overlap rule.
    internal CollectionManifest ScanOwned(string path, CancellationToken token) => Scan(path, token, owned: true);

    private CollectionManifest Scan(string path, CancellationToken token, bool owned = false)
    {
        var issues = new List<ValidationIssue>();
        var root = path;
        CollectionManifest Result(IReadOnlyList<FileEntry> files, bool stable) =>
            new(1, root, files, stable && issues.Count == 0 ? Fingerprint(files) : null,
                DateTimeOffset.UtcNow, stable, issues.ToArray());
        try
        {
            token.ThrowIfCancellationRequested();
            root = Path.GetFullPath(path);
            if (!owned && !ApplicationDataPaths.IsSourceAllowed(root, applicationDirectory))
            {
                issues.Add(new(ValidationCode.UnsafePath, "Select a folder outside application-owned state and staging, and not a parent of that location."));
                return Result([], false);
            }
            var before = Inventory(root, token, issues);
            // Do not present a partial collection as complete when enumeration or validation fails.
            if (issues.Count > 0) return Result([], false);
            var first = ReadAll(before, token);
            CheckInventory(root, before, token);
            var second = ReadAll(before, token);
            CheckInventory(root, before, token);
            if (!first.SequenceEqual(second)) throw new UnstableCollectionException();
            return Result(first, true);
        }
        catch (UnsafeFolderException ex)
        {
            issues.Add(new(ValidationCode.UnsafePath, "Folder or ancestor is a link; select its physical location instead.", ex.Message));
        }
        catch (UnstableCollectionException)
        {
            issues.Add(new(ValidationCode.Unstable, "Files or folders changed during inspection. Refresh to retry."));
        }
        catch (SourceReadException ex)
        {
            issues.Add(new(ValidationCode.ReadError, "Cannot read this regular file safely; it may be unavailable, unsupported or changing. Check access and Refresh.", ex.RelativePath));
        }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new(ValidationCode.AccessError, "Cannot access the selected folder. Check permissions and Refresh."));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            issues.Add(new(ValidationCode.FolderError, "Cannot inspect the selected folder. Check its location and access, then Refresh."));
        }
        return Result([], false);
    }

    private FileEntry[] ReadAll(Metadata[] inventory, CancellationToken token)
    {
        return inventory.Where(e => !e.IsDirectory).Select(entry =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ValidateAncestors(new FileInfo(entry.FullPath).Directory!, token);
                var hash = reader.Read(entry.FullPath, token);
                if (hash.SizeBytes != entry.SizeBytes) throw new UnstableCollectionException();
                return new FileEntry(entry.RelativePath, hash.SizeBytes, hash.Sha256);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SourceReadException(entry.RelativePath, ex);
            }
        }).ToArray();
    }

    internal sealed record Metadata(string RelativePath, string FullPath, bool IsDirectory, long SizeBytes, DateTime WriteTime);

    internal static Metadata[] Inventory(string root, CancellationToken token, List<ValidationIssue> issues)
    {
        var directory = new DirectoryInfo(root);
        ValidateAncestors(directory, token);
        var entries = new List<Metadata>();
        // Use an explicit stack so deeply nested trees do not exhaust the call stack.
        var pending = new Stack<(DirectoryInfo Directory, string Prefix)>();
        pending.Push((directory, ""));
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ValidateAncestors(current.Directory, token);
                foreach (var item in current.Directory.EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    var relative = current.Prefix + item.Name;
                    try
                    {
                        if (!RelativePaths.IsSafe(relative))
                        {
                            issues.Add(new(ValidationCode.UnsafePath, "Unsafe or nonportable path; excluded from inspection.", relative));
                            continue;
                        }
                        relative = RelativePaths.Canonical(relative);
                        if (IsLink(item) || (item.Attributes & FileAttributes.Device) != 0)
                        {
                            issues.Add(new(ValidationCode.UnsupportedEntry, "Symbolic link, reparse point or unsupported entry; excluded and not followed.", relative));
                            continue;
                        }
                        var isDirectory = item is DirectoryInfo;
                        // Directory timestamps are refreshed lazily by some filesystems (e.g. NTFS) and cannot be
                        // compared reliably across passes; membership changes are already caught by the entry set.
                        entries.Add(new(relative, item.FullName, isDirectory, item is FileInfo file ? file.Length : 0,
                            item is FileInfo fileInfo ? fileInfo.LastWriteTimeUtc : default));
                        if (item is DirectoryInfo child) pending.Push((child, relative + "/"));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        issues.Add(new(ValidationCode.AccessError, "Cannot inspect this entry; check access and Refresh.", relative));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new(ValidationCode.AccessError, "Cannot enumerate this directory; check access and Refresh.", current.Prefix));
            }
        }
        foreach (var group in entries.GroupBy(e => RelativePaths.CollisionKey(e.RelativePath)).Where(g => g.Count() > 1))
            issues.Add(new(ValidationCode.NameCollision, "Case-insensitive or Unicode-normalization path collision; rename the conflicting entries.", group.Key));
        return entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToArray();
    }

    internal static void CheckInventory(string root, Metadata[] before, CancellationToken token)
    {
        var issues = new List<ValidationIssue>();
        if (!before.SequenceEqual(Inventory(root, token, issues)) || issues.Count > 0) throw new UnstableCollectionException();
    }

    private static bool IsLink(FileSystemInfo item)
    {
        if (item.LinkTarget is not null) return true;
        var attributes = item.Attributes;
        return attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static void ValidateAncestors(DirectoryInfo directory, CancellationToken token)
    {
        for (var parent = directory; parent is not null; parent = parent.Parent)
        {
            token.ThrowIfCancellationRequested();
            if (IsLink(parent)) throw new UnsafeFolderException(parent.FullName);
        }
    }

    // Retained for callers of the original inspection API. Encoding is shared with pure comparison.
    public static string Fingerprint(IReadOnlyList<FileEntry> files) => CollectionInventory.Fingerprint(files);

    private sealed class UnsafeFolderException(string path) : Exception(path);
    internal sealed class UnstableCollectionException : Exception;
    private sealed class SourceReadException(string path, Exception inner) : IOException(path, inner)
    {
        public string RelativePath { get; } = path;
    }
}
