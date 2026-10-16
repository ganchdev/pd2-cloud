# Short progress — remaining development work

Tasks 1–8 backends plus the explicit desktop reviewed upload/recovery journey are implemented (folder inspection, discovery, dashboard, comparison, GitHub connection, cloud reads, backups/recovery, publication engine with journal/baseline reconciliation and verified baseline ancestry, and a reviewed Sync All with a read-only Upload Recovery screen). Explicit repository initialization/adoption is now implemented too. What's left, in plain language:

**Blocking task 8**
1. **Successful-sync persistence** — Done. `LocalSyncStateStore` in `PD2Cloud.Local` persists the pinned remote revision, verified collection fingerprint and local/remote pairing as a versioned JSON document (`local-sync-state.json`) in application data, following existing store conventions.
2. **Journal/baseline reconciliation** — Done. After a successful upload, `CollectionUploader` persists the successful-sync state so the recovery notes and the "last uploaded" note match. Only successful publication writes the sync state.
3. **Verified baseline ancestry** — Done. `CollectionUploader.VerifyBaselineAncestryAsync` checks that the saved "last uploaded" commit is an ancestor of the current GitHub head; `LocalSyncStateStore.VerifyAncestryAsync` exposes the same check. Read-only and never fabricates a baseline.
4. **Desktop reviewed upload UI** — Done. The dead Sync All button is now a real reviewed initial upload (`SyncViewModel` + `SyncOperations` + UploadReview screen). It shows exactly what will be published (repository, branch, pinned revision, files, sizes, fingerprint), keeps Confirm enabled only while every reviewed input is unchanged, and reports exactly what happened (`Published`/`PublishedSourceChanged`/`NotPublished`/`ConcurrentUpdate`/`Interrupted`/blocked/cancelled). Never "Synced".
5. **Desktop recovery UI** — Done. A read-only Upload Recovery screen lists retained publication evidence, assesses it under the exact repository pairing and reports the outcome without replaying, replacing files or writing a baseline. Re-checking is idempotent.
6. **Repository initialization/adoption** — Done. Explicit, user-confirmed bootstrap makes the reviewed initial upload reachable end to end. `CollectionUploader.UploadAsync` accepts a reviewed `RepositoryBootstrapCase`: **unborn** → the manifest is written through the Contents API (GitHub's Git Data API rejects writes to an empty repository) to create the first commit and default branch, then a child commit adds the complete `.pd2cloud/manifest.json` + `.pd2cloud/files/`, non-forced PATCH; **empty tree** at a real pinned commit → child commit adding `.pd2cloud/`, non-forced PATCH; **unrelated populated** → only after an explicit adoption acknowledgement, a child commit adding `.pd2cloud/` while preserving every unrelated root entry/subtree/mode/id verbatim. Incompatible managed data and mismatched classifications stay blocked; no implicit writes, `base_tree` deletion, forced update, reparenting or automatic retry/merge. The durable `intent`→`candidate`→`receipt` chain now binds the direction (`Upload`/`Initialize`/`Bootstrap`/`Adopt`). The desktop review names the exact case, labels the action, requires the adoption acknowledgement, and drops the prompt if the classification changes underneath. See `docs/repository-bootstrap.md`.

**Remaining chunks**

7. **First safe download** — Pull cloud files down into an empty folder safely, then test changes flowing one direction at a time.
8. **Two-device byte-identical transfer** — Test that two different computers end up with exactly the same files.
9. **Conflict/missing-file review UI** — When both sides changed the same stuff, keep both copies, let the user pick which side wins, and drop the prompt if things change underneath.
10. **Removal/archival policy decision** — Decide what happens when a file is deleted: delete it everywhere, or keep it archived somewhere.
11. **Distribution & packaging** — Build signed, installable apps for each OS so people can actually run it.

## Open task-8 items (not blocking the reviewed initial upload)

- Ordinary **subsequent (baseline) uploads** are not implemented: any existing `local-sync-state.json` or retained recovery evidence blocks another upload. Reviewed cleanup/abandonment is likewise not implemented.
- Live disposable private-repository GitHub acceptance, signed/native platform interaction and new native rendering evidence for the added screens remain open. The new screens are covered by view-model tests only.
- Downloads/source replacement still need same-filesystem staging, per-file source intents and rename/restart tests before they can be enabled.

## Next-agent prompt

```text
Read PD2-CLOUD-HANDOFF.md, README.md and every file in docs/, especially
safe-upload.md, repository-bootstrap.md, backups-recovery.md, delivery-plan.md,
implementation-plan.md and cloud-reads.md. Inspect implementation, tests and Git
status.

Base commit: 7128e2f on main (task 8 successful-sync state: versioned
LocalSyncStateStore, journal/baseline reconciliation, verified baseline
ancestry), on top of the pushed task-8 publication-engine commit 388fbe0. The
explicit desktop reviewed upload and read-only recovery integration, and the
explicit repository initialization/adoption bootstrap, are implemented on top
of that but are still uncommitted. All 367 tests, Release build (zero
warnings/errors), formatting and diff checks pass on macOS.

Item 6 (repository initialization/adoption) is complete: `CollectionUploader`
now accepts a reviewed `RepositoryBootstrapCase` for unborn (Contents API seed
commit + child commit + non-forced PATCH), empty-tree (child commit, non-forced
PATCH) and unrelated (adoption, child commit preserving every unrelated entry
verbatim) repositories. The durable intent→candidate→receipt records bind the
bootstrap direction. The desktop review names the case, requires the adoption
acknowledgement and drops stale prompts. See docs/repository-bootstrap.md.

Turn this work into a reviewable commit only if the user asks. Continue with
short-progress item 7, the first safe download: verified installation into an
empty target folder, then one-sided changes flowing one direction at a time,
with same-filesystem staging, per-file source intents, rename/restart recovery
and stale-review invalidation. Preserve local configuration, Git signing,
unrelated repository content and generic opaque-file inspection. No implicit
initialization/adoption, automatic merge/deletion, removal policy or background
sync. Do not commit or push without the user's instruction.
```
