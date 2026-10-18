using System.Security.Cryptography;
using PD2Cloud.Core;
using PD2Cloud.GitHub;
using PD2Cloud.Local;

namespace PD2Cloud.Remote;

/// <summary>Explicit reviewed replacement of the local collection with one verified pinned cloud revision.</summary>
public sealed record PullRequest(string SourceRoot, RepositoryChoice Repository, string PinnedCommit,
    string CollectionFingerprint);

public sealed record PullResult(string Commit, string BackupDirectory, int FileCount, string CollectionFingerprint,
    bool BaselineRecorded);

/// <summary>
/// Verifies the pinned managed collection again, writes a complete retained backup of the current local
/// collection, then installs the verified cloud bytes. The backup is never deleted by this class.
/// </summary>
public sealed class CollectionPuller(OperationPreparationStore preparations, GitHubConnection connection,
    LocalSyncStateStore syncStateStore)
{
    public async Task<PullResult> PullAsync(PullRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.PinnedCommit) || request.PinnedCommit.Length != 40
            || request.PinnedCommit.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new RecoveryBlockedException("The reviewed cloud revision is invalid. Refresh and review again.");
        if (string.IsNullOrWhiteSpace(request.CollectionFingerprint) || request.CollectionFingerprint.Length != 64)
            throw new RecoveryBlockedException("The reviewed cloud collection fingerprint is invalid. Refresh and review again.");
        if (!Path.IsPathFullyQualified(request.SourceRoot))
            throw new RecoveryBlockedException("Select an absolute local folder before pulling.");
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SourceRoot));

        // Re-read and re-verify the exact reviewed revision; never install a cached inventory or stale bytes.
        var snapshot = await connection.ReadRepositoryAsync(request.Repository, ct).ConfigureAwait(false);
        var verified = CloudCollectionReader.Verify(snapshot, ct);
        Require(verified.Repository == request.Repository && verified.Commit == request.PinnedCommit
            && verified.Content == RepositoryContent.ManagedCollection && verified.Collection?.Manifest is not null,
            "The cloud revision changed. Refresh and review the resolution again; no local file was replaced.");
        var manifest = verified.Collection!.Manifest!;
        Require(manifest.CollectionFingerprint == request.CollectionFingerprint,
            "The cloud collection changed. Refresh and review the resolution again; no local file was replaced.");
        var payloads = Payloads(snapshot, manifest.Files);

        return await preparations.CoordinateReplacementAsync(sourceRoot, async (backup, token) =>
        {
            try
            {
                await ApplyAsync(sourceRoot, manifest.Files, path => Task.FromResult(payloads[path]), token).ConfigureAwait(false);
                var installed = await preparations.InspectSourceAsync(sourceRoot, token).ConfigureAwait(false);
                Require(CollectionInventory.IsValidManifest(installed) && Equal(installed.Files, manifest.Files)
                    && installed.CollectionFingerprint == manifest.CollectionFingerprint,
                    "The installed local collection could not be verified.");
            }
            catch (Exception error)
            {
                var restored = await TryRestoreAsync(sourceRoot, backup, CancellationToken.None).ConfigureAwait(false);
                var suffix = restored
                    ? $" Your previous local files were restored from {backup.BackupDirectory}."
                    : $" Automatic restore could not complete. Your complete backup is at {backup.BackupDirectory}.";
                if (error is OperationCanceledException) throw;
                throw new RecoveryBlockedException("Pulling the cloud collection did not finish." + suffix);
            }

            var recorded = true;
            try
            {
                var state = new LocalSyncState(1, request.Repository.FullName, request.Repository.DefaultBranch,
                    request.PinnedCommit, sourceRoot, manifest.Files, DateTimeOffset.UtcNow,
                    manifest.CollectionFingerprint!);
                await syncStateStore.SaveSyncStateAsync(state).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The bytes are correctly installed and backed up; only the baseline note failed.
                recorded = false;
            }
            return new PullResult(request.PinnedCommit, backup.BackupDirectory, manifest.Files.Count,
                manifest.CollectionFingerprint!, recorded);
        }, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, byte[]> Payloads(RepositorySnapshot snapshot, IReadOnlyList<FileEntry> files)
    {
        var entries = snapshot.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var path = CloudStorage.FilesPath + "/" + file.RelativePath;
            if (!entries.TryGetValue(path, out var entry) || entry.Type != "blob"
                || !snapshot.Blobs.TryGetValue(entry.Sha, out var bytes))
                throw new RecoveryBlockedException("The verified cloud payload changed. Refresh and review the resolution again.");
            Require(bytes.LongLength == file.SizeBytes
                && Convert.ToHexStringLower(SHA256.HashData(bytes)) == file.Sha256,
                "The verified cloud payload changed. Refresh and review the resolution again.");
            result.Add(file.RelativePath, bytes);
        }
        return result;
    }

    private static async Task ApplyAsync(string sourceRoot, IReadOnlyList<FileEntry> files,
        Func<string, Task<byte[]>> read, CancellationToken ct)
    {
        var expected = files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await read(file.RelativePath).ConfigureAwait(false);
            Require(bytes.LongLength == file.SizeBytes
                && Convert.ToHexStringLower(SHA256.HashData(bytes)) == file.Sha256,
                "A replacement payload failed verification.");
            await WriteAsync(sourceRoot, file.RelativePath, bytes, ct).ConfigureAwait(false);
        }
        // Remove the old collection's files only after every replacement byte is in place.
        var issues = new List<ValidationIssue>();
        var current = CollectionScanner.Inventory(sourceRoot, ct, issues);
        Require(issues.Count == 0, "The local folder could not be enumerated safely.");
        foreach (var entry in current.Where(e => !e.IsDirectory && !expected.Contains(e.RelativePath)))
        {
            ct.ThrowIfCancellationRequested();
            var path = PhysicalPath(sourceRoot, entry.RelativePath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task<bool> TryRestoreAsync(string sourceRoot, ReplacementBackup backup, CancellationToken ct)
    {
        try
        {
            foreach (var file in backup.Files)
            {
                ct.ThrowIfCancellationRequested();
                var source = Path.Combine(backup.BackupDirectory, file.RelativePath);
                var temporary = PhysicalPath(sourceRoot, file.RelativePath) + ".pd2cloud-restore-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                File.Copy(source, temporary, overwrite: false);
                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    stream.Flush(flushToDisk: true);
                File.Move(temporary, PhysicalPath(sourceRoot, file.RelativePath), overwrite: true);
            }
            var expected = backup.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
            var issues = new List<ValidationIssue>();
            var current = CollectionScanner.Inventory(sourceRoot, ct, issues);
            if (issues.Count != 0) return false;
            foreach (var entry in current.Where(e => !e.IsDirectory && !expected.Contains(e.RelativePath)))
            {
                ct.ThrowIfCancellationRequested();
                var path = PhysicalPath(sourceRoot, entry.RelativePath);
                if (File.Exists(path)) File.Delete(path);
            }
            var restored = await preparations.InspectSourceAsync(sourceRoot, ct).ConfigureAwait(false);
            return CollectionInventory.IsValidManifest(restored) && Equal(restored.Files, backup.Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task WriteAsync(string sourceRoot, string relativePath, byte[] bytes, CancellationToken ct)
    {
        var target = PhysicalPath(sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".pd2cloud-resolve-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        ct.ThrowIfCancellationRequested();
        File.Move(temporary, target, overwrite: true);
    }

    private static string PhysicalPath(string sourceRoot, string relativePath)
    {
        Require(RelativePaths.IsSafe(relativePath) && relativePath == RelativePaths.Canonical(relativePath),
            "A replacement path is unsafe.");
        var path = Path.GetFullPath(Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = sourceRoot.EndsWith(Path.DirectorySeparatorChar) ? sourceRoot : sourceRoot + Path.DirectorySeparatorChar;
        Require(path.StartsWith(prefix, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase),
            "A replacement path escaped the local folder.");
        return path;
    }

    private static bool Equal(IReadOnlyList<FileEntry> a, IReadOnlyList<FileEntry> b) =>
        a.OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .SequenceEqual(b.OrderBy(f => f.RelativePath, StringComparer.Ordinal));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new RecoveryBlockedException(message);
    }
}
