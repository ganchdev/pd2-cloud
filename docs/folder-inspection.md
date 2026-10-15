# Generic folder inspection

Implementation reference, updated 2026-10-15. Task 2 is implemented; folder persistence and cloud synchronization are not.

PD2 Cloud is a generic folder synchronization engine. All regular files within the selected directory are treated as opaque binary data. The product's save-folder wording is presentation only; discovery contains no format knowledge or file-content parsing.

## Inclusion and traversal

Inspect all regular files recursively, including hidden, extensionless, unknown, backup, configuration and temporary files. There is no extension allowlist or automatic filename exclusion. Preserve file bytes and actual names. Empty directories are traversed but are not manifest entries; the snapshot describes files, not directory metadata.

Relative manifest paths use `/` separators and Unicode NFC normalization. Physical paths retain their original spelling. Sort entries using ordinal comparison of canonical paths. Reject case-insensitive/NFC collisions across files and directories, including empty directories. Unsafe segments include traversal, rooted paths, separators within names, Windows reserved names, control characters, alternate-stream syntax and trailing dots/spaces.

Symbolic links, Windows reparse points, linked roots/ancestors and unsupported entries are blocked, never followed. This prevents recursive link loops. Unreadable directories/files and unsafe paths produce diagnostics. An unresolved issue invalidates the entire inspection; the scanner returns no partial file inventory or collection fingerprint. The UI shows Needs attention or Failed with the offending paths where available. No omitted entry is treated as a complete collection.

The inspector creates no state, staging directories or temporary files. A user-created `.tmp` file is an ordinary included file. Future application-generated state and staging must live outside the source tree and must never be silently excluded by filename rules.

## Manifest schema 1

`CollectionManifest` contains:

- `SchemaVersion`: currently 1.
- `RootPath`: configured absolute local path, never part of collection equality.
- `Files`: deterministic ordered `FileEntry` list with canonical `RelativePath`, `SizeBytes` and lowercase hexadecimal `Sha256`.
- `CollectionFingerprint`: lowercase SHA-256 of the complete path/content inventory, or null for invalid inspection.
- `InspectedAt`: UTC timestamp, diagnostic only.
- `IsStable` and `Issues`: observation result and generic `ValidationIssue` diagnostics.

Collection fingerprint encoding is fixed: UTF-8 bytes of `PD2Cloud.Collection.v1` followed by one NUL byte, then each file in ordinal canonical-path order. Each entry is a four-byte big-endian UTF-8 path byte length, the path bytes and its raw 32-byte SHA-256 content hash. No timestamp, local root, filesystem enumeration order or platform metadata participates. The empty inventory has a valid fingerprint. Renames, additions, removals and byte changes change the fingerprint. Equivalent trees on different operating systems produce identical file inventories/fingerprints; whole diagnostic manifests differ in root and inspection time.

A successful inspection is never a successfully synchronized baseline. The UI uses Scanning, Scanned successfully, Needs attention and Failed. It does not display cloud synchronization states.

## Desktop workflow

Before selection, the app displays PD2 Cloud, “Find your saves”, the requested save-folder description and Select Folder. After selection it displays the selected path, file list/count, last inspection time, inspection status, Refresh and Change Folder. The list uses relative paths and has no character/stash grouping or configuration controls.

| Status | Meaning |
|---|---|
| Scanning | An inspection is in progress; previous results are cleared |
| Scanned successfully | The complete inventory passed stability and filesystem validation, including an empty folder |
| Needs attention | Safety or stability checks block a complete valid inventory |
| Failed | Folder access, enumeration, reading or an unexpected error prevented inspection |

Incomplete scans show “File inventory incomplete”; they do not claim an empty collection. Inspection Details exposes issue paths/messages, sizes, hashes and the collection fingerprint. Cancelling or failing the folder picker retains the current selection/results. Changing the folder starts a new inspection. There is no automatic background scan or synchronization, and selecting a folder does not persist it across restarts.

## Read-only stability checks

Inspection runs on a worker thread and honors cancellation throughout traversal and hashing. Each inspection takes three full recursive metadata inventories (canonical/physical paths, entry kinds, sizes and write times) surrounding two complete SHA-256 passes. Both hash lists and all inventories must match. A detectable edit, new entry, removal, read failure or directory-enumeration failure invalidates the result. Refresh always scans again; it does not cache prior hashes. Superseded scans are cancelled and their late results/errors are discarded.

Retained file reading uses read-only handles, bounded buffers and length checks. On macOS/Linux x64/arm64, nonblocking/no-follow opens and native handle type checks reject pipes and other nonregular files. Windows checks reparse/device/directory attributes and opens with restrictive read sharing. No contents are interpreted or executed.

These observations are not an atomic snapshot or a lock against external writers. Transient changes that return to identical data may escape detection. Ancestor checks do not protect against adversarial replacement between checks and opens. A file may change immediately after inspection. Future synchronization must capture immutable bytes and revalidate before any write; inspection results alone cannot authorize replacement.

## Local state boundary

`LocalSyncState` only defines a future successful-sync record: schema version, selected repository identity/branch, accepted Git commit, local root, file fingerprints and successful-sync timestamp. No instance is fabricated by inspection, no state file is currently written or read, and no migration runs at startup. Prior folder/profile choices were in memory only, so there is no existing persisted configuration to migrate. Future JSON decoding ignores unknown obsolete fields while retaining the defined unrelated fields; persistence must preserve unrelated settings/credentials when it is introduced.

`ApplicationDataPaths.LocalSyncStatePath` reserves `local-sync-state.json` beneath `Environment.SpecialFolder.LocalApplicationData/PD2Cloud`, outside the source directory. Typical locations are `%LOCALAPPDATA%/PD2Cloud` on Windows, `~/Library/Application Support/PD2Cloud` on macOS and `$XDG_DATA_HOME/PD2Cloud` (usually `~/.local/share/PD2Cloud`) on Linux, as resolved by .NET. This is a path boundary only, not a persistence service. Future onboarding must reject a source containing the application state/staging location rather than creating sync loops.

## Verification and limitations

Recorded local verification on 2026-10-15:

- `mise run build`: passed, zero warnings/errors.
- `mise run format:check`: passed.
- `mise run test`: 29 passed, zero failed, zero reported skipped on macOS. Collision integration cases return without creating a collision on volumes unable to represent both names; this count does not establish that those filesystem branches ran.
- Rendered actual MainWindow states through a temporary native Avalonia harness with synthetic files and a test picker: initial, scanning, success, empty, warning, failure and expanded diagnostics. Initial/results were also checked at minimum window size.
- Searched source/UI for obsolete profile-specific components; only deliberately obsolete JSON fields remain in the compatibility test.

No new hosted CI results or Windows/Linux native results are claimed for this refactor. The UI harness and captures are temporary local artifacts, not product dependencies or a native-picker test.

Synthetic tests cover arbitrary extensions, recursion, exact bytes/hashes, deterministic fingerprints, refresh additions/modifications/removals, empty folders, portable names, Unicode canonicalization, collisions, permissions, links, nonregular files, cancellation, instability and unchanged source bytes/write times. Filesystem collision fixtures run only when the volume can represent both conflicting names; portable collision-key checks run everywhere. Unix fixtures are skipped on Windows; native Windows ACL/reparse acceptance is still required.

Native picker interaction remains a manual acceptance item. Prior macOS automation was denied by the OS. Windows/Linux execution, packaging and native visual acceptance must be checked on those platforms before claiming native support. Very large inventories are materialized in memory and rendered as a simple unvirtualized list; performance tuning is deferred. Folder choice is not persisted. No remote state, credentials, background sync or GitHub synchronization is added by this task.
