# Generic folder inspection

Current task-8 extension: [safe upload](safe-upload.md) adds a coordinated initial-upload/publication-recovery API with repository/branch/pinned-head binding and durable candidate records. The desktop remains read-only with Sync All disabled. Successful-sync persistence and baseline ancestry remain unimplemented; publication does not establish synchronization success. Earlier task-specific transfer limitations below describe their original delivery boundary. Live GitHub and native acceptance remain open.

Implementation reference, updated 2026-10-15. Tasks 2–7 are implemented; GitHub/cloud reads remain separate from local inspection. Transfers and successful-sync persistence are unimplemented.

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

The [shared setup journey](github-setup-ux.md) connects GitHub and chooses a repository before showing Find Your Saves. Select Folder opens the native picker; a valid fresh inspection enables Continue. The dashboard shows the selected path, file count/list and synchronization availability; Settings → Saves provides folder selection, inspection status, retry and details. The list uses relative paths and has no character/stash grouping or configuration controls.

| Status | Meaning |
|---|---|
| Scanning | An inspection is in progress; previous results are cleared |
| Scanned successfully | The complete inventory passed stability and filesystem validation, including an empty folder |
| Needs attention | Safety or stability checks block a complete valid inventory |
| Failed | Folder access, enumeration, reading or an unexpected error prevented inspection |

Incomplete scans show “File inventory incomplete”; they do not claim an empty collection. Inspection Details exposes issue paths/messages, sizes, hashes and the collection fingerprint. Cancelling or failing the folder picker retains the current selection/results. Changing the folder starts a new inspection. There is no automatic background scan or synchronization, and restart performs one fresh inspection of the persisted folder.

## Read-only stability checks

Inspection runs on a worker thread and honors cancellation throughout traversal and hashing. Each inspection takes three full recursive metadata inventories (canonical/physical paths, entry kinds, sizes and write times) surrounding two complete SHA-256 passes. Both hash lists and all inventories must match. A detectable edit, new entry, removal, read failure or directory-enumeration failure invalidates the result. Refresh always scans again; it does not cache prior hashes. Superseded scans are cancelled and their late results/errors are discarded.

Retained file reading uses read-only handles, bounded buffers and length checks. On macOS/Linux x64/arm64, nonblocking/no-follow opens and native handle type checks reject pipes and other nonregular files. Windows checks reparse/device/directory attributes and opens with restrictive read sharing. No contents are interpreted or executed.

These observations are not an atomic snapshot or a lock against external writers. Transient changes that return to identical data may escape detection. Ancestor checks do not protect against adversarial replacement between checks and opens. A file may change immediately after inspection. Future synchronization must capture immutable bytes and revalidate before any write; inspection results alone cannot authorize replacement.

## Local state boundary

`LocalSyncState` only defines a future successful-sync record: schema version, selected repository identity/branch, accepted Git commit, local root, file fingerprints and successful-sync timestamp. No instance is fabricated by inspection or comparison, no successful-sync state file is written or read. The pure comparer can consume a supplied record; it does not persist or advance it. Folder settings use a separate persistence service. Prior folder/profile choices were in memory only, so there is no existing persisted configuration to migrate. Settings decoding retains unknown obsolete and unrelated JSON fields when updating the selected folder; obsolete fields never affect inspection. Folder settings do not touch credentials; task 5 adds separate native credential storage.

`ApplicationDataPaths.LocalSyncStatePath` reserves `local-sync-state.json` beneath `Environment.SpecialFolder.LocalApplicationData/PD2Cloud`, outside the source directory. Typical locations are `%LOCALAPPDATA%/PD2Cloud` on Windows, `~/Library/Application Support/PD2Cloud` on macOS and `$XDG_DATA_HOME/PD2Cloud` (usually `~/.local/share/PD2Cloud`) on Linux, as resolved by .NET. The whole application-data directory is reserved, including future staging. Folder selection and the scanner reject source roots that contain, equal or lie within that directory, even before it exists. Prefix checks respect directory boundaries and use case-insensitive comparison on Windows/macOS; the scanner also rejects linked source ancestors. Application storage with linked ancestors or a linked settings file is rejected rather than followed.

## Verification and limitations

Recorded local verification on 2026-10-15:

- `mise run build`: passed, zero warnings/errors.
- `mise run format:check`: passed.
- `mise run test`: 29 passed, zero failed, zero reported skipped on macOS. Collision integration cases return without creating a collision on volumes unable to represent both names; this count does not establish that those filesystem branches ran.
- Rendered actual MainWindow states through a temporary native Avalonia harness with synthetic files and a test picker: initial, scanning, success, empty, warning, failure and expanded diagnostics. Initial/results were also checked at minimum window size.
- Searched source/UI for obsolete profile-specific components; only deliberately obsolete JSON fields remain in the compatibility test.

No new hosted CI results or Windows/Linux native results are claimed for this refactor. The UI harness and captures are temporary local artifacts, not product dependencies or a native-picker test.

Synthetic tests cover arbitrary extensions, recursion, exact bytes/hashes, deterministic fingerprints, refresh additions/modifications/removals, empty folders, portable names, Unicode canonicalization, collisions, permissions, links, nonregular files, cancellation, instability and unchanged source bytes/write times. Filesystem collision fixtures run only when the volume can represent both conflicting names; portable collision-key checks run everywhere. Unix fixtures are skipped on Windows; native Windows ACL/reparse acceptance is still required.

Native picker interaction remains a manual acceptance item. Prior macOS automation was denied by the OS. Windows/Linux execution, packaging and native visual acceptance must be checked on those platforms before claiming native support. Very large inventories are materialized in memory and rendered as a simple unvirtualized list; performance tuning is deferred. Folder choice is persisted separately from the baseline. No remote state, credentials, background sync or GitHub synchronization is added by this task.

## Folder settings and restart (task 3)

`ApplicationDataPaths.SettingsPath` is `settings.json` beside the reserved `local-sync-state.json`, under the .NET platform `LocalApplicationData/PD2Cloud` location. The schema is `{ "SchemaVersion": 1, "SelectedFolder": "absolute physical folder path", "SetupStarted": true, "SetupCompleted": true }`; absent setup flags mean false. Setup flags are non-secret preferences, never evidence of synchronization. No manifests, hashes, timestamps, baseline or secrets are persisted by this workflow.

Startup loads preferences once and starts a fresh inspection. It never rewrites settings merely by opening or refreshing. Selecting an allowed folder saves it even when it is currently unavailable; inspection reports that failure without forgetting the preference. Cancelled/rejected selection retains the current folder. Closing during load/inspection rejects late UI results. Persistence failure displays a separate notice and permits session-only inspection; choosing the folder again retries saving.

Missing settings use an empty preference object. Older version 0 or unversioned JSON objects retain unknown fields and upgrade to version 1 on explicit selection. Malformed JSON, wrong field types, non-object JSON and unsupported versions are left untouched. Invalid/unsafe stored paths are not inspected. The UI explains restoration failure and allows selection; damaged/future settings need manual repair or relocation before they can be saved again. This folder-settings code never touches a credential facility or synchronization state. Task 5 credentials use a separate native adapter.

Updates reread the latest document, change only owned `SchemaVersion`/`SelectedFolder`/setup flag fields, create a unique temporary file in the same app directory, flush file contents to disk and rename over the destination. An in-process semaphore serializes operations on the store. Failed writes leave the previous settings intact; normal failure cleanup removes the temporary file. A crash may leave an unused temporary file; it is never treated as settings or inspected. Atomic rename depends on the underlying filesystem. Directory metadata is not fsynced, so survival of sudden power loss is not guaranteed. There is no cross-process writer coordination or protection against adversarial path replacement. Future staging outside the reserved application directory must be added to the rejection policy before use.

Task-3 verification on macOS (2026-10-15):

- `mise run test -- -p:UsedAvaloniaProducts=`: 45 passed, zero failed (16 new test cases over task 2).
- `mise run build -- -p:UsedAvaloniaProducts=`: Release passed with zero warnings/errors.
- `mise run format:check`: passed. `git diff --check`: passed.
- Standard build/harness invocations encountered Avalonia statistics-task access denial for its application-data build log, including escalation attempts. The command-line property above skips that statistics target for local verification only; project/package settings are unchanged.
- A temporary native Avalonia harness rendered the real MainWindow with actual settings storage and a test picker. Visually inspected selected/restored results, missing restored folder, malformed settings, save failure at minimum size and rejected application-data selection at minimum size. Notices wrap and recovery buttons remain usable. Harness captures are at `/private/tmp/pd2-generic-ui/task3-*.png` and are not product/test dependencies.
- Tests cover fresh contents after restart, missing/inaccessible restored folders and recovery, malformed/future/wrong-type documents, older/unknown fields, preservation of unrelated preferences, replacement/failure cleanup, linked settings, source/storage boundaries and disposal during delayed startup.

Native Windows/Linux execution, native picker interaction, actual process termination/power-loss testing and large-folder performance remain unverified. Tests simulate restart using fresh store/view-model instances and real temporary files, rather than launching two product processes. Unix permissions/link fixtures return early on Windows; Windows ACL/reparse acceptance remains necessary.

### Build/run logging fix (2026-10-15)

The Avalonia logging error also reproduced with the user's normal `mise run run`, so the earlier command-line statistics workaround was insufficient for daily development. `mise.toml` now sets the upstream-supported `AVALONIA_TELEMETRY_OPTOUT=1` alongside the .NET telemetry opt-out. This makes the statistics task return without attempting telemetry/log writes. No dependency or application behavior changed. Direct `dotnet` calls outside mise need the same environment variable.

Verified without extra MSBuild flags: `mise run build` passed with zero warnings/errors, `mise run test` passed all 45 tests, and `mise run format:check` passed. `mise run run` built and remained running without the logging error; the verification process was then stopped with Ctrl-C. No file permissions were changed. This supersedes the need for `-p:UsedAvaloniaProducts=` in the earlier validation notes.

## Comparison boundary (task 4)

`CollectionInventory.Fingerprint` in Core now owns the existing schema-1 encoding. `CollectionScanner.Fingerprint` delegates to it for compatibility; inspection output and desktop behavior are unchanged. The [pure comparison contract](collection-comparison.md) requires complete valid inventories and explicit remote verification; it never uses incomplete inspection as an empty collection or fabricates a baseline.

Task-4 macOS verification: standard `mise run test` passed all 97 cases, `mise run build` passed with zero warnings/errors, and `mise run format:check` passed. The 52 added cases are pure decision-table/validation tests; no UI changes or new native UI acceptance are claimed. Existing filesystem, platform and settings limitations above still apply.

## Independent GitHub connection (task 5)

The desktop now also offers GitHub App connection and private repository selection. The scanner, folder settings, manifest/fingerprint rules and pure comparison are unchanged. Local startup inspection and Retry Inspection do not call GitHub or touch credentials. Dashboard Refresh independently checks GitHub account/repository metadata and then runs task-6 pinned cloud verification. Connection startup restores native credentials and reads account/repository metadata independently; a stalled or failed GitHub operation leaves folder inspection usable. Task 6 now adds read-only remote inventories and advisory comparison through the separate Remote bridge; local scanning remains unchanged. No repository/source writes, baseline persistence or background sync exist. See [cloud reads](cloud-reads.md). See [GitHub connection](github-connection.md) for setup, tests and remaining live/native acceptance gates.

## Local recovery preparation (task 7)

[Task 7](backups-recovery.md) reuses the scanner and regular-file reader for a separate local-only capture/backup API. Public inspection behavior and inclusion rules are unchanged. An internal owned-snapshot scan bypasses only the application-storage overlap rejection so retained app-owned copies can be verified; all path/link/stability checks remain. The public scanner still rejects application storage, and desktop startup/Refresh never invoke preparation. Preparation limits block whole operations without narrowing generic inspection. The source is never replaced, no baseline is written and Sync All stays disabled.
