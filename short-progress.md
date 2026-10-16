# Short progress — remaining development work

Tasks 1–8 backends are implemented (folder inspection, discovery, dashboard, comparison, GitHub connection, cloud reads, backups/recovery, upload/publication engine). What's left, in plain language:

**Blocking task 8**
1. **Successful-sync persistence** — Done. Added `LocalSyncStateStore` in `PD2Cloud.Local` that persists the pinned remote revision, verified collection fingerprint and local/remote pairing as a versioned JSON document (`local-sync-state.json`) in application data, following existing store conventions (schema version, create-new + flush + rename, preserve corrupt/future documents).

2. **Journal/baseline reconciliation** — After an upload, make the recovery notes and that "last uploaded" note match, so they don't tell two different stories.
3. **Verified baseline ancestry** — Check that the saved "last uploaded" version really is an older version of what's on GitHub now, instead of just trusting the label.
4. **Desktop reviewed upload UI** — Turn the dead Sync All button into a real one that shows what will be uploaded and then what actually happened.
5. **Desktop recovery UI** — A screen that flags an upload that got interrupted, lost, or collided with someone else's change, and lets you deal with it.

**Remaining chunks**

6. **First safe download** — Pull cloud files down into an empty folder safely, then test changes flowing one direction at a time.
7. **Two-device byte-identical transfer** — Test that two different computers end up with exactly the same files.
8. **Conflict/missing-file review UI** — When both sides changed the same stuff, keep both copies, let the user pick which side wins, and drop the prompt if things change underneath.
9. **Removal/archival policy decision** — Decide what happens when a file is deleted: delete it everywhere, or keep it archived somewhere.
10. **Distribution & packaging** — Build signed, installable apps for each OS so people can actually run it.

## Next-agent prompt

```text
Read PD2-CLOUD-HANDOFF.md, short-progress.md, README.md and all of docs/. Base commit:
388fbe0 on main (task 8, signed/pushed). 320 tests, build, format and diff checks pass.

Do only this one task: implement journal/baseline reconciliation for successful-sync
state. After an upload, make the recovery notes and the "last uploaded" note match so
they don't tell two different stories. Follow the existing store conventions (schema
version, create-new + flush + rename, preserve corrupt/future documents). Add focused
tests and verify with `mise run build`, `mise run test`, `mise run format:check`,
`git diff --check`.

Persist only: do not use the baseline for decisions, skip verified baseline ancestry,
leave the desktop and Sync All alone, never fabricate a baseline or say "Synced". No
downloads, init/adoption, merge/deletion, source replacement or background sync. Keep
.local.env and Git signing; update README, docs, short-progress.md and both handoff copies.
Do not commit or push. Stop when this one task is done and verified.
```
