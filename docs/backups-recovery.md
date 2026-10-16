# Immutable capture, backups and recovery

Current task-8 extension: [safe upload](safe-upload.md) adds a coordinated initial-upload/publication-recovery API with repository/branch/pinned-head binding and durable candidate records. The desktop remains read-only with Sync All disabled. Successful-sync persistence and baseline ancestry remain unimplemented; publication does not establish synchronization success. Earlier task-specific transfer limitations below describe their original delivery boundary. Live GitHub and native acceptance remain open.

Task 7 implementation contract, 2026-10-16. `OperationPreparationStore` in Local provides local preparation and recovery assessment. It is deliberately not connected to desktop startup, Refresh or Sync All. The desktop still performs read-only inspection and advisory comparison. There are no transfers, source replacements, automatic restores, cleanup or successful-sync writes.

## Preparation contract

`PrepareAsync(sourceRoot, cancellationToken)` acquires the storage operation gate and a filesystem lease, rejects existing recovery evidence, and freshly inspects the entire selected physical root. It copies every eligible regular file into an independently owned capture, then copies the original source again into a separate complete backup. Both must match the same original inventory. Hidden, unknown, extensionless, empty and temporary files remain ordinary opaque bytes. There are no exclusions or format parsers.

Each copied file uses a new file, a bounded 64 KiB buffer, a read-only regular-file source handle, length and SHA-256 checks, a disk flush and a rename into its snapshot. No hard links to the source are created. Metadata inventories are rechecked before each copy and after each snapshot. Both complete snapshots receive the scanner's full two-pass verification; the source is freshly reinspected before Prepared is recorded. Size, hash, canonical path, collision and complete membership checks must all agree. Source bytes and write times are never changed.

The capture and backup preserve file bytes and canonical NFC relative paths, as specified by schema 1. Physical source spelling is retained for opening files. Empty directories are copied, but directory metadata/membership, ACLs, ownership and timestamps are outside the file-inventory contract. This is a verified collection backup, not a filesystem-image backup.

Published snapshot files are never modified by this service. Immutability means independent retained bytes and no in-place update API; application storage is not an OS-enforced immutable volume. Every recovery assessment rehashes evidence. Returned `PreparedOperation` metadata is read-only and is an observation, never permission to install or publish. External tampering or later source changes invalidate reuse. The scanner's limitations around adversarial ancestor replacement and external writers still apply; this does not lock the source against other programs.

Preparation currently supports at most 1,000 files, an 8 MiB inventory document and 16 KiB per progress record. These limits block the whole operation, never skip files. The generic inspector is unchanged and has no new file-count/extension filter. Copying streams bytes without a per-file memory allocation; sufficient application-storage capacity for two complete snapshots plus journals is required. Storage failure leaves evidence and the source intact. No space reservation or automatic pruning exists.

## Storage and durability

All task-7 data lives under `LocalApplicationData/PD2Cloud/recovery/`, already covered by the source/storage overlap rejection. Tests supply an isolated application directory. The selected source cannot contain, equal or lie within application storage. Linked roots, ancestors, snapshot entries, journals and locks are rejected. No staging is created beside or inside the selected source. Future source installation will need separately validated same-filesystem staging; these app-data captures do not establish that gate.

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
```

Schema-1 `inventory.json` contains operation ID, source root, ordered complete `FileEntry` inventory and collection fingerprint. It has no credentials, repository identity, baseline or synchronization-success flag. Each schema-1 journal record contains operation ID, sequence, SHA-256 of the preceding record's exact bytes, SHA-256 of the inventory's exact bytes, phase, optional file intent and completion flag. The journal is small: the inventory is stored once, not repeated per file. Strict decoding rejects duplicate, missing, unknown, malformed and unsupported fields, invalid paths/inventories, mismatched identity/digests, sequence gaps and impossible phase/file transitions. Digests detect inconsistent evidence; they are not authentication against a malicious writer with access to storage.

The state sequence is Capturing → Captured → BackingUp → Prepared. Each file has a durable intent before its snapshot copy and a completion record after flushed publication. These intents describe only app-owned copy operations; no source-replacement intent exists. Records and inventory use create-new temporary files, `Flush(true)`, close and same-directory rename without overwrite. Unix additionally fsyncs created directories and rename parents, including both parents of a copied-file move. Windows publication uses write-through `MoveFileEx` without replacement. Unsupported/failed durability calls abort. A multi-file snapshot is not atomic; only a complete verified inventory can reach Prepared.

This establishes a restart protocol and tests process interruption. It does not establish hardware/power-loss guarantees on every filesystem, network share or platform. Windows directory creation and rename durability, Linux/macOS filesystem variants and native denied/full-disk behavior require acceptance. Existing folder-settings durability is unchanged.

## Serialization and cancellation

All preparation/assessment instances for the same canonical application directory share an in-process semaphore. A held `FileShare.None` lease on `operations.lock` also excludes cooperating processes; another process fails closed with a busy/unavailable notice. The OS releases it on process death. No lock file is deleted or stolen. Gate release is covered on success, error and cancellation, including cancellation while waiting. Source aliases/adversarial storage replacement are outside this cooperative lease boundary. GitHub credential rotation and preferences keep their existing independent gates; there is no transfer coordinator yet.

Cancellation is observed while waiting, scanning, reading chunks, creating snapshot directories and before committing Prepared. A begun file flush/rename/journal publication finishes or fails at that boundary. Cancellation does not delete partial evidence, roll back source files or promise that an already durable Prepared record disappears. All operation directories, captures, backups and orphan temporary files are retained. Any existing operation directory, including a Prepared one, blocks another preparation until a later explicit reviewed lifecycle is implemented. There is no abandonment/deletion bypass.

## Recovery assessment

`AssessRecoveryAsync(cancellationToken)` takes the same gate/lease and reconstructs journal history from disk. It does not resume copying, install files, mark synchronization success or rewrite journals. It verifies every file claimed complete; a completed capture and final backup also require full fresh snapshot inspection.

| Status | Meaning |
|---|---|
| Prepared | Final journal, both complete snapshots and current source inventory verify. No installation or synchronization occurred. |
| Interrupted | A valid journal prefix exists, but preparation stopped before Prepared. Retain evidence and review; partial snapshots cannot authorize further work. |
| SourceChanged | Complete prepared evidence verifies, but the source changed, disappeared or cannot be freshly verified. Preserve the user's current files. |
| Blocked | Missing/corrupt/future/unknown/linked evidence, sequence errors, temporary journal residue, corrupt claimed copies or inaccessible backups. Preserve evidence and source for manual review; do not speculate about rollback. |

Repeated assessments yield the same result for unchanged inputs and preserve evidence/source bytes. An orphan directory without a committed journal is Blocked. Temporary records never count as committed. A final record whose rename succeeded can yield Prepared after a lost return/abrupt exit only when all evidence and the current source still verify. Busy/unsafe storage itself raises a blocking exception rather than returning an empty successful assessment.

## Verification and later gates

From the project root, run `mise run test -- --filter FullyQualifiedName~OperationPreparationTests` for the 52 task-7 cases, or `mise run test` for the complete suite. Fixtures use disposable temporary folders and synthetic bytes, not the selected saves or live GitHub. Run `mise run build` and `mise run format:check` for the remaining checks. `mise run run` exercises the existing desktop only; it does not invoke the preparation API.

Task-7 tests exercise complete/empty/Unicode/opaque inventories, independent snapshots, unchanged source bytes/times and settings, no baseline, source edits during/after preparation, missing/changed/extra backup data, corrupt/future/duplicate/unknown journals and inventories, unknown entries, bounded metadata, cancellation, serialization, links and whole-operation file-count limits. A trace-driven test interrupts every observed inventory/journal flush, rename, directory-flush, file-chunk and publication boundary using a fresh store per interruption. These are abrupt exceptions without service cleanup, followed by repeated recovery from new instances.

`tests/PD2Cloud.RecoveryProbe` is a test-only executable. Subprocess tests exit without managed cleanup during a journal temporary flush, partial copy, file rename and final Prepared publication. Another test holds the lease in a separate process, proves exclusion, kills the process and assesses retained evidence after OS lease release. They do not simulate sudden power loss. Windows link fixtures return early; native Windows/Linux execution and hosted CI remain open. Task 7 changes no desktop views, so no new UI-rendering acceptance is claimed.

Validation on macOS: all **260 tests** pass (52 added task-7 cases, including the trace-driven interruption loop and five subprocess cases), standard `mise run build` passes with zero warnings/errors, and `mise run format:check` and diff checks pass. An initial NuGet vulnerability-cache access warning was cleared with an approved outside-sandbox forced restore; final standard checks have no warnings. No package, telemetry, audit or credential configuration changed.

Before tasks 8–9 enable any writes: bind reviewed local evidence to a freshly validated repository/branch/pinned commit and operation direction; extend journals for candidate publication/lost responses and source per-file replacement intent; verify baseline ancestry when a real baseline exists; revalidate source and immutable bytes under the operation coordinator; create same-filesystem install staging; test every actual source-rename and baseline boundary. No source mutation API is present here to bypass those gates. Adoption/removal policy, initialization, automatic merge/deletion, background sync and successful-sync persistence remain disabled or unimplemented. Live GitHub and native platform acceptance remain open.
