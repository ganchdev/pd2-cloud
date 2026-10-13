# PD2 Cloud — implementation plan

Prepared 2026-10-13 against the supplied MVP specification. This is a proposed implementation design and delivery backlog, not a claim that the application has been built or validated. No existing PD2 Cloud solution was found in the workspace.

## 1. Technical choices

Use C# on .NET 10 LTS, Avalonia with MVVM, an injected HTTPS client, JSON manifests and local state, and SHA-256 for application fingerprints. Pin SDK and dependency versions when scaffolding. .NET 10 is supported through November 2028. [Microsoft support documentation](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)

Publish self-contained builds for `win-x64`, `osx-arm64`, `osx-x64`, and `linux-x64`. Maintain an explicit tested OS/distribution matrix; runtime identifiers alone do not establish support. Verify the selected Avalonia version against that matrix, including directory pickers and Linux desktop environments. [Avalonia platform documentation](https://docs.avaloniaui.net/docs/supported-platforms)

Suggested solution boundaries:

```text
src/
  PD2Cloud.Core/          Models, comparison rules, planning, coordinator, interfaces
  PD2Cloud.Local/         Discovery, stable snapshots, state, backups, transactions
  PD2Cloud.GitHub/        Device authentication, repository access, Git object APIs
  PD2Cloud.Platform/      Credentials, process inspection, browser and OS integration
  PD2Cloud.Desktop/       Avalonia views, view models and composition root
tests/
  PD2Cloud.Core.Tests/
  PD2Cloud.Local.Tests/
  PD2Cloud.GitHub.Tests/
  PD2Cloud.IntegrationTests/
docs/
  architecture.md
  save-profiles.md
  recovery.md
  release-checklist.md
```

Core has no Avalonia or platform dependencies. Concrete adapters implement Core interfaces. Desktop composes them; GitHub and Local never reference Desktop. Use xUnit for meaningful safety and behavior tests, temporary save directories, an injectable HTTP handler, and an in-memory cloud repository for two-device simulations.

## 2. Decisions to encode before synchronization

### Whole-collection comparison

A collection is the complete recognized filename-to-content inventory plus compatibility-relevant profile fields. File presence participates in equality. Timestamps and descriptive application versions do not. An unrelated Git commit or changed upload timestamp does not constitute changed progress.

Calculate differences at file level for the dashboard, then make one collection-level decision. Changes to different characters on different devices still block synchronization. Equal local and cloud collections can establish a baseline even if both differ from the old baseline, provided profiles and revision lineage pass validation.

### Absence and choosing one version

The specification prohibits automatic deletion but also requires choosing a complete collection. These meet when the losing collection contains files absent from the winning collection.

Proposed rule: ordinary Sync All blocks any previously synchronized absence. The review offers restoration from the surviving side or an explicit choice of the complete local/cloud collection. A complete-version choice lists every file that will leave active storage and requires explicit confirmation. Preserve both complete collections first. Local removals become recoverable archival moves; cloud removals affect only the managed namespace in a new commit, preserving the old commit and a downloaded backup. Do not quietly retain extra active files, since that would construct a third collection.

This explicit archival behavior is a proposed interpretation requiring a product decision before enabling the relevant workflow. Until resolved, block a complete-version choice that requires removal; all other development can proceed.

### Initialization and onboarding

Onboarding, startup refresh and Refresh do not write saves or repository content. Persist settings locally and inspect the cloud. Initialize a repository only during an explicit synchronization action. If both collections are empty, report that there is nothing to synchronize and avoid an unnecessary initialization commit.

GitHub does not permit creating a reference in a completely empty repository through the Git references API. Use the Contents API for a small bootstrap file, then upload the complete first snapshot with the Git object APIs. The initial operation can therefore create a bootstrap commit and one save checkpoint; subsequent uploads create one checkpoint each. Never report the bootstrap alone as a successful save synchronization. [GitHub empty-repository guidance](https://docs.github.com/en/rest/guides/using-the-rest-api-to-interact-with-your-git-database), [reference creation restrictions](https://docs.github.com/en/rest/git/refs)

### Save-format evidence

Maintain a versioned profile policy rather than an extension wildcard. Initial confirmed PlugY families are character `.d2s`, matching personal-stash `.d2x`, and configured shared-stash `.sss` names. PlugY supports configurable stash names and distinct hardcore names; obtain those settings from user-selected configuration or advanced profile input. Treat unpaired sidecars as a validation issue rather than silently ignoring them. [PlugY documentation](https://github.com/ChaosMarc/PlugY/blob/master/PlugYInstaller/PlugY_The_Survival_Kit_-_Readme.txt), [PlugY save handling](https://github.com/ChaosMarc/PlugY/blob/master/PlugY/SavePlayerData.cpp)

Native PD2 shared stash support needs its own profile. The Cain implementation reports `pd2_shared.stash` support, making it a candidate for fixture investigation, not proof of every season's filename conventions. Confirm native softcore/hardcore variants and any additional required artifacts using real game-generated fixtures before claiming profile support. [Cain implementation](https://github.com/jackmusick/cain)

Classify `.map` and `.ma0`–`.ma3` as candidates for profile-specific sidecars; determine whether they are required, optional or regenerated through fixtures and gameplay checks. Exclude backups, logs, exports, temporary files and game configuration by default. Never upload unrecognized files merely because they share a directory.

Persist profile ID/version, stash implementation, hardcore separation, configured stash names, and user-declared PD2/PlugY compatibility information. Filename inspection cannot reliably establish season compatibility. Record whether compatibility information is detected, declared or unknown. Block unsupported/incompatible combinations; require explicit profile selection when detection is ambiguous. An empty new-device directory needs a target profile before installation.

### Game safety limits

Process detection is advisory evidence, not an exclusive lock against the game. Inspect executable names and available executable paths/command lines, including Wine/Proton. Block known or suspected game activity and collection instability. An inspection failure must not be presented as verified inactivity.

Recheck activity before snapshotting and before installing files; hold file handles with restrictive sharing where supported. Require the player to keep PD2 closed during Sync All. A process can start after a check, and portable APIs cannot guarantee otherwise. Tests must distinguish detection guarantees from best-effort protection. PD2 Cloud can serialize its own instances but cannot lock a game that does not cooperate.

## 3. Persistent and remote data

### Managed repository namespace

```text
pd2-cloud/
  bootstrap.json          Optional empty-repository initialization marker
  manifest.json           Current full collection inventory
  saves/
    <original filenames>  Unmodified binary save data
```

Choose one namespace and treat ownership markers as required. A populated repository without that namespace is eligible only after an explicit initialization confirmation. An existing ambiguous namespace is blocked. Build every new root tree from the observed head's tree, replacing only the managed subtree and preserving unrelated entries. Files absent from the desired managed inventory can be removed only after the explicit review above.

Manifest fields:

- Schema version, collection UUID and minimum reader compatibility.
- Profile ID/version and compatibility-relevant settings.
- Snapshot UUID, UTC creation time and application version.
- Complete sorted file inventory: relative original filename, role/group, byte size and SHA-256.

The manifest must not embed its own commit SHA. Resolve the manifest at an immutable commit, and obtain blob IDs from that commit's tree. Git object IDs and application SHA-256 fingerprints serve different purposes. Reject missing/extra managed saves and manifest/tree discrepancies.

Read all metadata and blobs from the same pinned revision. Traverse the managed tree without accepting truncated tree results. Bound manifest size, file count, individual file sizes and total staged size. GitHub documents a 100 MB limit for blob downloads; preflight a conservative compatible upload/download limit with an actionable oversized-stash error. [GitHub blob API](https://docs.github.com/en/rest/git/blobs)

### Local records

Keep application state and verified backup collections outside the save discovery root. Local installation staging must be on the target filesystem for reliable per-file rename operations. Exclude its reserved directory from discovery.

Use versioned JSON with durable temporary-write-and-rename replacement for each record. Couple baseline updates to the transaction journal so a crash between updates can be reconciled.

| Record | Contents |
|---|---|
| Configuration | GitHub user ID/login, numeric repository ID plus current owner/name, branch, canonical save path, profile, preferences |
| Baseline | Pairing ID, collection UUID, accepted revision, complete fingerprints/profile, last successful sync time |
| Remote cache | Last verified revision/manifest and verification time; never sufficient to authorize offline sync |
| Transaction journal | Operation UUID/type, pairing, observed head, desired inventory, candidate commit, phase, per-file progress, verified backup/staging paths |
| Backup metadata | Original filenames and hashes, local/remote provenance, relevant revisions and collection profile |

Pairing identity includes repository ID, branch, collection identity, save location and relevant profile. Switching any of these invalidates comparison. Preserve old pairing backups; do not automatically reuse its baseline for the new pairing.

Store tokens only through Windows credential facilities, macOS Keychain or a Linux Secret Service adapter. Secure-store failure clears credential persistence, retains non-secret configuration, and requests authentication when needed. No plaintext fallback. Define Disconnect as clearing local secrets and account association; offer GitHub's authorization-management page for remote revocation rather than claiming local deletion revokes the token.

## 4. Authentication and repository access

Register a GitHub App with device flow enabled, expiring user tokens, Contents read/write and Metadata read. Ship its public client ID and installation URL; do not ship a secret or private key.

Request the device code, display/copy the user code, open the supplied verification URL, and poll at GitHub's interval. Handle pending authorization, slowdown, denial, expiration and cancellation. GitHub's documented device flow does not require a client secret; refresh of device-generated user tokens also supports omitting it. [GitHub device authentication](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app), [refresh documentation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens)

Retrieve account identity and enumerate the user's app installations and their accessible repositories, following pagination. Show private repositories with user write permission and compatible app access. Exclude archived/read-only repositories. Installation guidance and browser-based repository creation require a subsequent refresh; neither browser completion nor authorization alone proves repository access.

Validate repository identity, privacy, access and branch on each sync. Stop if a repository becomes public. Explain protected-branch/ruleset rejection without bypassing protection. A missing branch in a populated repository is a configuration/setup condition, not an empty save collection.

Refresh credentials through a single serialized operation and replace the secure token pair together. Retry reads with bounded backoff respecting rate-limit headers. For uncertain writes, inspect state before retrying. Redact tokens, authorization codes, HTTP authorization headers, save bytes and sensitive response bodies from diagnostics.

## 5. Pure comparison and planning

Input: validated baseline B (if any), current stable local collection L, verified remote collection R, and pairing/profile/revision validity. Output: file differences, collection state, blocking reasons and an immutable proposed plan.

Apply validation and missing-file guards before deciding direction. Require verified cloud state for every sync. If remote lineage no longer contains the baseline, block for reconciliation; do not infer safety solely from stored hashes after a history rewrite.

| Condition after validation | Collection decision |
|---|---|
| L equals R | No transfer; establish/advance baseline after verification |
| No B, only L populated | Initial upload |
| No B, only R populated | Initial download |
| No B, both populated and different | Initial reconciliation; choose a complete side |
| L differs from B, R equals B | Upload |
| L equals B, R differs from B | Download |
| Both differ from B and L differs from R | Conflict; block every transfer |
| A file present in B is absent on either side | Explicit missing-file review |
| Cloud unverified, incompatible profile, unsafe file, unstable reads | Block with actionable reason |

Keep per-file/character status separate from the collection decision. A character may say Synced while the collection says Conflict. Cached cloud results must visibly carry the last verification time and cannot claim current Synced. Track refresh completion and successful synchronization independently.

A conflict choice is tied to the exact reviewed local fingerprints, profile and remote revision. If any changes before application, invalidate it and present the updated review. Initial reconciliation uses the same preservation and confirmation mechanics.

## 6. Snapshotting, upload and installation

### Stable local capture

Enumerate only policy-approved files. Validate names across supported platforms, including case/Unicode collisions, Windows reserved names and trailing dots/spaces. Reject traversal, absolute paths, alternate data stream names, symlinks, junction/reparse-point escapes and non-regular files. Permit selecting a directory through a picker only after resolving and validating its target.

Capture files into immutable staging while hashing. Re-enumerate and compare identities, sizes and hashes across the complete collection. Abort on additions, removals or changes; use bounded retries only while the game is closed. Upload captured bytes rather than reopening mutable game files later. Recheck the local collection before reporting success; a post-capture change remains pending progress rather than silently becoming part of the baseline.

### Upload

1. Serialize operations for the pairing; recover existing unfinished operations first.
2. Validate credentials, privacy, access, game state, profile, stable L and pinned R.
3. Produce the collection plan and stop for any unresolved blocker.
4. Persist an operation journal. Preserve both sides for explicit resolution.
5. Create blobs for changed captured bytes; reuse unchanged blob IDs.
6. Create the complete managed subtree and a root tree preserving unrelated content.
7. Create one commit whose single parent is the observed remote head. Record its SHA before updating the branch.
8. Update the reference with `force: false`.
9. Verify publication and the uploaded snapshot. Persist the accepted snapshot baseline only after success; retain any newer local changes as pending.

Non-forced updates enforce a fast-forward relationship; the API does not expose a general expected-old-head compare-and-swap parameter. A candidate parented on the observed head makes competing normal commits diverge, rejecting the stale candidate. After rejection, refresh and replan; never simply change its parent without comparing collections. [GitHub reference update semantics](https://docs.github.com/en/rest/git/refs)

After a timeout, check whether the recorded candidate is the branch head or an ancestor of it. If it was published, avoid creating a duplicate upload; inspect subsequent snapshots before choosing a baseline or proposing another action. If publication cannot be verified, retain a pending journal and report uncertainty, not success. Concurrent remote rewrites and hostile writers are outside normal fast-forward safety assumptions and must trigger conservative review.

### Download and local replacement

1. Pin the chosen cloud revision, download into staging, and verify every file's size and SHA-256.
2. Revalidate game activity, target inventory, profile, available space and any reviewed decision.
3. Create and verify a complete backup of the original local collection. For resolution, also preserve a complete verified cloud copy.
4. Persist a durable Prepared journal containing originals, targets, hashes and backup locations.
5. Before each mutation, durably record intent. Install through same-filesystem temporary files and atomic per-file replacement where supported; record completion.
6. Verify the complete active inventory against the target, then persist the baseline and successful time through the journaled finalization protocol.
7. Recheck the remote head for dashboard freshness. A newer cloud version means another refresh/plan is needed, not that the installed pinned version is corrupt.

Replacing multiple files in an existing save directory is not one atomic transaction. Preserve enough verified state to finish or roll back after each possible interruption. Do not promise that a player launching PD2 during an interrupted replacement will see a coherent collection. Surface outstanding recovery before normal dashboard actions.

Keep verified backups in MVP; do not automatically prune them. A disk-space failure aborts before mutation where possible and keeps a recoverable journal if it occurs during application. Cancellation before mutation leaves active files intact. During mutation, honor cancellation at safe boundaries and finish recovery/rollback before reporting cancellation.

## 7. Recovery protocol

Use phases such as `Prepared`, `Applying`, `InstalledVerified`, `BaselineCommitted`, and `Completed`; uploads additionally track `ObjectsCreated`, `CommitCreated`, and `PublicationUnknown/Published`. Phase transitions and per-file intents must be durable before the associated changes.

On startup, inspect journals before allowing sync or a competing instance. Verify saved original and target hashes rather than trusting phase labels. Complete a proven installation or roll back from proven backups. Finalize the baseline only for a fully verified installed/published snapshot.

If active files have been edited since interruption, do not automatically restore over them. Preserve the new bytes, block further synchronization, and offer explicit recovery using the stored versions. Detect incomplete/corrupted backups or journals and provide paths and clear recovery guidance. Never perform speculative rollback from unverified backup contents.

## 8. Delivery backlog and gates

| Milestone | Work | Exit evidence |
|---|---|---|
| 1 — Foundation | Solution/CI, desktop shell, local-only dashboard, picker, profile registry, directory validation, stable discovery/hashing, versioned settings | Characters and stash grouped correctly without GitHub; read-only refresh; tested path collisions, exclusions and unstable-file behavior |
| 2 — GitHub reads | Device auth/secure-store adapters, installation guidance, repo picker, validated pinned manifests/trees, offline cache | Real private-repo onboarding on each OS; remote listing without Git; no writes during onboarding/refresh |
| 3 — Safe synchronization | Pure planner, pairing baselines, journal/backup/install primitives, Git object uploads, first-use initialization, conservative conflicts/missing-file blocks | Verified two-device upload/download round trip; no-op creates no commit; races reject stale uploads; interrupted writes recover |
| 4 — Protection completion | Full initial/conflict/missing-file review, both-side preservation, stale-decision invalidation, platform fault cases and recovery UX | No transfer occurs before required confirmation/backups; tested failure at every mutation boundary |
| 5 — Release | Complete settings/onboarding, platform packaging/signing, docs, fixtures and gameplay matrix | Supplied acceptance scenarios pass on supported platforms; signed/distributable artifacts and clear compatibility declaration |

Safety primitives start in Milestone 1 and gate Milestone 3. Milestone 4 completes their coverage and user flows; it is not the first point where backups or recovery are introduced. Repository initialization writes are implemented with the synchronization gate, although repository eligibility can be delivered earlier.

### First implementation slice

The [delivery plan](delivery-plan.md) divides Foundation into smaller tasks and
supersedes the earlier suggestion to scaffold all five projects first. As of
2026-10-13, task 1 implements read-only folder selection/metadata inspection with
only Core, Local and Desktop. Build, formatting and 10 behavior tests passed on
macOS; test execution is wired into mise and CI. Native picker interaction and
visual acceptance remain pending because macOS automation was denied. No validated
save discovery, profiles, fingerprints, settings or cloud behavior is implemented.
See the delivery plan for verification limits and the next task.

## 9. Verification and release prerequisites

Core tests exhaust the decision table, including equal independent changes, disjoint independent changes, profile changes, sidecar-only changes, missing files and baseline invalidation.

Filesystem integration tests cover byte preservation, corrupted downloads, unsafe names/links, permissions, disk exhaustion and a crash after every journal/rename/baseline boundary. Restart tests prove recovery is idempotent and never overwrites subsequent external edits without review.

Provider tests cover pagination, expiration/slowdown, refresh rotation, missing repositories/branches, public repositories, protected branches, truncated trees, stale-head rejection and lost responses after successful publication.

Two-device tests run two independent local states against one cloud model. Supplement these with a disposable real private repository for device flow, installation permissions, initialization, concurrent upload rejection and byte-identical transfers. Include complete native PD2 and PlugY collections with separate hardcore/softcore stashes and OS-crossing filenames.

Native release validation covers secure-store persistence/unavailability, directory pickers, running game detection under actual Windows/Wine/Proton environments, package installation and opening synchronized saves in PD2. Synthetic bytes establish transport correctness but cannot establish format/game compatibility.

External prerequisites are a registered GitHub App, legitimate game-generated fixtures for each supported profile, access to the platform test environments, and signing/notarization credentials for the chosen distribution channels. Plan/foundation work can proceed without these; production GitHub and release acceptance cannot be claimed until they are supplied and validated.

Release only when the supplied two-computer journey passes with complete character/stash inventories, explicit divergence handling, recoverable interruptions and no Git installation. Keep deferred features outside this implementation.
