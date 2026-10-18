using System.Security.Cryptography;

namespace PD2Cloud.Local;

public sealed partial class OperationPreparationStore
{
    // The callback cannot outlive the lease. Remote is the only production consumer; no public write capability.
    internal async Task<T> CoordinateUploadAsync<T>(string id, Func<UploadEvidence, Task<T>> action, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _) || id != id.ToLowerInvariant())
            throw new RecoveryBlockedException("Invalid operation identity.");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = AcquireLease();
            var entries = Directory.EnumerateFileSystemEntries(directory).Where(p => Path.GetFileName(p) != "operations.lock").ToArray();
            var path = Path.Combine(directory, id);
            if (entries.Length != 1 || entries[0] != path) throw new RecoveryBlockedException("Unresolved recovery evidence blocks upload.");
            RecoveryStorage.CheckPath(path);
            var (inventory, journal) = ReadJournal(path, id, ct);
            if (journal.Phase != PreparationPhase.Prepared) throw new RecoveryBlockedException("Preparation is incomplete.");
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                RecoveryStorage.CheckPath(child);
                var name = Path.GetFileName(child);
                if (name is not ("capture" or "backup" or "pending" or "upload" or "inventory.json") && !name.StartsWith("journal-", StringComparison.Ordinal))
                    throw new RecoveryBlockedException("Unknown operation evidence.");
                if (name is "capture" or "backup" or "pending" or "upload" && !Directory.Exists(child))
                    throw new RecoveryBlockedException("Unsafe operation directory.");
            }
            var pending = Path.Combine(path, "pending");
            if (Directory.Exists(pending) && Directory.EnumerateFileSystemEntries(pending).Any())
                throw new RecoveryBlockedException("Unexpected pending data.");
            var operation = ToOperation(inventory, path);
            async Task Revalidate(bool source)
            {
                VerifySnapshot(operation.CaptureDirectory, operation.Files, ct);
                VerifySnapshot(operation.BackupDirectory, operation.Files, ct);
                if (source) await VerifySourceAsync(operation.SourceRoot, operation.Files, ct).ConfigureAwait(false);
            }
            await Revalidate(true).ConfigureAwait(false);
            var preparationDigest = Digest(FileFingerprintReader.ReadBounded(Path.Combine(path, $"journal-{journal.Sequence:D6}.json"), MaxProgressBytes, ct));
            var evidence = new UploadEvidence(operation, preparationDigest, Path.Combine(path, "upload"), Revalidate,
                boundary => observer?.Reached("Upload:" + boundary, id, null), ct);
            try { return await action(evidence).ConfigureAwait(false); }
            finally
            {
                evidence.Close();
                // The next refresh re-reads the repository, so evidence is never needed after this call.
                try { DeleteEntry(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally { gate.Release(); }
    }
}

internal sealed class UploadEvidence(PreparedOperation operation, string preparationDigest, string directory,
    Func<bool, Task> revalidate, Action<string> boundary, CancellationToken ct)
{
    private bool closed;
    internal PreparedOperation Operation => operation;
    internal string PreparationDigest => preparationDigest;
    private void Check() { if (closed) throw new InvalidOperationException("Operation lease ended."); }
    internal void Close() => closed = true;
    internal Task RevalidateAsync(bool source = true) { Check(); return revalidate(source); }
    internal byte[] ReadFile(Core.FileEntry file)
    {
        Check();
        var path = Path.Combine(operation.CaptureDirectory, file.RelativePath);
        RecoveryStorage.CheckPath(path);
        var bytes = FileFingerprintReader.ReadBounded(path, 2 * 1024 * 1024, ct);
        if (bytes.LongLength != file.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
            throw new RecoveryBlockedException("Captured upload bytes changed.");
        return bytes;
    }
    internal Dictionary<string, byte[]> ReadRecords()
    {
        Check();
        RecoveryStorage.CheckPath(directory);
        var records = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return records;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            RecoveryStorage.CheckPath(path);
            var name = Path.GetFileName(path);
            if (name is not ("intent.json" or "candidate.json" or "published.json"))
                throw new RecoveryBlockedException("Unknown publication record.");
            records.Add(name, FileFingerprintReader.ReadBounded(path, 16 * 1024, ct));
        }
        if (records.Count == 0) throw new RecoveryBlockedException("Incomplete publication evidence.");
        return records;
    }
    internal void WriteRecord(string name, byte[] bytes)
    {
        Check();
        if (name is not ("intent.json" or "candidate.json" or "published.json") || bytes.Length > 16 * 1024)
            throw new RecoveryBlockedException("Invalid publication record.");
        ct.ThrowIfCancellationRequested();
        RecoveryStorage.CreateDirectory(directory);
        RecoveryStorage.WriteNew(Path.Combine(directory, name), bytes, b => boundary(name + ":" + b));
    }
}
