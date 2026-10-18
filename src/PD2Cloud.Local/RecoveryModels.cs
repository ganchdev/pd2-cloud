using PD2Cloud.Core;

namespace PD2Cloud.Local;

public enum PreparationPhase { Capturing, Captured, BackingUp, Prepared }
public enum RecoveryStatus { Prepared, Interrupted, SourceChanged, Blocked }

/// <summary>Preparation evidence only. It never authorizes replacement or establishes synchronization.</summary>
public sealed record PreparedOperation(string OperationId, string SourceRoot, string CaptureDirectory,
    string BackupDirectory, IReadOnlyList<FileEntry> Files, string CollectionFingerprint);

public sealed record RecoveryAssessment(string OperationId, RecoveryStatus Status, string Message,
    PreparedOperation? Operation = null);

/// <summary>A complete retained copy of the local collection created immediately before a reviewed replacement.</summary>
public sealed record ReplacementBackup(string BackupId, string SourceRoot, string BackupDirectory,
    IReadOnlyList<FileEntry> Files, string CollectionFingerprint, DateTimeOffset CreatedAt);

internal sealed record ReplacementBackupRecord(int SchemaVersion, string BackupId, string SourceRoot,
    FileEntry[] Files, string CollectionFingerprint, DateTimeOffset CreatedAt);

public sealed class RecoveryBlockedException(string message) : IOException(message);

internal sealed record PreparationInventory(int SchemaVersion, string OperationId, string SourceRoot,
    FileEntry[] Files, string CollectionFingerprint);

// Each append-only progress record binds the immutable inventory and its predecessor's exact bytes.
internal sealed record PreparationJournal(int SchemaVersion, string OperationId, int Sequence,
    string? PreviousDigest, string InventoryDigest, PreparationPhase Phase, string? FileIntent, bool FileCompleted);

/// <summary>Test seam for abrupt interruption, including after a rename but before the next journal record.</summary>
internal interface IPreparationObserver
{
    void Reached(string boundary, string operationId, string? relativePath);
}
