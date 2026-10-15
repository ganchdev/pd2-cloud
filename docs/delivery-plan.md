# PD2 Cloud delivery plan

Revised 2026-10-15 for generic folder synchronization. Earlier game-format inventory and gameplay evidence gates no longer apply. Deliver small reviewable chunks; build, formatting and meaningful behavior tests gate each chunk. Native behavior requires platform checks.

## Status at a glance

| Task | Implementation status | Remaining acceptance/work |
|---|---|---|
| Setup | Complete | Cross-platform workflow remains configured; no new hosted run for the current refactor |
| 1. Folder selection and inspection | Implemented; listing replaced by task 2 | Native picker interaction and platform acceptance |
| 2. Generic recursive discovery | Implemented | Native Windows/Linux checks and large-folder performance remain unverified |
| 3. Persistent local dashboard | Implemented | Native Windows/Linux acceptance; see limitations below |
| 4–11. Comparison, cloud, recovery and distribution | Not started | See subsequent chunks below |

The next planned implementation task is **task 4**. Native acceptance can be completed separately; obsolete game-profile evidence does not block further generic-folder work.

## Current tasks

### 1. Select and inspect a folder

Implemented the desktop shell, native folder picker adapter, Refresh, empty/error states, cancellation and stale-result rejection. Task 2 replaces the direct-child metadata listing with a recursive file inventory.

Native picker interaction remains an acceptance item: earlier macOS UI automation was denied (`-1743`). Use temporary physical populated/empty folders to verify selection, cancellation, changing folders, external edits followed by Refresh, inaccessible paths and link errors. Windows/Linux native acceptance remains pending.

### 2. Generic recursive discovery and fingerprinting — implemented

Reimplemented according to the superseding specification:

- Include all regular files recursively; preserve bytes and canonical relative paths.
- Generate deterministic file entries and a collection SHA-256 fingerprint.
- Reuse read-only hashing and two complete passes with inventory/stability checks.
- Block unsafe paths, collisions, links, unreadable entries and unstable collections.
- Show folder, count, inspection time/status, files and Refresh/Change Folder; put diagnostics in a collapsed expander.
- Remove the obsolete format-specific subsystem, dialogs, configuration, models and tests.
- Define only a future `LocalSyncState` model and application-data path boundary. No baseline is written by inspection.

Synthetic tests cover recursive arbitrary-file inventories, hashes, cross-root equality, additions/modifications/removals, portable paths, collisions, permissions, links, nonregular files, cancellation, instability, source preservation and view-model transitions. See [folder inspection](folder-inspection.md) for exact rules and limitations.

**Verification (2026-10-15):** `mise run build` passed with zero warnings/errors; `mise run format:check` passed; `mise run test` passed all 29 test cases on macOS. Filesystem collision fixtures conditionally exercise actual collisions only on volumes that can represent both names. A temporary native Avalonia harness rendered the real MainWindow with synthetic files and a test picker: initial, scanning, results, empty, warning, failure and expanded diagnostics were visually inspected, including initial/results at minimum window size. Only Inspection Details remains as an expander; there is no technical configuration form. Failed/incomplete scans show “File inventory incomplete” instead of a misleading zero count. Successful inspection is separate from cloud synchronization, which is not implemented. Captures are local review artifacts, not test infrastructure or product code.

Native Windows/Linux checks and native picker interaction remain platform acceptance limitations rather than save-format gates. The harness does not establish native picker or pointer/keyboard acceptance. Avalonia build services required access outside the filesystem sandbox to their application-data log. Inspection is observational rather than an atomic snapshot; large-folder UI performance remains unverified.

### 3. Resume the local dashboard reliably — implemented

Versioned `settings.json` now stores only the folder preference under platform local application data/PD2Cloud. Opening the window restores that folder and performs a fresh read-only inspection. Missing/inaccessible folders retain the selection and offer Refresh/Change Folder. Settings notices remain separate from inspection status, so save failure does not prevent inspection.

Missing settings start empty. Version 0 or missing-version objects are accepted and upgraded on the next selection, preserving unknown fields. Malformed or future-version documents remain untouched and show a notice; select a folder for the current session, then repair/move the damaged settings file to enable future saving. No credentials are read, written or removed. Settings never create or advance `LocalSyncState`.

Replacement uses a unique same-directory temporary file, flushes its contents to disk, closes it and renames over the destination. Failed writes preserve the old file and clean up temporary files where possible. Source roots that contain application storage, equal it or lie within it are rejected before persistence and by the scanner, including the reserved future state/staging subtree before it exists. No filename exclusions or format-specific settings were added.

**Verification:** Standard `mise run test` passed all 45 tests, `mise run build` passed with zero warnings/errors, and formatting and diff checks passed. Actual MainWindow restart/error states were rendered and visually inspected with a temporary native harness. Avalonia’s supported telemetry opt-out is configured in `mise.toml`; `mise run run` also starts without extra MSBuild flags. **Limitations:** See [folder inspection](folder-inspection.md) for task-3 test/build/UI results and durability/platform limits. No GitHub access or background synchronization was implemented.

## Subsequent chunks

| Chunk | Deliverable and gate |
|---|---|
| 4. Collection comparison | Pure baseline/local/remote decision table; whole-collection conflicts and missing-file review, no transfer |
| 5. GitHub connection | Device flow, native secure storage, private repository selection/disconnect; registered app and native tests required |
| 6. Cloud reads | Verified pinned manifests/trees/blobs, offline/error states; no repository writes during onboarding or Refresh |
| 7. Backups and recovery | Immutable capture, verified backups, durable journals and serialized operations; interruption tests before enabling source writes |
| 8. First safe upload | Explicit Sync All, private-repo checks, managed namespace, unrelated-content preservation and non-forced publication |
| 9. First safe download | Verified install into an empty target, then one-sided changes both ways; byte-identical two-device and recovery tests |
| 10. Conflict/missing-file review | Preserve both snapshots, explicit complete-side choice and stale-review invalidation; archival/removal decision required |
| 11. Distribution and acceptance | Native UI/picker, credentials, filesystem behavior, packaging and complete two-computer acceptance on the tested platform matrix |

No automatic background synchronization, automatic merging or automatic deletion propagation. GitHub functionality is not implemented as part of task 2. No real game fixtures are required for generic discovery.
