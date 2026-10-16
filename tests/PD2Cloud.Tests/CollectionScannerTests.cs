using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PD2Cloud.Core;
using PD2Cloud.Local;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class CollectionScannerTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pd2-collection-" + Guid.NewGuid());
    public CollectionScannerTests() => Directory.CreateDirectory(root);
    private Task<CollectionManifest> Scan() => new CollectionScanner().ScanAsync(root, default);
    private string Put(string relative, byte[]? bytes = null)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? [0, 255, 1, 3]);
        return path;
    }

    private sealed class MutatingReader(Action<string, int> action) : IFileFingerprintReader
    {
        private int reads;
        public FileFingerprint Read(string path, CancellationToken token)
        {
            var result = new FileFingerprintReader().Read(path, token);
            action(path, ++reads);
            return result;
        }
    }

    [Fact]
    public async Task IncludesEveryRegularFileRecursivelyAndPreservesSourceBytesAndTimes()
    {
        foreach (var name in new[] { "z.unknown", "a.txt", ".DS_Store", "backup.bak", "nested/config.ini", "nested/deeper/extensionless", "nested/.tmp" }) Put(name);
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (File.ReadAllBytes(p), File.GetLastWriteTimeUtc(p)));
        var result = await Scan();
        Assert.True(result.IsValid);
        Assert.Equal(before.Count, result.Files.Count);
        Assert.Equal(result.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal), result.Files);
        Assert.Contains(result.Files, f => f.RelativePath == "nested/deeper/extensionless");
        foreach (var entry in result.Files)
        {
            Assert.Equal(4L, entry.SizeBytes);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 0, 255, 1, 3 })), entry.Sha256);
        }
        foreach (var (file, original) in before)
        {
            Assert.Equal(original.Item1, await File.ReadAllBytesAsync(file));
            Assert.Equal(original.Item2, File.GetLastWriteTimeUtc(file));
        }
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        Assert.Equal(3, Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task FingerprintIgnoresRootTimestampMetadataAndCreationOrderButIncludesPathsAndBytes()
    {
        Put("first/b", [8]); Put("a", [1]);
        var first = await Scan();
        var otherRoot = Path.Combine(root, "other");
        Directory.CreateDirectory(otherRoot);
        File.WriteAllBytes(Path.Combine(otherRoot, "a"), [1]);
        Directory.CreateDirectory(Path.Combine(otherRoot, "first"));
        File.WriteAllBytes(Path.Combine(otherRoot, "first", "b"), [8]);
        File.SetLastWriteTimeUtc(Path.Combine(otherRoot, "a"), DateTime.UtcNow.AddYears(-1));
        var other = await new CollectionScanner().ScanAsync(otherRoot, default);
        Assert.Equal(first.Files, other.Files);
        Assert.Equal(first.CollectionFingerprint, other.CollectionFingerprint);
        Assert.NotEqual(first.RootPath, other.RootPath);
        Put("first/b", [9]);
        var modified = await Scan();
        Assert.NotEqual(first.CollectionFingerprint, modified.CollectionFingerprint);
        File.Move(Path.Combine(root, "a"), Path.Combine(root, "renamed"));
        Assert.NotEqual(modified.CollectionFingerprint, (await Scan()).CollectionFingerprint);
    }

    [Fact]
    public async Task CollectionFingerprintMatchesPortableSchemaVector()
    {
        Put("nested/b", [8]); Put("a", [1]);
        Assert.Equal("a3636f3b98fcce43a2d37af523f51a068215e20a1833cc1ca0d61ae811067c70", (await Scan()).CollectionFingerprint);
        Put("e\u0301/😀.binary", [9]);
        var result = await Scan();
        Assert.Contains(result.Files, f => f.RelativePath == "é/😀.binary");
        Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains('\\'));
    }

    [Fact]
    public async Task EmptyAndRefreshExposeAdditionModificationAndRemoval()
    {
        var empty = await Scan();
        Assert.True(empty.IsValid);
        Assert.Empty(empty.Files);
        var path = Put("nested/file");
        var added = await Scan();
        Assert.Single(added.Files);
        Put("nested/file", [7, 8]);
        var changed = await Scan();
        Assert.NotEqual(added.Files[0].Sha256, changed.Files[0].Sha256);
        File.Delete(path);
        var removed = await Scan();
        Assert.Empty(removed.Files);
        Assert.Equal(empty.CollectionFingerprint, removed.CollectionFingerprint);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("addition")]
    [InlineData("removal")]
    [InlineData("folder")]
    public async Task DetectsChangesAcrossReadsIncludingSameSizeAndRestoredTimestamp(string mutation)
    {
        var path = Put("nested/file", Encoding.UTF8.GetBytes("original"));
        var time = File.GetLastWriteTimeUtc(path);
        var reader = new MutatingReader((file, read) =>
        {
            if (read != 1) return;
            if (mutation == "addition") Put("new/nested.bin");
            else if (mutation == "removal") File.Delete(file);
            else if (mutation == "folder") Directory.CreateDirectory(Path.Combine(root, "new"));
            else { File.WriteAllText(file, "modified"); File.SetLastWriteTimeUtc(file, time); }
        });
        var result = await new CollectionScanner(reader).ScanAsync(root, default);
        Assert.False(result.IsStable);
        Assert.False(result.IsValid);
        Assert.Null(result.CollectionFingerprint);
        Assert.Empty(result.Files);
        Assert.Contains(result.Issues, i => i.Code is ValidationCode.Unstable or ValidationCode.ReadError);
    }

    [Fact]
    public async Task MissingRootAndFileRootFailAndCancellationIsHonored()
    {
        Assert.Contains((await new CollectionScanner().ScanAsync(Path.Combine(root, "missing"), default)).Issues, i => i.Code is ValidationCode.FolderError or ValidationCode.AccessError);
        var path = Put("file");
        Assert.False((await new CollectionScanner().ScanAsync(path, default)).IsValid);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionScanner().ScanAsync(root, cancelled.Token));
        using var during = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionScanner(new MutatingReader((_, _) => during.Cancel())).ScanAsync(root, during.Token));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("a\\b")]
    [InlineData("dir/CON.txt")]
    [InlineData("file:stream")]
    [InlineData("trailing.")]
    public void RejectsUnsafePortablePaths(string path) => Assert.False(RelativePaths.IsSafe(path));

    [Fact]
    public void UnicodeCanonicalizationAndCollisionKeysArePortable()
    {
        Assert.Equal("dir/é.bin", RelativePaths.Canonical("dir/e\u0301.bin"));
        Assert.Equal(RelativePaths.CollisionKey("Dir/É.bin"), RelativePaths.CollisionKey("dir/e\u0301.BIN"));
        Assert.True(RelativePaths.IsSafe("nested/😀.data"));
    }

    [Fact]
    public async Task DetectsCaseCollisionWhenFilesystemSupportsDistinctNames()
    {
        Put("Case/file", [1]); Put("case/file", [2]);
        // Case-insensitive volumes cannot represent both names; validation remains covered by the portable key test.
        if (Directory.GetDirectories(root).Length != 2) return;
        var result = await Scan();
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == ValidationCode.NameCollision);
    }

    [Fact]
    public async Task DetectsUnicodeCollisionWhenFilesystemSupportsDistinctNames()
    {
        Put("é", [1]); Put("e\u0301", [2]);
        if (Directory.GetFiles(root).Length != 2) return;
        var result = await Scan();
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == ValidationCode.NameCollision);
    }

    [UnixFact]
    public async Task UnsafeNamesAreReportedAndNeverMadeIntoAValidCollection()
    {
        Put("bad:name");
        var result = await Scan();
        Assert.Contains(result.Issues, i => i.Code == ValidationCode.UnsafePath && i.RelativePath == "bad:name");
        Assert.False(result.IsValid);
    }

    [UnixFact]
    public async Task LinksLoopsLinkedRootsAndNonregularFilesAreRejectedWithoutReadingTargets()
    {
        Directory.CreateSymbolicLink(Path.Combine(root, "loop"), root);
        File.CreateSymbolicLink(Path.Combine(root, "dangling"), Path.Combine(root, "absent"));
        var result = await Scan().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.IsValid);
        Assert.Equal(2, result.Issues.Count(i => i.Code == ValidationCode.UnsupportedEntry));
        Assert.Empty(result.Files);
        Assert.Contains((await new CollectionScanner().ScanAsync(Path.Combine(root, "loop"), default)).Issues, i => i.Code == ValidationCode.UnsafePath);
        File.Delete(Path.Combine(root, "dangling"));
        Directory.Delete(Path.Combine(root, "loop"));
        Assert.Equal(0, MkFifo(Path.Combine(root, "pipe"), 0x180));
        result = await Scan().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == ValidationCode.ReadError);
    }

    [UnixFact]
    public async Task ReadAndNestedEnumerationPermissionErrorsAreReported()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Put("nested/file");
        var mode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
            Assert.Contains((await Scan()).Issues, i => i.Code == ValidationCode.ReadError && i.RelativePath == "nested/file");
        }
        finally { File.SetUnixFileMode(path, mode); }
        var directory = Path.GetDirectoryName(path)!;
        mode = File.GetUnixFileMode(directory);
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
            var result = await Scan();
            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, i => i.Code == ValidationCode.AccessError);
        }
        finally { File.SetUnixFileMode(directory, mode); }
    }

    [Fact]
    public async Task InspectionDoesNotCreateApplicationStateOrTemporaryFiles()
    {
        var stateExisted = File.Exists(ApplicationDataPaths.LocalSyncStatePath);
        var state = stateExisted ? File.ReadAllBytes(ApplicationDataPaths.LocalSyncStatePath) : null;
        Put("user.tmp");
        await Scan(); await Scan();
        Assert.Equal("user.tmp", Assert.Single(Directory.GetFiles(root)).Split(Path.DirectorySeparatorChar).Last());
        Assert.Equal(stateExisted, File.Exists(ApplicationDataPaths.LocalSyncStatePath));
        if (state is not null) Assert.Equal(state, File.ReadAllBytes(ApplicationDataPaths.LocalSyncStatePath));
        Assert.True(Path.IsPathFullyQualified(ApplicationDataPaths.LocalSyncStatePath));
        Assert.False(ApplicationDataPaths.LocalSyncStatePath.StartsWith(root, StringComparison.Ordinal));
    }

    [Fact]
    public void FutureStateBoundaryIgnoresObsoleteExtraFieldsAndPreservesUnrelatedValues()
    {
        var json = """{"SchemaVersion":1,"RepositoryIdentity":"repo-id","Branch":"main","LastSynchronizedCommit":"commit","LocalRootPath":"/saves","Files":[],"SynchronizedAt":"2026-10-15T10:00:00Z","CollectionFingerprint":"","Profile":{"StashSystem":"obsolete"},"GameVersion":"obsolete"}""";
        var state = JsonSerializer.Deserialize<LocalSyncState>(json)!;
        Assert.Equal("repo-id", state.RepositoryIdentity);
        Assert.Equal("commit", state.LastSynchronizedCommit);
        Assert.Equal("/saves", state.LocalRootPath);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    public void Dispose() => Directory.Delete(root, true);
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix fixture; Windows native ACL/reparse acceptance remains required.";
    }
}
