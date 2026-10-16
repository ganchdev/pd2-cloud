# First safe upload: publication engine

Task 8 publication/recovery API, 2026-10-16. `CollectionUploader` in Remote implements an explicit initial-upload operation. **Task 8 is not complete end to end: Sync All remains disabled and no desktop code invokes this API.** Baseline ancestry and the desktop recovery/review journey must pass their gates before the application can offer synchronization or claim success. Successful-sync persistence now stores the pinned remote revision, verified collection fingerprint and local/remote pairing in a versioned local store following existing store conventions. After a successful upload, the sync state is persisted so the recovery notes (intent/candidate/receipt records) and the "last uploaded" note (`local-sync-state.json`) match. No live GitHub writes were performed during implementation.

## Supported operation and authorization

`UploadAsync(UploadRequest, cancellationToken)` requires an explicit request containing operation ID, exact canonical source root, reviewed collection fingerprint, numeric repository identity/full name/default branch and reviewed pinned commit. A task-7 `PreparedOperation` alone cannot trigger publication. The store reconstructs the complete inventory and final preparation journal from disk; caller-supplied snapshot paths or file inventories are never trusted.

The sole supported direction is a populated local capture into an **already managed, verified empty collection at a real commit**, with no existing successful-sync state. It independently revalidates private/nonarchived/enabled repository identity, installation Contents write/user push permission, branch reference and managed bytes. It requires the exact reviewed head. Unborn/empty-tree/unrelated/incompatible repositories, populated managed collections, empty local inventories and any existing `local-sync-state.json` are blocked. There is no implicit initialization/adoption, reconciliation/removal policy or baseline bypass. This intentionally does not make a newly created private repository upload-ready; bootstrap/adoption remains an explicit future product decision.

## Coordination and immutable bytes

Remote now references Local as well as GitHub/Core. Local still has no GitHub dependency, and GitHub has no Core/Local dependency. Internal friend-assembly capabilities expose scoped operations only to Remote. The public scanner and file inclusion rules are unchanged.

The complete upload or assessment holds the existing per-application-storage semaphore and exclusive filesystem lease. Preparation and other recovery/upload instances cannot interleave. It also holds the single composed `GitHubConnection` credential/selection gate, excluding selection, refresh and disconnect until the operation ends. Lock order is local storage, then connection; these services never acquire in reverse order. Cancelled waiters cannot release another operation's gate. Existing unrelated/partial recovery evidence blocks publication. Credential rotation remains per connection instance; use one app instance per account/client and one application-storage location. There is no new global cross-process credential protocol.

The operation rehashes both complete snapshots and the current source under the lease. Every uploaded payload is bounded-read through the regular-file/no-follow reader and independently compared with the saved size and SHA-256. All privately owned payload arrays and the generated manifest/tree pass a complete task-6 round-trip verification before any Git object POST. Source/capture/backup are revalidated before intent and again after candidate recording, before publication. The entire transfer uses retained captured bytes; it never rereads live source bytes for upload. Task-6 limits apply, including the manifest/aggregate/whole-tree limits. Oversize inventories block wholly; generic inspection gains no exclusions.

Local files are never replaced. Application storage is not an OS-enforced immutable volume and the lease cannot lock external writers. Task-7 ancestor-replacement and filesystem durability limitations still apply. A source edit detected after publication produces `PublishedSourceChanged`; current user bytes are retained and no baseline is written.

## Durable publication protocol

The existing preparation inventory and chained journal remain schema 1 and unchanged. A new `upload/` directory in the retained operation contains up to three create-new, bounded (16 KiB) schema-1 JSON records:

| Record | Exact binding and boundary |
|---|---|
| `intent.json` | Upload direction, operation ID/root/fingerprint, SHA-256 of final preparation record (transitively binding the complete inventory/chain), repository ID/full name/branch, pinned parent and original root tree; flushed before object creation |
| `candidate.json` | SHA-256 of exact intent bytes, verified candidate commit and root tree; flushed before any branch update |
| `published.json` | SHA-256 of exact candidate record bytes and candidate commit; written only after a fresh pinned cloud read verifies the exact candidate tree and complete collection at the current head |

All use task-7 create-new temporary-write, file flush, rename and parent-directory durability. Records are never overwritten or removed. Unknown, duplicate, missing, malformed, future, oversized, linked, out-of-sequence or mismatched records fail closed. Temporary files and an empty/interrupted upload directory require review, never authorization. Hashes provide consistency, not protection from an attacker able to rewrite all app-owned evidence. Generic local `AssessRecoveryAsync` returns Blocked with guidance to use publication assessment when an upload directory exists; it cannot report Prepared and accidentally conceal remote-write evidence.

Publication creates bounded base64 blobs, explicit direct-child Git trees and one commit with the observed head as its **single parent**. Returned blob/tree IDs are checked against locally computed Git object IDs; the immutable commit is fetched to confirm its tree and sole parent. Unrelated root entries and all their referenced subtrees/modes/IDs are preserved verbatim; no `base_tree`, deletion entry or replacement of unrelated content is used. The generated complete tree must still fit the reader limits.

Immediately before reference update, fresh repository/access/head checks must match the pinned original commit/tree, and local evidence is revalidated. The only reference write is PATCH of the selected existing branch with `force: false`, once. There is no reference creation, forced update, automatic retry, reparenting or automatic merge. GitHub's REST update enforces fast-forward, not an atomic expected-old-SHA compare-and-swap. Concurrent normal advances reject the sibling candidate; an external history rewind racing the final check cannot be excluded by this API. No branch lock or stronger remote transaction guarantee is claimed.

Protocol references: GitHub's [blob creation](https://docs.github.com/en/rest/git/blobs#create-a-blob), [tree creation](https://docs.github.com/en/rest/git/trees#create-a-tree), [commit creation](https://docs.github.com/en/rest/git/commits#create-a-commit) and [non-forced reference update](https://docs.github.com/en/rest/git/refs#update-a-reference).

## Lost responses and restart

Any transport error, cancellation or interruption retains evidence. Do not call Upload again: the existence of upload evidence blocks replay. `CollectionUploader.AssessRecoveryAsync(operationId, repository)` acquires the same gates, validates the complete record chain and exact repository pairing, and performs only remote reads. It may append a verified local publication receipt; it never creates remote objects or updates references.

| Result | Meaning |
|---|---|
| Interrupted | Valid intent exists without a candidate; no reference update could have been attempted. Unreachable Git objects may exist. Review retained evidence. |
| NotPublished | The current verified head is still the pinned parent and no receipt exists. This is an observation, not proof that publication never happened before a rewind. No automatic retry is authorized. |
| ConcurrentUpdate | Current head differs from parent/candidate, or returned to parent after a receipt. Preserve candidate and refresh/review. Even a descendant is unresolved without ancestry evidence. |
| Published | Fresh managed-byte verification finds the exact candidate at the current head, complete snapshots/current source still verify, and a durable receipt exists. This confirms publication only. |
| PublishedSourceChanged | Exact candidate publication verifies, but current source differs or cannot be verified. Preserve the edit and backup. No synchronization success is claimed. |

Corrupt or unavailable evidence/access raises a blocking exception; it never becomes empty remote state. A new branch head after a lost response remains unresolved even if it happens to contain identical files. No ancestry or successful baseline is fabricated. Repeated assessment for unchanged inputs is idempotent. Captures, backups, journals, orphan objects and publication records remain retained; reviewed cleanup/abandonment and ordinary subsequent uploads are not implemented.

## Verification and remaining gates

Run `mise run test -- --filter FullyQualifiedName~CollectionUploadTests`, then standard full test/build/format checks. The 44 task-8 synthetic cases cover byte-identical opaque uploads, unrelated executable/link/subtree preservation, explicit pairing/head/root/fingerprint checks, unsupported classifications and limits, baseline blocking, malformed object responses and durable records, rejected and lost reference responses, competing updates before/during PATCH, later/rewound heads, external source edits, immutable snapshot tampering, cancellation, coordinator exclusion and every publication record's temporary-flush/rename/directory-flush boundary. Tests restart with new stores/connections; no live account or source data is used. Task-7 abrupt subprocess coverage continues to apply to the shared storage/lease implementation; upload-specific interruptions are injected exceptions, not abrupt subprocess/power-loss simulations.

Desktop startup/selection/Refresh remain read-only. Sync All is still hard disabled. Before completing task 8 end to end, verify baseline ancestry against pinned commits, test every baseline boundary, and integrate an explicit reviewed upload and recovery journey with stale-pairing/result rejection. Do not treat Published as Synced. Downloads/source replacement additionally require same-filesystem staging and source per-file intents/rename/recovery tests. Initialization/adoption and removal/reconciliation remain blocked pending explicit policies. Live disposable private GitHub acceptance, signed/native platform interaction, Windows/Linux filesystem execution, full-disk/power-loss testing and hosted CI remain open.

Validation on macOS: standard `mise run test` passed all **325 tests** (6 new reconciliation cases + 44 task-8 upload cases), standard `mise run build` passes with zero warnings/errors, `mise run format:check` and `git diff --check` pass. An initial NuGet vulnerability-cache access denial was cleared by an approved outside-sandbox forced restore; final standard commands passed without overrides. No package versions, audit/telemetry settings, credentials, local configuration or Git signing were changed. No new desktop rendering was needed because the desktop is unchanged.
