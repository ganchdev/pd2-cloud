using System.Text.Json;
using PD2Cloud.Core;

namespace PD2Cloud.Local;

public sealed partial class OperationPreparationStore
{
    /// <summary>
    /// Runs a source-replacement callback after writing a complete, independently verified backup of the
    /// current collection. The same storage gate and filesystem lease used by preparation exclude interleaving.
    /// The backup is retained even when the callback fails or is cancelled.
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
                // Unresolved publication/preparation evidence must be reviewed before local files are replaced.
                if (Directory.EnumerateFileSystemEntries(directory).Any(p => Path.GetFileName(p) != "operations.lock"))
                    throw new RecoveryBlockedException("Existing recovery evidence requires review before replacing local files.");
                var original = await scanner.ScanAsync(sourceRoot, token).ConfigureAwait(false);
                RequireValid(original);
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
                RequireValid(backed);
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
                return await replace(backup, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // Read-only inspection used by replacement callers after writing files. It retains the source/storage rule.
    internal Task<CollectionManifest> InspectSourceAsync(string sourceRoot, CancellationToken token) =>
        scanner.ScanAsync(sourceRoot, token);
}
