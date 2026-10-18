# Safe upload: publication engine

`CollectionUploader` in Remote publishes one prepared local snapshot on top of the exact cloud revision it was compared with. Sync uses it whenever the plan is Upload, when cloud storage needs setting up, and for **Keep This PC's Saves** in a conflict; see [sync](sync.md). The three setup cases are specified in [repository bootstrap](repository-bootstrap.md). A successful publication records the sync in `local-sync-state.json`.

## Request and authorization

`UploadAsync(UploadRequest, cancellationToken)` takes the operation ID, exact canonical source root, compared collection fingerprint, numeric repository identity/full name/default branch, the pinned commit (empty for an unborn repository) and a `RepositoryBootstrapCase`. `None` replaces an existing managed collection, empty or not; `UnbornRepository`, `EmptyTree` and `Unrelated` set up cloud storage. A `PreparedOperation` alone cannot trigger publication: the store reconstructs the complete inventory and final preparation journal from disk, and caller-supplied snapshot paths or file inventories are never trusted. An empty local inventory is refused.

The backend independently revalidates private/nonarchived/enabled repository identity, installation Contents write/user push permission, the branch reference and managed bytes, and requires the classification and head to match the request exactly. If they do not, nothing is written and the user is told to refresh and sync again. Incompatible managed data blocks.

## Coordination and immutable bytes

Remote references Local as well as GitHub/Core. Local has no GitHub dependency, and GitHub has no Core/Local dependency. Internal friend-assembly capabilities expose scoped operations only to Remote.

The upload holds the per-application-storage semaphore and exclusive filesystem lease, then the single composed `GitHubConnection` credential/selection gate (excluding selection, refresh and disconnect until it ends). Lock order is local storage, then connection. Only the operation being uploaded may exist in operation storage. Credential rotation remains per connection instance; use one app instance per account/client and one application-storage location.

The operation rehashes both snapshots and the current source under the lease. Every payload is bounded-read through the regular-file/no-follow reader and compared with the saved size and SHA-256. All payloads and the generated manifest/tree pass a complete [cloud read](cloud-reads.md) round-trip verification before any Git object POST. Source/capture/backup are revalidated before the intent, after the candidate and before publication. The transfer uses captured bytes only; it never rereads live source bytes. Cloud read limits apply; oversize inventories block wholly.

Local files are never replaced by an upload. A source edit detected after publication produces `PublishedSourceChanged`: the upload stands, but no sync is recorded and the user is told to sync again.

## Publication

While the upload runs, an `upload/` directory in the operation holds up to three create-new, bounded (16 KiB) schema-1 JSON records: `intent.json` (direction `Upload`/`Initialize`/`Bootstrap`/`Adopt`, operation/fingerprint, preparation digest, repository and pinned parent/tree), `candidate.json` (intent digest, candidate commit and tree) and `published.json` (written only after a fresh read verifies the exact candidate at the head). They tie the steps of one attempt together; the whole operation directory is deleted when the upload call ends, whatever the outcome.

Publication creates bounded base64 blobs, explicit direct-child Git trees and one commit with the observed head as its **single parent**. Returned blob/tree IDs are checked against locally computed Git object IDs. Unrelated root entries and their subtrees/modes/IDs are preserved verbatim; no `base_tree`, deletion entry or replacement of unrelated content is used. For an unborn repository the manifest is first seeded through the Contents API to create the default branch.

Immediately before the reference update, a fresh read must still show the pinned commit/tree (or the unborn seed commit), and local evidence is revalidated. The only reference update is one non-forced PATCH of the branch. There is no forced update, automatic retry, reparenting or merge: if the cloud changed meanwhile it is never overwritten. GitHub's REST update enforces fast-forward, not an atomic expected-old-SHA compare-and-swap; an external history rewind racing the final check cannot be excluded.

Protocol references: GitHub's [blob creation](https://docs.github.com/en/rest/git/blobs#create-a-blob), [tree creation](https://docs.github.com/en/rest/git/trees#create-a-tree), [commit creation](https://docs.github.com/en/rest/git/commits#create-a-commit) and [non-forced reference update](https://docs.github.com/en/rest/git/refs#update-a-reference).

## Results and lost responses

| Result | Meaning | Dashboard message |
|---|---|---|
| Published | A fresh read verifies the exact candidate at the head and the source still matches; the sync is recorded | Uploaded N files |
| PublishedSourceChanged | The candidate is published but the source changed during the upload; no sync is recorded | Sync again to upload the newest files |
| NotPublished / ConcurrentUpdate | The head is still the parent, or moved elsewhere; nothing was overwritten | The cloud saves changed; sync again |

Errors, cancellation and lost responses raise or report failure; evidence is still deleted and nothing is replayed. The next check (Refresh, startup or the re-check after a sync attempt) re-reads the repository: if local and cloud files already match, `SyncViewModel.RecordMatchAsync` records the sync without transferring anything; otherwise the plan simply shows what remains to sync.

## Verification

Run `mise run test -- --filter FullyQualifiedName~CollectionUploadTests` and `mise run test -- --filter FullyQualifiedName~SyncWorkflowTests`, then standard full test/build/format checks. Tests use a fake GitHub server and temporary stores; no live account or source data is used. Live disposable private GitHub acceptance, signed/native platform interaction, Windows/Linux filesystem execution, full-disk/power-loss testing and hosted CI remain open.
