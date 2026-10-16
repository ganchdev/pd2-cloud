# Repository initialization/adoption (bootstrap)

Explicit, user-confirmed repository bootstrap for the reviewed initial upload, 2026-10-16. This extends [safe upload](safe-upload.md). A freshly created private repository is *unborn* (no commits, no default-branch reference) and is classified `EmptyRepository`; a repository with files is classified `Unrelated`. Neither can satisfy the ordinary reviewed-upload gate, so without an explicit bootstrap the reviewed initial upload is unreachable. This document defines the three bounded bootstrap cases, their durable evidence and their recovery.

Bootstrap never happens implicitly. Selection, startup, Refresh and Disconnect never write. The only trigger is the explicit review/confirmation journey in the desktop. A bootstrap is not a successful synchronization; a published bootstrap records remote publication only.

## Supported cases

`CollectionUploader.UploadAsync(UploadRequest)` accepts the ordinary initial upload plus three explicit bootstrap directions, selected by the reviewed `RepositoryBootstrapCase`. The request still requires the operation ID, canonical source root, reviewed collection fingerprint, numeric repository identity/full name/default branch and the reviewed pinned commit (empty for the unborn case). A task-7 `PreparedOperation` alone still cannot trigger publication.

| Case | Reviewed classification | Publication |
|---|---|---|
| UnbornRepository | `EmptyRepository` with no commit and no reference | The manifest is written with the Contents API (which creates the first commit and the default branch), then one child commit adds the complete `.pd2cloud/manifest.json` + `.pd2cloud/files/` and the branch is updated with a non-forced PATCH |
| EmptyTree | `EmptyRepository` at a real pinned commit with an empty tree | One child commit adding `.pd2cloud/`, non-forced PATCH of the existing branch |
| Unrelated | `Unrelated` (complete pinned tree with entries and no reserved namespace) | After an explicit adoption acknowledgement, one child commit adding `.pd2cloud/`, preserving every unrelated root entry/subtree/mode/id verbatim; non-forced PATCH |

The local inventory must be populated (`Files.Count > 0`). `Incompatible` managed data, populated managed collections, mismatched reviewed classifications, an existing `local-sync-state.json` baseline and any retained recovery evidence stay blocked. A second upload after a successful bootstrap is blocked by the persisted baseline exactly as for the ordinary initial upload.

## Authorization and coordination

The operation holds the existing per-application-storage semaphore and exclusive filesystem lease for the whole publication, and the composed `GitHubConnection` credential/selection gate. Preparation, other recovery/assessment, selection, refresh and disconnect cannot interleave. Lock order remains local storage then connection. Cancelled waiters cannot release another operation's gate. One app instance per account/client and one application-storage location are still required; credential rotation is not coordinated across processes.

Preparation evidence is reconstructed from disk; caller-supplied paths/inventories are never trusted. Both complete snapshots and the current source are rehashed, every payload is bounded-read through the regular-file/no-follow reader and checked against its saved size and SHA-256, and every privately owned payload array plus the generated manifest/tree passes a complete task-6 round-trip verification before any Git object is created. Task-6 read limits apply. Captured bytes are used for the transfer; live source is never reread for upload. Local files are never replaced.

## GitHub primitives

GitHub's Git Data API (`/git/blobs`, `/git/trees`, `/git/commits`, `/git/refs`) refuses to write to an **empty** repository, returning `409 Git Repository is empty.`. An unborn repository therefore cannot be populated with a directly created root commit. The bootstrap instead uses:

- **Contents API file creation** (`PUT /repos/{owner}/{repo}/contents/.pd2cloud/manifest.json`). For an unborn repository this creates the first commit (parent of later history) and the default branch. The returned content id must equal the computed manifest blob id, and the returned commit id/tree are bound for the subsequent checks. This is the only supported way to seed an empty repository.
- **Child commit + non-forced PATCH** for all three cases: after the repository is non-empty, the complete tree is created with direct-child trees (never `base_tree`), a commit is created with the observed head as its single parent, and the branch is updated once with `force: false`.

If the seed succeeds but the subsequent child commit or branch update fails, the branch may be left at the manifest-only seed commit. That intermediate revision classifies as `Incompatible` (the reserved namespace exists without a files tree) until the child commit lands, and recovery reports `ConcurrentUpdate` rather than replaying.

## Durable publication protocol

The existing `upload/` operation directory and its create-new, bounded, chained schema-1 records are reused. The `intent.json` record now binds the bootstrap **direction** (`Upload`, `Initialize`, `Bootstrap` or `Adopt`) in addition to the operation ID/root/fingerprint, preparation journal digest, repository/branch and parent/original tree. `candidate.json` repeats the direction and chains the exact intent bytes; `published.json` chains the exact candidate bytes. For `Initialize`, the parent and original tree are null.

| Direction | Case | Parent/original tree bound |
|---|---|---|
| `Upload` | Managed-empty initial upload | yes |
| `Initialize` | UnbornRepository | no (the seed commit has no parent; the intent binds null parent/tree) |
| `Bootstrap` | EmptyTree | yes |
| `Adopt` | Unrelated | yes (unrelated entries preserved) |

Records remain create-new, flushed and renamed, never overwritten or removed. Unknown, duplicate, missing, malformed, future, oversized, linked, out-of-sequence or mismatched records fail closed. Direction mismatch between the intent and candidate blocks recovery. Generic local assessment still returns Blocked when an `upload/` directory exists, directing to publication recovery.

## Lost responses and recovery

Any transport error, cancellation or interruption retains evidence and blocks replay. `CollectionUploader.AssessRecoveryAsync(operationId, repository)` acquires the same gates, validates the complete chain and exact repository pairing, performs remote reads only and never creates objects or updates references.

| Result | Meaning |
|---|---|
| Interrupted | Valid intent without a candidate; no commit or branch update was attempted (or the Contents API seed was rejected). Unreachable objects or a manifest-only seed commit may remain unreferenced. |
| NotPublished | The current verified head is still the pinned parent, with no receipt. Not proof that publication never happened before a rewind. |
| ConcurrentUpdate | The repository changed: a different/later head, an unborn repository left at the manifest-only seed commit, or a parent after a receipt. Unresolved even for a descendant. |
| Published | Fresh verification finds the exact candidate at the current head and the local evidence verifies. Publication only. |
| PublishedSourceChanged | The candidate verifies but the current source differs or is unavailable. The edit and backup are preserved. |

Corrupt/unavailable evidence or access blocks; it never becomes empty remote state. Repeated assessment is idempotent and never writes the sync state. Only a successful publication persists the baseline.

## Desktop review and confirmation

`SyncViewModel` derives the reviewed `RepositoryBootstrapCase` from the verified cloud result and the selected repository. `Sync All` (labelled **Initialize Repository**, **Create First Collection** or **Review Adoption**) is enabled only for a populated valid local folder with no baseline and no retained evidence when the cloud revision is a bootstrap case or a verified empty managed collection. The reason it is unavailable is always shown.

The review names the exact case and its effect, and shows the repository, branch, pinned revision, file list, count, byte total and fingerprint. Confirmation stays enabled only while every reviewed input (local fingerprint/root, repository/branch, cloud revision and classification) is unchanged. For the unrelated case it additionally requires an explicit "adopt this repository" acknowledgement. Any change underneath drops the prompt and requires a fresh review. Explicit `Sync All` is the only transfer trigger; startup, selection, Refresh and recovery remain read-only.

The result screen reports exactly what happened (`Published`/`PublishedSourceChanged`/`NotPublished`/`ConcurrentUpdate`/`Interrupted`/`Blocked`/cancelled) and names the bootstrap case, never "Synced". The read-only Upload Recovery screen assesses retained evidence under the exact repository pairing without replaying, replacing files or writing a baseline.

## Out of scope

No downloads or source replacement, no implicit initialization/adoption, no automatic merge/deletion, no removal/archival policy and no background synchronization. Incompatible managed data stays blocked. `.local.env`, Git signing, unrelated repository content and generic opaque-file inspection are preserved.

## Verification

Synthetic tests cover unborn bootstrap (Contents API seed commit, then a child commit and non-forced PATCH), empty-tree bootstrap (child commit, non-forced PATCH), adopted unrelated repositories (every unrelated root entry/subtree/mode/id preserved verbatim), incompatible/mismatched classifications blocked before writes, direction binding in the durable records, lost-response and competing-update recovery without replay or baseline, seed rejection (intent-only interruption), and a second upload blocked afterward. Desktop workflow tests cover the reviewed initialization/adoption journey, the required adoption acknowledgement, the dropped prompt when the classification changes underneath, and that startup/selection/Refresh write nothing. No live account or real save data is used.
