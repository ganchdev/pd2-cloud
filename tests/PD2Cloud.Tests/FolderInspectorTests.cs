using PD2Cloud.Core;
using PD2Cloud.Local;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class FolderInspectorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd2-inspection-" + Guid.NewGuid());
    private readonly FolderInspector inspector = new();

    public FolderInspectorTests()
    {
        Directory.CreateDirectory(root);
        // macOS aliases /var and /tmp; use a physical test location.
        if (!OperatingSystem.IsWindows())
        {
            var temp = new DirectoryInfo(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            for (var parent = temp; parent is not null; parent = parent.Parent)
            {
                if (parent.LinkTarget is not null)
                {
                    var physical = parent.ResolveLinkTarget(true)!.FullName;
                    var old = root;
                    root = Path.Combine(physical, Path.GetRelativePath(parent.FullName, root));
                    Assert.True(Directory.Exists(old));
                    break;
                }
            }
        }
    }

    [Fact]
    public async Task ListsAllDirectChildrenAndPreservesBytesAndWriteTimes()
    {
        var bytes = new byte[] { 0, 255, 3, 4 };
        var file = Path.Combine(root, "z.d2s");
        await File.WriteAllBytesAsync(file, bytes);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "unrecognized");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "nested", "hidden.d2s"), "nested");
        var before = File.GetLastWriteTimeUtc(file);
        var result = await inspector.InspectAsync(root, default);
        Assert.Null(result.Error);
        Assert.Equal(["a.txt", "nested", "z.d2s"], result.Entries.Select(e => e.Name));
        Assert.Equal(4L, result.Entries.Last().SizeBytes);
        Assert.Equal(FolderEntryKind.Directory, result.Entries[1].Kind);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
        Assert.Equal(3, Directory.GetFileSystemEntries(root).Length);
    }

    [Fact]
    public async Task EmptyThenRefreshReflectsExternalChanges()
    {
        Assert.Empty((await inspector.InspectAsync(root, default)).Entries);
        await File.WriteAllTextAsync(Path.Combine(root, "new"), "abc");
        Assert.Equal(3L, Assert.Single((await inspector.InspectAsync(root, default)).Entries).SizeBytes);
        File.Delete(Path.Combine(root, "new"));
        Assert.Empty((await inspector.InspectAsync(root, default)).Entries);
    }

    [Fact]
    public async Task MissingAndFilePathsHaveClearErrors()
    {
        Assert.Contains("no longer exists", (await inspector.InspectAsync(Path.Combine(root, "missing"), default)).Error);
        var file = Path.Combine(root, "file");
        await File.WriteAllTextAsync(file, "x");
        Assert.NotNull((await inspector.InspectAsync(file, default)).Error);
    }

    [Fact]
    public async Task CancellationIsHonored()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspector.InspectAsync(root, cancellation.Token));
    }

    [UnixFact]
    public async Task LinksAreFlaggedAndLinkedRootsAndAncestorsRejected()
    {
        Directory.CreateDirectory(Path.Combine(root, "real"));
        File.CreateSymbolicLink(Path.Combine(root, "dangling"), Path.Combine(root, "absent"));
        File.CreateSymbolicLink(Path.Combine(root, "file-link"), Path.Combine(root, "outside"));
        Directory.CreateSymbolicLink(Path.Combine(root, "folder-link"), Path.Combine(root, "real"));
        var result = await inspector.InspectAsync(root, default);
        Assert.Equal(3, result.Entries.Count(e => e.Kind == FolderEntryKind.UnsafeLink && e.SizeBytes is null));
        Assert.Contains("Unsafe", (await inspector.InspectAsync(Path.Combine(root, "folder-link"), default)).Error);
        Directory.CreateDirectory(Path.Combine(root, "real", "child"));
        Assert.Contains("Unsafe", (await inspector.InspectAsync(Path.Combine(root, "folder-link", "child"), default)).Error);
    }

    [UnixFact]
    public async Task InaccessibleDirectoryProducesAnErrorAndCanRecover()
    {
        if (OperatingSystem.IsWindows()) return; // UnixFact skips this fixture on Windows.
        var permissions = File.GetUnixFileMode(root);
        try
        {
            File.SetUnixFileMode(root, UnixFileMode.None);
            var result = await inspector.InspectAsync(root, default);
            Assert.Contains("access", result.Error);
            Assert.Empty(result.Entries);
        }
        finally
        {
            File.SetUnixFileMode(root, permissions);
        }
        Assert.Null((await inspector.InspectAsync(root, default)).Error);
    }

    public void Dispose() => Directory.Delete(root, true);
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix permission/link fixture; native Windows ACL/junction verification remains required.";
    }
}
