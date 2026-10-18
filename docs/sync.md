# Sync

The dashboard has a **Sync** button that picks the direction itself, and a **Restore** button for choosing it yourself. Refresh checks the save folder and the cloud copy; only Sync and Restore transfer files.

## What Sync does

Sync compares three inventories: the local save folder, the verified cloud collection at one checked revision, and the files recorded at this PC's last successful sync (`local-sync-state.json` in application data). `SyncPlanner.Plan` in Core makes the decision.

| Situation | Status | Sync |
|---|---|---|
| Repository empty (no commits or an empty tree) or holding only unrelated files | Unsynced | Sets up cloud storage, then uploads |
| Local and cloud files match | In sync | Nothing to do; the sync record is refreshed if it was missing or stale |
| Only this PC changed since the last sync | Unsynced | Uploads |
| Only the cloud changed since the last sync | Unsynced | Downloads (local files backed up first) |
| No sync recorded yet: local folder empty, cloud populated | Unsynced | Downloads |
| No sync recorded yet: local populated, cloud collection empty | Unsynced | Uploads |
| Both changed, or never synced and both populated and different | Unsynced | Opens Restore so you choose Pull or Push |
| Local folder and cloud storage both empty | Nothing to sync yet | Disabled |
| Cloud saves can't be read (changed outside PD2 Cloud, e.g. edited on GitHub, or written by a newer version) | Needs attention | Disabled; Restore can push this PC's saves to replace them |
| Folder, connection or repository not usable | Needs attention | Disabled, with the reason |

Files are compared by path, size and SHA-256. Deleting or renaming a file is an ordinary change on the side where it happened and carries over to the other side. Nothing is merged per file; the chosen side replaces the other as a whole.

## Restore

Restore offers two whole-collection choices at any time, regardless of what Sync would do:

- **Pull cloud → this PC** runs the download below. It needs readable cloud saves.
- **Push this PC → cloud** runs the upload below. It also sets up empty or unrelated repositories, and it can replace cloud saves that no longer verify, for example after a file was deleted in the GitHub web UI while `manifest.json` still lists it. Earlier versions stay in the repository's Git history.

Edit the cloud saves only through PD2 Cloud. Changing files in `.pd2cloud/` directly breaks the manifest, and the cloud then counts as unreadable until a push replaces it.

## A typical two-PC rhythm

1. PC1: Sync sets up the repository and uploads. Play, quit, Sync → upload.
2. PC2 (empty save folder): Sync → download. Play, quit, Sync → upload.
3. PC1: Refresh shows Unsynced; Sync → download. Play, quit, Sync → upload.

## Upload

1. Capture the local folder into application data and verify it still matches what Refresh saw.
2. Build the commit: the `.pd2cloud/` collection is replaced; every other repository entry, mode and object is preserved. An unborn repository is first seeded through the Contents API.
3. Publish with one non-forced branch update on top of the checked revision. If the cloud changed in the meantime the update is refused and nothing is overwritten; Refresh and Sync again.
4. On success, record the published revision and files as the last sync.

Local files are never modified by an upload. Capture data is deleted after every attempt, and anything left behind by a crash is discarded by the next sync.

If the response to the branch update is lost, the app does not retry. The next check (the re-check after the sync attempt, Refresh or startup) reads the repository: when the cloud already matches the local files, the sync is recorded.

## Download

1. Re-read the repository and require the exact checked revision and collection fingerprint, then verify every file's bytes.
2. Back up the whole local folder to `replacements/<id>/files` in application data and verify the backup.
3. Write each cloud file through a temporary file and rename it into place, then remove local files that are not in the cloud collection.
4. Re-inspect the folder and require an exact match. On any failure or cancellation, restore the backup.
5. Record the revision and files as the last sync.

Only the newest five backups are kept; older ones are deleted after a successful download.

## Limits

- Whole-collection replacement only: no per-file merge, version browser or undo beyond the local backups and the repository's Git history.
- No background or automatic sync.
- The application-data lease stops two PD2 Cloud instances interfering, not arbitrary external writers. Avoid syncing while the game is writing saves.
- Live GitHub, Windows/Linux native filesystem behaviour and abrupt process death during a download are not covered by automated tests; tests use a synthetic GitHub server and disposable folders.
