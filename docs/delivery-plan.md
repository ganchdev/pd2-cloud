# PD2 Cloud delivery plan

Revised 2026-10-16 for generic folder synchronization. Earlier game-format inventory and gameplay evidence gates no longer apply. Deliver small reviewable chunks; build, formatting and meaningful behavior tests gate each chunk. Native behavior requires platform checks.

## Status at a glance

| Task | Implementation status | Remaining acceptance/work |
|---|---|---|
| Setup | Complete | Cross-platform workflow remains configured; no new hosted run for the current refactor |
| 1. Folder selection and inspection | Implemented; listing replaced by task 2 | Native picker interaction and platform acceptance |
| 2. Generic recursive discovery | Implemented | Native Windows/Linux checks and large-folder performance remain unverified |
| 3. Persistent local dashboard | Implemented | Native Windows/Linux acceptance; see limitations below |
| 4. Collection comparison | Implemented | Pure Core API with task-6 advisory desktop integration; baseline persistence and transfer gates remain future work |
| 5. GitHub connection | Implemented; live acceptance pending | Verify configured app registration, live device/token/repository checks and remaining native platform gates |
| 6. Cloud reads | Implemented; live acceptance pending | Pinned verification, classification and advisory desktop comparison; see [cloud reads](cloud-reads.md) |
| 7. Backups and recovery | Implemented as a local preparation/assessment API | No desktop invocation or source replacement; see [backups and recovery](backups-recovery.md) |
| 8. First safe upload | Publication API and successful-sync persistence implemented; desktop gate remains open | Baseline ancestry and explicit desktop recovery/review integration; [contract](safe-upload.md) |
| 9–11. Download, review and distribution | Not started | See subsequent chunks below |

Task 7 is implemented within its local-only safety boundary. Task 8 now has a bounded initial-upload engine with durable publication recovery and successful-sync persistence; **Sync All remains disabled** until the remaining baseline ancestry and desktop gates pass. No end-to-end task-8 completion is claimed. Task 5 live acceptance gates are tracked in [GitHub connection](github-connection.md). Native acceptance can be completed separately; obsolete game-profile evidence does not block further generic-folder work.

## Implementation history

Tasks 1–5 below record their original delivery boundaries and validation counts. Task 6 and the status table above describe current cloud-read/comparison behavior.

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

### 4. Collection comparison — implemented

Core now compares complete inventories without filesystem or GitHub access. It returns no transfer, initial upload/download, one-sided upload/download, whole-collection conflict, missing-file review or typed blocking reasons. Missing baseline files take precedence over equality, and different-file edits conflict. Invalid inventories, mismatched pairing and unverified/rewritten history block decisions. Unavailable remote state is never empty. See the [comparison contract](collection-comparison.md) for the decision order and caller obligations.

**Verification (2026-10-15):** 97 tests passed on macOS (52 added comparison cases); standard `mise run test`, `mise run build` and `mise run format:check` passed, with zero build warnings/errors. No UI changes were needed. Comparison results are advisory only: no remote verifier, transfer, conflict-resolution UI, deletion policy or baseline persistence exists. No hosted CI or Windows/Linux results are claimed.

### 5. GitHub connection — implemented; live acceptance pending

Added independent GitHub/platform projects and a connection panel. Device flow handles cancellation, expiry, denial and cumulative slowdown. Token rotation and native credential mutations are serialized; private repository/default-branch metadata is revalidated before secure persistence. Disconnect removes local credentials/selection without network access. Native storage has no plaintext fallback. Local Refresh remains inspection only; onboarding/startup/repository reload do not read or write repository contents, create a baseline or synchronize.

Development runs load `PD2CLOUD_GITHUB_CLIENT_ID` from optional ignored `.local.env`; `.local.env.example` supplies an empty template and tracked configuration contains no personal Client ID. The existing local value was preserved when moved out of `mise.toml`. Verify its app registration enables device flow, Contents read/write and Metadata read and install it on a disposable private repository. See [setup, implementation contract and precise acceptance gates](github-connection.md). Standard `mise run test` passed 127 cases (30 new), `mise run build` passed with zero warnings/errors and `mise run format:check` passed on macOS. Live registration/authorization/rotation/repository checks, Windows/Linux native storage and signed-desktop native UI/credential acceptance remain open. A disposable production-adapter Keychain round trip passed on this Mac; actual MainWindow connection states were rendered and inspected with scripted responses, including minimum size. This does not establish live authentication or native Windows/Linux behavior.

## GitHub setup UX follow-up (2026-10-16)

Shared onboarding and Settings screens replace the single connection panel. Added native browser/clipboard actions, repository creation/access guidance, confirmation before disconnect, persisted setup start/completion and dashboard routing. See the [UX contract](github-setup-ux.md) for exact behavior, validation and remaining boundaries. The follow-up originally checked metadata only. Task 6 now adds read-only cloud verification and advisory comparison; no transfers exist.

## Verified cloud reads (task 6, 2026-10-16)

Implemented read-only pinned Git object and manifest verification, empty/managed/unrelated/incompatible classification, explicit unavailable states and advisory desktop comparison. Checks run after selection/restoration and dashboard Refresh. Complete tree hashes and managed blob bytes are verified, resource limits fail closed, changing heads/access reject the observation, and account/selection changes cancel stale results. Local inspection remains independent. See [cloud reads](cloud-reads.md) for schema, limits and trust boundaries. No successful baseline, ancestry lookup, adoption, transfers or source writes are implemented. All 208 tests, standard Release build (zero warnings/errors), formatting and diff checks pass on macOS. Actual MainWindow cloud states were rendered/inspected with synthetic HTTP at normal/minimum sizes. Live GitHub and native/cross-platform acceptance remain open.

## Backups and recovery (task 7, 2026-10-16)

Implemented a local-only preparation API with independently copied capture/backup bytes, complete revalidation, separate immutable inventory metadata, append-only chained progress journals, file-copy intents and process/storage serialization. Recovery checks evidence without writing source files, retains partial/corrupt data and blocks later edits or unresolved preparations. Tests interrupt every observed persistence/copy boundary and use abrupt subprocess exit/kill for recovery and OS lease evidence. No desktop invocation, transfers, source replacement, baseline persistence or cleanup was enabled. See [the task-7 contract](backups-recovery.md) for limits, durability and later write gates.

Task-7 macOS validation: all **260 tests** pass (52 new cases, including abrupt subprocess exits and process-death lease release), standard Release build passes with zero warnings/errors, and formatting/diff checks pass. Native Windows/Linux, live GitHub and hosted CI acceptance remain open.

## Subsequent chunks

| Chunk | Deliverable and gate |
|---|---|
| 5. GitHub connection | Implemented; registered app/live checks and remaining native acceptance required (see above) |
| 6. Cloud reads | Implemented: pinned manifests/trees/blobs, classifications, bounded offline/error states and advisory comparison. No source/repository writes; live acceptance pending |
| 7. Backups and recovery | Implemented: immutable capture, independently verified complete backups, strict durable journals, serialized preparation/assessment and interruption/subprocess tests. Source writes remain disabled |
| 8. First safe upload | Publication API and successful-sync persistence implemented: pinned initial managed-empty uploads, coordinated capture revalidation, retained candidate/receipt evidence and non-forced publication. Explicit desktop Sync All, baseline ancestry and live acceptance remain gated; see [contract](safe-upload.md) |
| 9. First safe download | Verified install into an empty target, then one-sided changes both ways; byte-identical two-device and recovery tests |
| 10. Conflict/missing-file review | Preserve both snapshots, explicit complete-side choice and stale-review invalidation; archival/removal decision required |
| 11. Distribution and acceptance | Native UI/picker, credentials, filesystem behavior, packaging and complete two-computer acceptance on the tested platform matrix |

No automatic background synchronization, automatic merging or automatic deletion propagation. GitHub functionality is not implemented as part of task 2. No real game fixtures are required for generic discovery.

## Task-8 publication engine (2026-10-16)

See [safe upload](safe-upload.md) for the exact supported initial-upload boundary, durable record schema and recovery outcomes. Successful-sync persistence now stores the pinned remote revision, verified collection fingerprint and local/remote pairing. No source writes, downloads, implicit initialization/adoption, merge/deletion or background sync were added. Published is remote publication evidence only; no baseline is persisted or synchronization success claimed.

Task-8 engine validation on macOS: **304 tests** pass (44 new upload cases), standard Release build passes with zero warnings/errors, formatting and diff checks pass. No live writes, Windows/Linux execution, hosted CI or new desktop rendering is claimed.
