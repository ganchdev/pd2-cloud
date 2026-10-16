# PD2 Cloud implementation plan

Revised 2026-10-15. The generic folder synchronization specification supersedes the earlier format-aware discovery architecture.

PD2 Cloud is a generic folder synchronization engine. All regular files within the selected directory are treated as opaque binary data. It will synchronize a complete recursive file inventory as one logical snapshot through a private GitHub repository. There is no game-format parser or game configuration model.

The 2026-10-16 [GitHub setup UX](github-setup-ux.md) adds shared onboarding/Settings navigation, browser/clipboard actions, persisted setup progress and a dashboard. Repository contents and synchronization remain unimplemented.

## Current boundaries

Use C#/.NET 10 and Avalonia MVVM. Core holds generic models, pure collection comparison and the scanner boundary, Local implements filesystem inspection, and Desktop composes them with a native picker. Local never depends on GitHub or Desktop. GitHub implements authentication and repository metadata without depending on Core/Local; Platform implements native credential storage. Desktop composes independent inspection and connection view models. The [inspection specification](folder-inspection.md) defines schema 1, canonical paths, hashing and safety guarantees.

Task 2 implements local recursive inspection and a simple file dashboard. Inspection has no cloud state and never establishes a baseline. Task 3 persists the selected folder outside the source tree and restores it with a fresh inspection. See the [delivery plan](delivery-plan.md).

## Implemented as of 2026-10-15

| Area | Built behavior |
|---|---|
| Desktop workflow | Initial “Find your saves” screen, Select Folder, selected path, file count, last inspection time, Refresh and Change Folder |
| Discovery | Recursive inclusion of every eligible regular file, with no extension filters or content parsing |
| Manifest | Schema 1, canonical relative paths, sizes, SHA-256 file hashes and a deterministic whole-collection fingerprint |
| Safety | Portable path validation, case/Unicode collision detection, link rejection, permission/read diagnostics and two-pass stability checks |
| Responsiveness | Worker-thread inspection, cancellation and rejection of superseded results/errors |
| Diagnostics | Collapsed Inspection Details with issues, excluded paths, file sizes/hashes and collection fingerprint |
| Local preferences | Versioned folder settings, safe replacement, startup inspection, persistent failure notices and application-storage source rejection |
| Comparison | Pure complete-inventory decision table, missing-file review and typed safety blocks; no desktop integration |
| GitHub connection | Device flow, serialized refresh, native-only credential storage, private repository metadata selection and disconnect; live acceptance pending |
| Future baseline boundary | `LocalSyncState` is a supplied comparison input and reserved path only; no baseline persistence or remote behavior |

The former profile registry, format-specific inventory policies, sidecar associations, stash questions/configuration, game-version fields and their tests have been removed. The old metadata-only inspector was also removed; the desktop now uses one generic scanner interface. The read-only fingerprint reader and native folder picker were reused.

Verification on macOS passed: 29 test cases, formatting, and a Release build with zero warnings/errors. The real window was rendered and visually inspected through a temporary native harness with a test picker. Hosted CI results were not available during local verification of this refactor. CI is configured for Windows, macOS and Linux; native picker and Windows/Linux acceptance remain open.

Tasks 2–5 are implemented; task 5 supports a developer-supplied public client ID and requires live registration/authentication/repository acceptance and remaining native platform checks. Task 6 (verified cloud reads) is next only with separate direction. Task-4 verification passed 97 tests, standard Release build and formatting on macOS. Task-5 verification passed 127 tests (30 new), a standard Release build with zero warnings/errors and formatting on macOS. Task-5 contract, setup and acceptance are in [GitHub connection](github-connection.md). Folder settings remain separate from successful-sync state and credentials. No transfers, backups/recovery or baseline persistence exist.

## Comparison and future synchronization

Task 4 implements only step 4 and its advisory decisions below, through `CollectionComparer.Compare` in Core. See the [comparison contract](collection-comparison.md) for API inputs, validation precedence and limitations. It accepts an optional supplied successful baseline; it never creates, loads, saves or advances one. Callers must supply verified pinned remote state and baseline history evidence. The desktop supports local inspection and separate GitHub connection; it does not invoke comparison or cloud inventory reads.

Local startup and Retry Inspection inspect only; separate connection startup restores credentials and reads account/repository metadata. Dashboard Refresh runs those local/metadata checks independently; it cannot inspect cloud file changes. An explicit Sync All action is the only transfer trigger; there is no automatic background sync. The future flow is:

1. Inspect/capture the local folder and create a deterministic manifest.
2. Read the last successfully synchronized device baseline.
3. Retrieve and verify one pinned immutable remote GitHub revision.
4. Compare local, baseline and remote complete inventories.
5. Determine upload, download, no-op, conflict or missing-file review.
6. Synchronize the entire collection only after safety and recovery gates pass.

| Validated condition | Decision |
|---|---|
| Local equals remote, with no missing baseline paths | No transfer; any future baseline advancement requires separate verification |
| No baseline, only local populated | Initial upload |
| No baseline, only remote populated | Initial download |
| No baseline, both populated and different | Ask for explicit reconciliation |
| Only local differs from baseline | Upload |
| Only remote differs from baseline | Download |
| Both differ and local differs from remote | Stop and ask; no automatic merge |
| Previously synchronized file absent on either side | Explicit review; no automatic deletion propagation |
| Unstable, unsafe, unreadable or unverified state | Block transfer |

Different-file edits on different devices still conflict at collection level. Equality includes canonical paths, file hashes and validated sizes, not timestamps, roots or input ordering. Missing-file review takes precedence over equality and direction. Baseline identity includes repository ID, branch and selected local root. A changed pairing or remote history rewrite invalidates assumptions. Offline cache cannot authorize synchronization. Explicit conflict choices must be invalidated when either reviewed side changes.

Choosing a complete side may remove active files absent from that side. The recoverable archival/removal policy still needs an explicit product decision before enabling that workflow; block it until decided. Preserve both complete snapshots before resolution. Never merge automatically or force a remote branch update.

## GitHub connection and future remote storage

Keep HTTPS REST access independent of directory scanning; no Git CLI requirement or hosted backend. Use a private repository and an explicitly owned namespace containing a versioned manifest and an opaque recursive files subtree. Preserve unrelated repository entries. Validate repository identity, privacy, branch and write access on each operation. Never treat a missing branch as an empty collection.

Read metadata, trees and blobs from one pinned commit. Validate manifest/tree consistency, canonical paths, hashes, size/count limits and nontruncated tree responses before accepting a remote inventory. Bound resource usage and preserve original bytes. Remote path serialization follows the same portable canonical rules as local inspection.

Task 5 implements GitHub App device polling, cancellation, expiry, slowdown and serialized device-token refresh without a client secret. Native Keychain/Credential Manager/Secret Service adapters have no plaintext fallback. Private repository selection persists ID/name/advertised default branch inside the native credential envelope after fresh permission/privacy checks. Disconnect deletes local credentials/selection; remote revocation is separate. Configuration uses the public `PD2CLOUD_GITHUB_CLIENT_ID`; mise loads it from optional ignored `.local.env`, with an empty `.local.env.example` template and no personal default in tracked configuration; live registration/installation/authentication acceptance remains unverified. Never ship an app secret/private key. See [GitHub connection](github-connection.md) for platform requirements, tested boundaries and external acceptance gates. The macOS adapter passed a disposable round trip; signed desktop/denied/locked-store and Windows/Linux checks remain open. This task does not verify branch contents, read inventories or write repository contents. Future operations must revalidate all access/identity/branch assumptions.

Initialize repository contents only during explicit synchronization, with confirmation before adopting an unrelated populated repository. Empty-repository bootstrap is not a completed save upload. Upload immutable captured bytes, create a commit parented on the observed head and publish with `force: false`. A competing update requires refreshing and replanning, not reparenting blindly. Record candidate commits before publication; after lost responses inspect whether publication succeeded before retrying.

## Future local safety and recovery

Store settings, successful-sync state and backups in platform application data, outside the selected root. Future staging must be outside the inspected tree while allowing same-filesystem installation. Reject source configurations containing app-owned state/staging. There is no reserved filename exclusion in generic discovery.

Use versioned durable temporary-write-and-rename records. Keep configuration, successful baseline, remote cache, backup metadata and operation journals separate. Inspection manifests are observations, never successful-sync records.

Before any local replacement: serialize app operations, verify an immutable staged snapshot, preserve and verify the complete original inventory, and durably record the operation and per-file intent. Use same-filesystem per-file atomic replacement where available. A multi-file replacement is not atomic. Verify the final inventory before committing the baseline, and reconcile journal/baseline updates after restart.

Recovery must be idempotent at each mutation boundary. Never overwrite external edits made after an interruption without review. Corrupted journals/backups require clear blocking recovery guidance; no speculative rollback. Preserve backups in the MVP, with no automatic pruning. Honor cancellation at safe transaction boundaries. Portable inspection does not provide a lock against external writers; future synchronization needs immutable capture and revalidation before writes.

## Verification and release gates

CI is configured to build, format-check and run behavior tests on Windows, macOS and Linux; the current refactor has been verified locally on macOS only. Tests use arbitrary synthetic binary fixtures. Native picker, credential storage, filesystem permissions/reparse behavior, UI and packaging require platform acceptance; CI alone does not establish all native behavior.

Future transfer tests must include two-device round trips, disjoint edits/conflicts, missing files, unrelated repository content, concurrent publication, lost responses and interruption at every journal/rename/baseline boundary. Live acceptance requires a disposable private repository and registered GitHub App. Signing/notarization and tested platform environments are later release dependencies.
