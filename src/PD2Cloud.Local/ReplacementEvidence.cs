using System.Text.Json;
using PD2Cloud.Core;

namespace PD2Cloud.Local;

public sealed partial class OperationPreparationStore
{
    private const int KeptReplacementBackups = 5;

    /// <summary>
    /// Runs a source-replacement callback after writing a complete, independently verified backup of the
    /// current collection. The same storage gate and filesystem lease used by preparation exclude interleaving.
    /// The backup is retained even when the callback fails or is cancelled; only the newest five are kept.
    /// </summary>
    internal async Task<T> CoordinateReplacementAsync<T>(string sourceRoot,
        Func<ReplacementBackup, CancellationToken, Task<T>> replace, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(sourceRoot) || !ApplicationDataPaths.IsSourceAllowed(sourceRoot, applicationDirectory))
            throw new RecoveryBlockedException("Select a physical source outside application storage.");
        sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                using var lease = AcquireLease();
                DiscardOperations();
                var original = await scanner.ScanAsync(sourceRoot, token).ConfigureAwait(false);
                RequireValid(original, "Your save folder");
                if (original.Files.Count > MaxFiles)
                    throw new RecoveryBlockedException("Replacement supports at most 1,000 files; nothing was changed.");

                var issues = new List<ValidationIssue>();
                var metadata = CollectionScanner.Inventory(sourceRoot, token, issues);
                if (issues.Count != 0) throw new RecoveryBlockedException("Source inventory is unsafe; no backup was written.");

                var id = Guid.NewGuid().ToString("N");
                var createdAt = DateTimeOffset.UtcNow;
                var backupDirectory = Path.Combine(applicationDirectory, "replacements", id, "files");
                RecoveryStorage.CreateDirectory(backupDirectory);
                foreach (var entry in metadata.Where(m => m.IsDirectory).OrderBy(m => m.RelativePath.Length))
                {
                    token.ThrowIfCancellationRequested();
                    RecoveryStorage.CreateDirectory(Path.Combine(backupDirectory, entry.RelativePath));
                }
                foreach (var file in original.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = metadata.SingleOrDefault(m => !m.IsDirectory && m.RelativePath == file.RelativePath)
                        ?? throw new RecoveryBlockedException("Source membership changed while the backup was being written.");
                    CollectionScanner.CheckInventory(sourceRoot, metadata, token);
                    var target = Path.Combine(backupDirectory, file.RelativePath);
                    var temporary = target + ".pd2cloud-backup-" + Guid.NewGuid().ToString("N");
                    FileFingerprint copied;
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        copied = new FileFingerprintReader().Copy(entry.FullPath, stream, token);
                        stream.Flush(flushToDisk: true);
                    }
                    if (copied.SizeBytes != file.SizeBytes || copied.Sha256 != file.Sha256)
                        throw new RecoveryBlockedException("Source bytes changed while the backup was being written.");
                    RecoveryStorage.Publish(temporary, target);
                    RecoveryStorage.FlushDirectory(Path.GetDirectoryName(target)!);
                }
                var backed = scanner.ScanOwned(backupDirectory, token);
                RequireValid(backed, "The backup in application data");
                if (!Equal(backed.Files, original.Files))
                    throw new RecoveryBlockedException("The complete replacement backup could not be verified.");

                var record = new ReplacementBackupRecord(1, id, sourceRoot, original.Files.ToArray(),
                    original.CollectionFingerprint!, createdAt);
                var recordBytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                if (recordBytes.Length > MaxJournalBytes)
                    throw new RecoveryBlockedException("The replacement backup record exceeds the metadata size limit.");
                RecoveryStorage.WriteNew(Path.Combine(applicationDirectory, "replacements", id, "backup.json"), recordBytes);

                var backup = new ReplacementBackup(id, sourceRoot, backupDirectory,
                    Array.AsReadOnly(original.Files.ToArray()), original.CollectionFingerprint!, createdAt);
                var result = await replace(backup, token).ConfigureAwait(false);
                PruneReplacementBackups();
                return result;
            }, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // Keeps only the newest backups. Runs after a successful replacement, so the backup just written always survives.
    private void PruneReplacementBackups()
    {
        var root = Path.Combine(applicationDirectory, "replacements");
        var stale = new DirectoryInfo(root).EnumerateDirectories()
            .Where(d => d.LinkTarget is null)
            // backup.json is written once per backup; a backup without it never reached a replacement.
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d.FullName, "backup.json")))
            .Skip(KeptReplacementBackups);
        foreach (var backup in stale)
        {
            try { backup.Delete(recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Read-only inspection used by replacement callers after writing files. It retains the source/storage rule.
    internal Task<CollectionManifest> InspectSourceAsync(string sourceRoot, CancellationToken token) =>
        scanner.ScanAsync(sourceRoot, token);
}
