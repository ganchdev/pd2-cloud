# Immutable capture, backups and recovery

`OperationPreparationStore` in Local captures and backs up the selected folder before every upload, and writes a separate complete backup before every download replaces local files. Sync drives both through Remote ([safe upload](safe-upload.md) and `CollectionPuller`); see [sync](sync.md). Startup, selection and Refresh never prepare, back up or replace anything.

Operation evidence under `recovery/` is scratch space for one sync: it is deleted when the upload call ends, and anything left behind by a crash is discarded by the next preparation or download. Download backups under `replacements/` are kept, up to the newest five.

## Preparation contract

`PrepareAsync(sourceRoot, cancellationToken)` acquires the storage operation gate and a filesystem lease, discards any leftover operation evidence, and freshly inspects the entire selected physical root. It copies every eligible regular file into an independently owned capture, then copies the original source again into a separate complete backup. Both must match the same original inventory. Hidden, unknown, extensionless, empty and temporary files remain ordinary opaque bytes. There are no exclusions or format parsers.

Each copied file uses a new file, a bounded 64 KiB buffer, a read-only regular-file source handle, length and SHA-256 checks, a disk flush and a rename into its snapshot. No hard links to the source are created. Metadata inventories are rechecked before each copy and after each snapshot. Both complete snapshots receive the scanner's full two-pass verification; the source is freshly reinspected before Prepared is recorded. Size, hash, canonical path, collision and complete membership checks must all agree. Source bytes and write times are never changed.

The capture and backup preserve file bytes and canonical NFC relative paths, as specified by schema 1. Physical source spelling is retained for opening files. Empty directories are copied, but directory metadata/membership, ACLs, ownership and timestamps are outside the file-inventory contract. This is a verified collection backup, not a filesystem-image backup.

Snapshot files are never modified in place while an operation lives. Immutability means independent bytes and no in-place update API; application storage is not an OS-enforced immutable volume. The uploader rehashes evidence before use. Returned `PreparedOperation` metadata is read-only and is never permission to publish on its own. External tampering or later source changes block the upload. The scanner's limitations around adversarial ancestor replacement and external writers still apply; this does not lock the source against other programs.

Preparation currently supports at most 1,000 files, an 8 MiB inventory document and 16 KiB per progress record. These limits block the whole operation, never skip files. The generic inspector is unchanged and has no new file-count/extension filter. Copying streams bytes without a per-file memory allocation; sufficient application-storage capacity for two complete snapshots plus journals is required. Storage failure leaves the source intact. No space reservation exists.

## Storage and durability

Operation data lives under `LocalApplicationData/PD2Cloud/recovery/` and download backups under `LocalApplicationData/PD2Cloud/replacements/`, both covered by the source/storage overlap rejection. Tests supply an isolated application directory. The selected source cannot contain, equal or lie within application storage. Linked roots, ancestors, snapshot entries, journals and locks are rejected. No staging is created beside or inside the selected source.

```text
recovery/
  operations.lock
  <32-lowercase-hex-operation-id>/
    inventory.json
    journal-000000.json
    journal-000001.json
    ...
    capture/<complete canonical file tree>
    backup/<complete canonical file tree>
    pending/<unique temporary copy files, if interrupted>
    upload/<intent/candidate/published records while an upload runs>
replacements/
  <32-lowercase-hex-backup-id>/
    backup.json
    files/<complete canonical file tree>
```

Schema-1 `inventory.json` contains operation ID, source root, ordered complete `FileEntry` inventory and collection fingerprint. It has no credentials, repository identity, baseline or synchronization-success flag. Each schema-1 journal record contains operation ID, sequence, SHA-256 of the preceding record's exact bytes, SHA-256 of the inventory's exact bytes, phase, optional file intent and completion flag. The journal is small: the inventory is stored once, not repeated per file. Strict decoding rejects duplicate, missing, unknown, malformed and unsupported fields, invalid paths/inventories, mismatched identity/digests, sequence gaps and impossible phase/file transitions. Digests detect inconsistent evidence; they are not authentication against a malicious writer with access to storage.

The state sequence is Capturing → Captured → BackingUp → Prepared. Each file has a durable intent before its snapshot copy and a completion record after flushed publication. These intents describe only app-owned copy operations; no source-replacement intent exists. Records and inventory use create-new temporary files, `Flush(true)`, close and same-directory rename without overwrite. Unix additionally fsyncs created directories and rename parents, including both parents of a copied-file move. Windows publication uses write-through `MoveFileEx` without replacement. Unsupported/failed durability calls abort. A multi-file snapshot is not atomic; only a complete verified inventory can reach Prepared.

This establishes a restart protocol and tests process interruption. It does not establish hardware/power-loss guarantees on every filesystem, network share or platform. Windows directory creation and rename durability, Linux/macOS filesystem variants and native denied/full-disk behavior require acceptance. Existing folder-settings durability is unchanged.

## Serialization and cancellation

All preparation/assessment instances for the same canonical application directory share an in-process semaphore. A held `FileShare.None` lease on `operations.lock` also excludes cooperating processes; another process fails closed with a busy/unavailable notice. The desktop additionally holds `instance.lock` in the application directory for its lifetime, so a second launch shows an already-running notice instead of starting and contending for recovery storage. The OS releases both leases on process death. No lock file is deleted or stolen. Gate release is covered on success, error and cancellation, including cancellation while waiting. Source aliases/adversarial storage replacement are outside this cooperative lease boundary. Uploads hold this storage gate and then the GitHub connection gate (see [safe upload](safe-upload.md)); downloads hold it while backing up and installing.

Cancellation is observed while waiting, scanning, reading chunks, creating snapshot directories and before committing Prepared. A begun file flush/rename/journal publication finishes or fails at that boundary. Cancellation never rolls back source files. Partial operation evidence left by cancellation or a crash is not reviewed or resumed; the next preparation or download discards it before starting.

## Download backups

`CollectionPuller` re-reads and re-verifies the pinned cloud revision, then, under the same gate and lease, copies the whole local folder into `replacements/<id>/files/` with per-file size/SHA-256 checks and flushes, verifies the complete backup and writes `backup.json` before any local file is replaced. It then installs the verified cloud bytes and re-inspects the folder. If installation or verification fails (or is cancelled), it restores the backup automatically and reports where the backup is; if restore cannot complete, the backup is left in place. A successful download records the sync. After a successful download only the newest five backups are kept; older ones are deleted.

## Recovery assessment (unused)

`AssessRecoveryAsync(cancellationToken)` remains as a Local API but nothing in the app calls it. It takes the same gate/lease and reconstructs journal history from disk without resuming, installing or rewriting anything. It verifies every file claimed complete; a completed capture and final backup also require full fresh snapshot inspection. An operation containing `upload/` is reported Blocked (the next sync discards it).

| Status | Meaning |
|---|---|
| Prepared | Final journal, both complete snapshots and current source inventory verify. |
| Interrupted | A valid journal prefix exists, but preparation stopped before Prepared. |
| SourceChanged | Complete prepared evidence verifies, but the source changed, disappeared or cannot be freshly verified. |
| Blocked | Upload evidence, or missing/corrupt/future/unknown/linked evidence, sequence errors, temporary journal residue, corrupt claimed copies or inaccessible backups. |

Busy/unsafe storage raises a blocking exception rather than returning an empty successful assessment.

## Verification

From the project root, run `mise run test -- --filter FullyQualifiedName~OperationPreparationTests`, or `mise run test` for the complete suite. Fixtures use disposable temporary folders and synthetic bytes, not the selected saves or live GitHub. Run `mise run build` and `mise run format:check` for the remaining checks.

The preparation tests exercise complete/empty/Unicode/opaque inventories, independent snapshots, unchanged source bytes/times and settings, source edits during/after preparation, corrupt journals and inventories, bounded metadata, cancellation, serialization, links and whole-operation file-count limits. A trace-driven test interrupts every observed inventory/journal flush, rename, directory-flush and file-chunk boundary using a fresh store per interruption. `tests/PD2Cloud.RecoveryProbe` is a test-only executable used by subprocess tests that exit without managed cleanup and that prove lease exclusion across processes. They do not simulate sudden power loss. Windows link fixtures return early; native Windows/Linux execution and hosted CI remain open.
