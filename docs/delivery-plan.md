# PD2 Cloud — deliverable work plan

Proposed on 2026-10-13. Setup is complete; task 1 is implemented with native acceptance pending.
This breaks the [implementation plan](implementation-plan.md) into reviewable
deliverables. Product decisions below remain open until resolved.

## Delivery approach

Each chunk should be independently reviewable, buildable and demonstrable. Use one
PR per chunk where practical; split a chunk if its safety or platform work becomes
too large. Add projects when their first implementation needs them rather than
scaffolding every future adapter up front.

Every chunk must pass build, formatting and relevant behavior tests. Add a test
task and CI test execution with the first behavior tests. Record a short demo and
known limitations. Native checks are required for platform-dependent behavior;
passing CI alone does not establish platform support.

## Start here: local-only collection inspection

### 1. Select and inspect a save folder

**Demo:** Open the app, select a folder, see its path and a read-only file listing,
then refresh after changing files outside the app.

- Replace hello world with a small desktop shell, folder picker, empty/error
  states and Refresh action.
- Introduce Core and Local only as needed to keep filesystem work out of views.
- Validate the selected directory and surface inaccessible paths and unsafe links.
- Show filenames and sizes as an inspection listing. Do not label files as a
  complete supported collection before a profile has validated them.
- Keep filesystem reads off the UI thread; support cancellation and reject stale
  results when the selected folder changes.

**Done when:** A populated folder, an empty folder, cancellation and an unreadable
folder have clear outcomes. Selection and refresh leave source files untouched.
The native directory picker works on the development machine.

**Progress (2026-10-13):** Implemented the desktop listing, native picker adapter,
Refresh, empty/error/cancel states and link warnings. Core contains only inspection
models/interface; Local reads metadata on a worker thread. Superseded scans are
cancelled and late successes/errors are discarded. Tests cover byte/write-time
preservation, direct-child listing, external changes, missing and inaccessible
folders, linked roots/ancestors, dangling links, picker cancellation/failure,
overlapping scans and closing during a scan. Test execution is wired into mise and
all three CI jobs. No profile, hashing, persistence or sync behavior was added.

**Verification:** Release build passed with zero warnings/errors; formatting check
and 10 behavior tests passed on macOS. Desktop process launched without reported
startup errors. Native picker interaction and visual UI verification remain
unverified: macOS denied Apple Events to System Events (`-1743`). Windows/Linux
native acceptance and Windows ACL/junction checks remain pending. Unix link and
permission fixtures are explicitly skipped on Windows. CI was updated locally but
has not run for these uncommitted changes.

**Limitations:** Direct children only; sizes are metadata and may change during a
scan. The app rejects linked selected paths rather than resolving them. Link checks
are metadata checks, not protection against adversarial path replacement. Very
large-folder rendering and durable selection remain later work. Task 1's native
picker acceptance gate is still open; task 2 has not started.

### 2. Discover and fingerprint a profiled collection

**Demo:** Choose a supported profile and see recognized characters, personal and
shared stashes, excluded files and validation issues. Refresh reflects save edits.

- Define versioned profile, file-entry, collection and validation models.
- Implement explicit filename policies, character/sidecar association and separate
  hardcore/softcore stash configuration. Require profile selection when ambiguous.
- Calculate sizes and SHA-256 fingerprints with collection stability checks.
- Reject unsafe names, case/Unicode collisions and link escapes. Report unpaired
  sidecars and unsupported or uncertain inventory instead of silently dropping it.
- Group recognized entries in the dashboard; distinguish inspection results from
  a validated stable collection.

**Done when:** Tests cover inclusions/exclusions, custom stash names, pairing,
  hardcore separation, byte hashes, unsafe paths and files changing during reads.
  Read-only scans preserve source bytes. Supported game inventory is documented
  with fixture evidence; synthetic tests alone do not establish game compatibility.

**Dependency:** Real save samples/configuration are needed before claiming a native
PD2 or PlugY profile is complete. Model and synthetic policy work can proceed first.

### 3. Resume the local dashboard reliably

**Demo:** Restart the app and reopen the configured folder/profile. Missing or
changed folders produce an actionable state. Repeated refresh stays responsive.

- Persist versioned non-secret folder/profile settings outside the save directory,
  using temporary-write-and-rename replacement.
- Handle malformed settings, unavailable folders and changed profile configuration.
- Harden cancellation, overlapping refresh and large-folder behavior.
- Document the local workflow and current profile limitations.

**Done when:** Restart and failure cases are tested; startup performs inspection
only. The local workflow can be used without GitHub or a token.

**Checkpoint:** Review the local dashboard and inventory evidence before cloud work.

## Subsequent chunks

| Chunk | Deliverable/demo | Completion evidence and dependencies |
|---|---|---|
| 4. Collection comparison | A pure comparison engine produces direction, conflict and blocking reasons from baseline/local/cloud inputs. | Decision-table tests include disjoint edits, equal independent edits, missing files, profile changes and invalid pairings. No transfer execution. Depends on collection models. |
| 5. GitHub connection | Connect an account, store credentials securely, select an accessible private repository and disconnect. | Device-flow cancellation/expiration, token refresh and secure-store failures covered. No plaintext fallback. Requires a registered GitHub App and native secure-store checks on supported OSes. |
| 6. Read the cloud collection | Refresh shows local/cloud differences from a verified pinned revision, plus clear offline/error states. | Pagination, privacy/access checks, manifest/tree consistency, size limits and corrupt content covered. Onboarding and refresh never write repository content. Depends on 2, 4 and 5. |
| 7. Backups and recovery | A sandbox demonstration backs up and installs a test collection, then recovers after interruption. | Immutable staged captures, verified backups, durable journals and instance serialization precede writes. Fault tests at every mutation boundary prove recovery and protect later external edits. Introduce game-activity checks with conservative handling of uncertainty. Depends on 2 and 3. |
| 8. First safe upload | Explicit Sync All uploads a stable local collection to an empty cloud collection. | Private-repo eligibility and initialization enforced; unrelated content preserved; non-forced publication, races and uncertain responses handled. Verified baseline only after publication. No-op creates no checkpoint. Depends on 4, 6 and 7. |
| 9. First safe download | A second device installs a verified cloud collection into an empty local target; later one-sided changes travel both ways. | Downloads are hash-verified before installation, backups/recovery gate replacement, and game checks gate capture/install. Two-device byte-identical round trip and interrupted-install tests pass. Depends on 8. |
| 10. Conflict and missing-file review | Two devices diverge; the app blocks sync and offers an explicit complete-version review. | Preserve both collections before applying a choice; invalidate stale reviews; never merge or propagate deletions automatically. Requires the archival/removal product decision. Depends on 9. |
| 11. Distribution and acceptance | Installable builds complete the documented two-computer journey on the supported platform matrix. | Native picker, secure storage, process inspection, packaging and gameplay checks pass with real fixtures. Documentation and recovery guidance complete. Signing/notarization depends on distribution decisions and credentials. |

Chunks 7 and 10 need small internal steps if their fault coverage or review UI grows.
Do not enable live replacement until chunk 7's recovery gate passes. Until chunk 10,
initial reconciliation, divergence and previously synchronized missing files remain
blocked with explanations.

## Decisions and external inputs

- **Save inventory:** Obtain representative native PD2 and/or PlugY saves and their
  configuration, including softcore/hardcore stashes. Start with the profile the
  user actually plays; do not guess support for other variants.
- **Complete-version choice:** Agree on recoverable archival of files absent from
  the winning collection before implementing removal in chunk 10.
- **GitHub App:** Register device-flow access and selected private-repository
  permissions before chunk 5's live acceptance.
- **Platform support:** Identify available Windows, macOS and Linux native test
  environments before making support claims. Release credentials are needed later.

Next: complete task 1 native picker/UI acceptance, then review task 2 inventory
evidence and profile requirements before implementing validated discovery.
