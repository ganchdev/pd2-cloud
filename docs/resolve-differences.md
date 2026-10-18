# Resolve local/cloud differences

Implemented 2026-10-18. This is an explicit, reviewed replacement flow, not automatic synchronization or merging. It covers the practical case where a fresh install finds a populated local folder and a different verified cloud collection.

## Offer and review

**Resolve** appears on the dashboard when all of the following hold:

- The local inspection is valid and the folder is selected.
- A private repository is selected and its cloud collection was verified at one pinned revision.
- The comparison is `Conflict`, `InitialDownload`, or (when a baseline is later supplied) `Upload`, `Download` or `MissingFileReview`.
- There is no retained publication/recovery evidence awaiting review.
- Any existing baseline is still an ancestor of the remote head.

The Resolve screen replaces the dashboard view, names the local and cloud file counts and the checked revision, and offers two complete-side choices. The title-bar Back button is at the top right; Cancel is at the bottom left.

| Choice | Effect |
|---|---|
| **Pull cloud → local** | Replaces the files in the selected local folder with the verified cloud collection. A complete backup of the current local folder is written first and retained. |
| **Push local → cloud** | Publishes the reviewed local snapshot as a replacement of the PD2 Cloud collection in a new child commit. Unrelated repository entries are preserved verbatim. Local files are not modified. |

There is no merge, no per-file side selection and no prompt to delete individual files. Pull replaces the collection; push replaces the managed collection.

## Pull safety sequence

`CollectionPuller` re-reads the repository and re-verifies the pinned revision, repository identity, tree, manifest and every blob hash before touching local files. It then:

1. Acquires the same application-storage gate and filesystem lease used by preparation/upload.
2. Blocks when retained recovery evidence exists.
3. Re-inspects the local folder and copies every file into `LocalApplicationData/PD2Cloud/replacements/<id>/files`, verifies the complete backup, and writes a schema-1 `backup.json` beside it.
4. Writes each verified cloud payload to a temporary file in the destination directory, flushes it, and renames it over the target.
5. Removes old local files that are not part of the cloud collection.
6. Re-inspects the folder and requires exact file membership, sizes, hashes and collection fingerprint.
7. On any failure or cancellation before verification, attempts to restore the backup; the backup is retained either way.
8. On success, saves the pinned revision, fingerprint and local/remote pairing as the local sync baseline.

A changed revision or fingerprint is rejected before any local write. The backup directory is never deleted by the puller. If the baseline note cannot be saved, the installed files are left in place and the result says the note failed; the backup remains.

## Push safety sequence

Push calls the existing immutable preparation (capture plus complete backup) and then the publication engine with `UploadMode.ReplaceManaged`:

- The pinned parent commit, repository, branch and reviewed local fingerprint must still match.
- The local capture is rehashed and the source must still match before publication.
- The generated tree replaces only the `.pd2cloud` namespace; every unrelated root entry, mode and object is preserved.
- Publication uses one non-forced reference update and retains the same intent/candidate/receipt evidence as an initial upload.
- Successful publication writes the new baseline. A changed source produces `PublishedSourceChanged` and preserves the newer local bytes.

## Limits and remaining work

- This is whole-collection replacement, not a merge. Version history, per-file review and deletion policy are not implemented.
- Automatic/background synchronization is still absent; both actions require the reviewed Resolve screen and fresh verified inputs.
- Retained backups and publication evidence are not pruned from the UI. Storage growth must be managed manually.
- Application storage is not an OS-enforced immutable volume, and the cooperative lease does not stop arbitrary external writers. Source ancestor-replacement and filesystem durability limitations described in [backups and recovery](backups-recovery.md) still apply.
- Live GitHub writes, Windows/Linux native filesystem behavior, abrupt process death during pull and hosted CI acceptance remain open. Tests use the synthetic GitHub server and disposable folders.
