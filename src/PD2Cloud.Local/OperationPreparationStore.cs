using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PD2Cloud.Core;

namespace PD2Cloud.Local;

/// <summary>
/// Local-only safety gate. Captures and backs up opaque files without changing the source.
/// No installation, rollback, transfer, cleanup or successful-sync API is provided.
/// </summary>
public sealed class OperationPreparationStore
{
    private const int MaxJournalBytes = 8 * 1024 * 1024;
    private const int MaxProgressBytes = 16 * 1024;
    private const int MaxFiles = 1000;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };
    private readonly string applicationDirectory;
    private readonly string directory;
    private readonly SemaphoreSlim gate;
    private readonly IPreparationObserver? observer;
    private readonly CollectionScanner scanner;

    public OperationPreparationStore(string? applicationDirectory = null) : this(applicationDirectory, null) { }

    internal OperationPreparationStore(string? applicationDirectory, IPreparationObserver? observer)
    {
        this.applicationDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory ?? ApplicationDataPaths.DirectoryPath));
        directory = Path.Combine(this.applicationDirectory, "recovery");
        var key = OperatingSystem.IsLinux() ? directory : directory.ToUpperInvariant();
        gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        scanner = new CollectionScanner(applicationDirectory: this.applicationDirectory);
        this.observer = observer;
    }

    public async Task<PreparedOperation> PrepareAsync(string sourceRoot, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(sourceRoot) || !ApplicationDataPaths.IsSourceAllowed(sourceRoot, applicationDirectory))
            throw new RecoveryBlockedException("Select a physical source outside application storage.");
        sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(async () =>
            {
                using var lease = AcquireLease();
                if (Directory.EnumerateFileSystemEntries(directory).Any(p => Path.GetFileName(p) != "operations.lock"))
                    throw new RecoveryBlockedException("Existing recovery evidence requires review before preparing another operation.");
                var original = await scanner.ScanAsync(sourceRoot, token);
                RequireValid(original);
                if (original.Files.Count > MaxFiles) throw new RecoveryBlockedException("Preparation supports at most 1,000 files; nothing was excluded or changed.");
                var issues = new List<ValidationIssue>();
                var metadata = CollectionScanner.Inventory(sourceRoot, token, issues);
                if (issues.Count != 0) throw new RecoveryBlockedException("Source inventory is unsafe.");
                var id = Guid.NewGuid().ToString("N");
                var operationDirectory = Path.Combine(directory, id);
                token.ThrowIfCancellationRequested();
                RecoveryStorage.CreateDirectory(operationDirectory);
                var inventory = new PreparationInventory(1, id, sourceRoot, original.Files.ToArray(), original.CollectionFingerprint!);
                var inventoryBytes = JsonSerializer.SerializeToUtf8Bytes(inventory, JsonOptions);
                if (inventoryBytes.Length > MaxJournalBytes) throw new RecoveryBlockedException("Recovery inventory exceeds the metadata size limit.");
                RecoveryStorage.WriteNew(Path.Combine(operationDirectory, "inventory.json"), inventoryBytes,
                    boundary => observer?.Reached("Inventory:" + boundary, id, null));
                var journal = new PreparationJournal(1, id, 0, null, Digest(inventoryBytes), PreparationPhase.Capturing, null, false);
                void Record(PreparationPhase phase, string? intent = null, bool completed = false)
                {
                    journal = journal with { Phase = phase, FileIntent = intent, FileCompleted = completed };
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
                    if (bytes.Length > MaxProgressBytes) throw new RecoveryBlockedException("Recovery progress exceeds the journal size limit.");
                    var path = Path.Combine(operationDirectory, $"journal-{journal.Sequence:D6}.json");
                    observer?.Reached("BeforeJournal", id, intent);
                    RecoveryStorage.WriteNew(path, bytes, boundary => observer?.Reached(boundary, id, intent));
                    observer?.Reached($"Journal:{phase}:{completed}", id, intent);
                    journal = journal with { Sequence = journal.Sequence + 1, PreviousDigest = Digest(bytes) };
                }
                Record(PreparationPhase.Capturing);
                var capture = Path.Combine(operationDirectory, "capture");
                var backup = Path.Combine(operationDirectory, "backup");
                Capture(capture, PreparationPhase.Capturing);
                Record(PreparationPhase.Captured);
                Record(PreparationPhase.BackingUp);
                Capture(backup, PreparationPhase.BackingUp);
                VerifySnapshot(capture, original.Files, token);
                VerifySnapshot(backup, original.Files, token);
                await VerifySourceAsync(sourceRoot, original.Files, token);
                token.ThrowIfCancellationRequested();
                Record(PreparationPhase.Prepared);
                return ToOperation(inventory, operationDirectory);

                void Capture(string destination, PreparationPhase phase)
                {
                    RecoveryStorage.CreateDirectory(destination);
                    foreach (var entry in metadata.Where(m => m.IsDirectory).OrderBy(m => m.RelativePath.Length))
                    {
                        token.ThrowIfCancellationRequested();
                        RecoveryStorage.CreateDirectory(Path.Combine(destination, entry.RelativePath));
                    }
                    foreach (var file in original.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        Record(phase, file.RelativePath);
                        var entry = metadata.SingleOrDefault(m => !m.IsDirectory && m.RelativePath == file.RelativePath)
                            ?? throw new RecoveryBlockedException("Source membership changed during capture.");
                        CheckSourceInventory(sourceRoot, metadata, token);
                        var target = Path.Combine(destination, file.RelativePath);
                        // Temporary data uses its own directory, so it cannot collide with an eligible filename.
                        var temporaryDirectory = Path.Combine(operationDirectory, "pending");
                        RecoveryStorage.CreateDirectory(temporaryDirectory);
                        var temporary = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N"));
                        FileFingerprint copied;
                        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            copied = new FileFingerprintReader().Copy(entry.FullPath, stream, token,
                                () => observer?.Reached("FileChunkWritten", id, file.RelativePath));
                            stream.Flush(flushToDisk: true);
                        }
                        observer?.Reached("FileFlushed", id, file.RelativePath);
                        if (copied.SizeBytes != file.SizeBytes || copied.Sha256 != file.Sha256)
                            throw new RecoveryBlockedException("Source bytes changed during capture.");
                        RecoveryStorage.Publish(temporary, target, boundary => observer?.Reached("File" + boundary, id, file.RelativePath));
                        // The rename spans two app-owned directories: flush removal from the temporary parent too.
                        RecoveryStorage.FlushDirectory(temporaryDirectory);
                        observer?.Reached("FilePublished", id, file.RelativePath);
                        Record(phase, file.RelativePath, true);
                    }
                    VerifySnapshot(destination, original.Files, token);
                    CheckSourceInventory(sourceRoot, metadata, token);
                }
            }, token);
        }
        finally { gate.Release(); }
    }

    /// <summary>Rechecks evidence only. Repeated recovery never writes source files or repairs damaged evidence.</summary>
    public async Task<IReadOnlyList<RecoveryAssessment>> AssessRecoveryAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(async () =>
            {
                using var lease = AcquireLease();
                var results = new List<RecoveryAssessment>();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry);
                    if (name == "operations.lock") continue;
                    try
                    {
                        if (!Guid.TryParseExact(name, "N", out _) || !Directory.Exists(entry))
                            throw new RecoveryBlockedException("Unrecognized recovery entry.");
                        RecoveryStorage.CheckPath(entry);
                        var (inventory, journal) = ReadJournal(entry, name, token);
                        foreach (var child in Directory.EnumerateFileSystemEntries(entry))
                        {
                            var childName = Path.GetFileName(child);
                            if (childName is not ("capture" or "backup" or "pending" or "inventory.json") && !childName.StartsWith("journal-", StringComparison.Ordinal))
                                throw new RecoveryBlockedException("Unknown operation evidence.");
                            RecoveryStorage.CheckPath(child);
                            if (childName is "capture" or "backup" or "pending" && !Directory.Exists(child))
                                throw new RecoveryBlockedException("Snapshot paths must be physical directories.");
                        }
                        if (journal.Phase >= PreparationPhase.Captured)
                            VerifySnapshot(Path.Combine(entry, "capture"), inventory.Files, token);
                        if (journal.Phase != PreparationPhase.Prepared)
                        {
                            results.Add(new(name, RecoveryStatus.Interrupted, "Preparation was interrupted. Retain all evidence for review; the source was never replaced."));
                            continue;
                        }
                        VerifySnapshot(Path.Combine(entry, "backup"), inventory.Files, token);
                        var pending = Path.Combine(entry, "pending");
                        if (Directory.Exists(pending) && Directory.EnumerateFileSystemEntries(pending).Any())
                            throw new RecoveryBlockedException("Unexpected pending bytes in a completed preparation.");
                        var operation = ToOperation(inventory, entry);
                        var source = await scanner.ScanAsync(inventory.SourceRoot, token);
                        results.Add(CollectionInventory.IsValidManifest(source) && Equal(source.Files, inventory.Files)
                            ? new(name, RecoveryStatus.Prepared, "Capture and complete backup verified. No synchronization or installation has occurred.", operation)
                            : new(name, RecoveryStatus.SourceChanged, "The source changed or cannot be verified. Review it before any future operation; backups were retained."));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
                    {
                        results.Add(new(name, RecoveryStatus.Blocked, "Recovery evidence is damaged, unsupported or inaccessible. Preserve it and the source for manual review; no automatic restore is safe."));
                    }
                }
                return (IReadOnlyList<RecoveryAssessment>)results.AsReadOnly();
            }, token);
        }
        finally { gate.Release(); }
    }

    private FileStream AcquireLease()
    {
        RecoveryStorage.CreateDirectory(directory);
        var path = Path.Combine(directory, "operations.lock");
        if (new FileInfo(path).LinkTarget is not null) throw new RecoveryBlockedException("Linked operation lock is unsafe.");
        RecoveryStorage.CheckPath(path);
        try
        {
            if (File.Exists(path)) _ = FileFingerprintReader.ReadBounded(path, 0, CancellationToken.None);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { throw new RecoveryBlockedException("Recovery storage is busy or unavailable. Close other app instances and retry."); }
    }

    private (PreparationInventory Inventory, PreparationJournal Journal) ReadJournal(string path, string id, CancellationToken token)
    {
        var inventoryPath = Path.Combine(path, "inventory.json");
        RecoveryStorage.CheckPath(inventoryPath);
        var inventoryBytes = FileFingerprintReader.ReadBounded(inventoryPath, MaxJournalBytes, token);
        using (var document = JsonDocument.Parse(inventoryBytes)) CheckDuplicateProperties(document.RootElement);
        var inventory = JsonSerializer.Deserialize<PreparationInventory>(inventoryBytes, JsonOptions)
            ?? throw new JsonException("Missing inventory.");
        if (inventory.SchemaVersion != 1 || inventory.OperationId != id || inventory.Files is null || inventory.Files.Length > MaxFiles
            || !CollectionInventory.IsValidFiles(inventory.Files) || inventory.CollectionFingerprint != CollectionInventory.Fingerprint(inventory.Files)
            || !inventory.Files.SequenceEqual(inventory.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            || !Path.IsPathFullyQualified(inventory.SourceRoot) || inventory.SourceRoot != Path.TrimEndingDirectorySeparator(Path.GetFullPath(inventory.SourceRoot))
            || !ApplicationDataPaths.IsSourceAllowed(inventory.SourceRoot, applicationDirectory))
            throw new RecoveryBlockedException("Invalid recovery inventory.");
        var inventoryDigest = Digest(inventoryBytes);
        var records = Directory.EnumerateFileSystemEntries(path, "journal-*").Order(StringComparer.Ordinal).ToArray();
        if (records.Length == 0 || records.Length > 4 * MaxFiles + 4) throw new RecoveryBlockedException("Incomplete journal.");
        PreparationJournal? latest = null;
        string? previous = null;
        var events = new List<(PreparationPhase Phase, string? Path, bool Completed)>();
        var completedFiles = new List<(PreparationPhase Phase, string Path)>();
        for (var i = 0; i < records.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (Path.GetFileName(records[i]) != $"journal-{i:D6}.json") throw new RecoveryBlockedException("Journal sequence is incomplete.");
            RecoveryStorage.CheckPath(records[i]);
            var bytes = FileFingerprintReader.ReadBounded(records[i], MaxProgressBytes, token);
            using (var document = JsonDocument.Parse(bytes)) CheckDuplicateProperties(document.RootElement);
            var record = JsonSerializer.Deserialize<PreparationJournal>(bytes, JsonOptions)
                ?? throw new JsonException("Missing journal.");
            if (record.SchemaVersion != 1 || record.OperationId != id || record.Sequence != i || record.PreviousDigest != previous
                || record.InventoryDigest != inventoryDigest || !Enum.IsDefined(record.Phase))
                throw new RecoveryBlockedException("Invalid recovery record.");
            if (i == 0)
            {
                events.Add((PreparationPhase.Capturing, null, false));
                AddFiles(PreparationPhase.Capturing);
                events.Add((PreparationPhase.Captured, null, false));
                events.Add((PreparationPhase.BackingUp, null, false));
                AddFiles(PreparationPhase.BackingUp);
                events.Add((PreparationPhase.Prepared, null, false));
            }
            if (i >= events.Count || events[i] != (record.Phase, record.FileIntent, record.FileCompleted))
                throw new RecoveryBlockedException("Invalid journal transition.");
            if (record.FileCompleted) completedFiles.Add((record.Phase, record.FileIntent!));
            previous = Digest(bytes);
            latest = record;
        }
        foreach (var file in completedFiles)
        {
            var snapshot = file.Phase == PreparationPhase.Capturing ? "capture" : "backup";
            var physical = Path.Combine(path, snapshot, file.Path);
            RecoveryStorage.CheckPath(physical);
            var fingerprint = new FileFingerprintReader().Read(physical, token);
            var expected = inventory.Files.Single(f => f.RelativePath == file.Path);
            if (fingerprint.SizeBytes != expected.SizeBytes || fingerprint.Sha256 != expected.Sha256)
                throw new RecoveryBlockedException("Previously verified snapshot bytes changed.");
        }
        return (inventory, latest!);

        void AddFiles(PreparationPhase phase)
        {
            foreach (var file in inventory.Files)
            {
                events.Add((phase, file.RelativePath, false));
                events.Add((phase, file.RelativePath, true));
            }
        }
    }

    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate journal property.");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckDuplicateProperties(child);
    }

    private static PreparedOperation ToOperation(PreparationInventory inventory, string directory) => new(inventory.OperationId,
        inventory.SourceRoot, Path.Combine(directory, "capture"), Path.Combine(directory, "backup"),
        Array.AsReadOnly(inventory.Files.ToArray()), inventory.CollectionFingerprint);

    private void VerifySnapshot(string path, IReadOnlyList<FileEntry> expected, CancellationToken token)
    {
        RecoveryStorage.CheckPath(path);
        var manifest = scanner.ScanOwned(path, token);
        RequireValid(manifest);
        if (!Equal(manifest.Files, expected)) throw new RecoveryBlockedException("Snapshot membership or bytes do not match the complete original inventory.");
    }

    private async Task VerifySourceAsync(string path, IReadOnlyList<FileEntry> expected, CancellationToken token)
    {
        var manifest = await scanner.ScanAsync(path, token);
        RequireValid(manifest);
        if (!Equal(manifest.Files, expected)) throw new RecoveryBlockedException("Source changed; preserve the captured evidence and review.");
    }

    private static void RequireValid(CollectionManifest manifest)
    {
        if (!CollectionInventory.IsValidManifest(manifest)) throw new RecoveryBlockedException("The complete inventory could not be verified; no source file was changed.");
    }

    private static void CheckSourceInventory(string root, CollectionScanner.Metadata[] before, CancellationToken token)
    {
        try { CollectionScanner.CheckInventory(root, before, token); }
        catch (CollectionScanner.UnstableCollectionException)
        {
            throw new RecoveryBlockedException("Source membership or metadata changed during preparation.");
        }
    }

    private static bool Equal(IReadOnlyList<FileEntry> a, IReadOnlyList<FileEntry> b) =>
        a.OrderBy(f => f.RelativePath, StringComparer.Ordinal).SequenceEqual(b.OrderBy(f => f.RelativePath, StringComparer.Ordinal));
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
