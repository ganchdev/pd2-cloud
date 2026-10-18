# Pure collection comparison

`SyncPlanner.Plan` in Core compares the local file list, the verified cloud file list and the files recorded at the last successful sync (or `null` when there is no record). It returns a `SyncAction`: InSync, Upload, Download or Conflict. It has no filesystem, GitHub, authentication, persistence or desktop dependency. It replaced the earlier `CollectionComparer`, which also validated pairings and baseline ancestry and produced blocking reasons and missing-file reviews. See [sync](sync.md) for how the dashboard uses the result.

## Inputs and trust boundary

The planner trusts its caller. `SyncViewModel` plans only with a currently valid local inspection and a cloud result from [verified cloud reads](cloud-reads.md) for the selected repository that is a managed collection. Unavailable, unverified, incompatible or offline cloud state never reaches the planner. Empty and unrelated repositories are handled before planning (Sync sets up cloud storage; see [repository bootstrap](repository-bootstrap.md)).

The last-sync record is used only when its repository, branch and canonical local root match the current selection; otherwise planning proceeds as if there were no record. There is no ancestry or history check: the record is simply the file list both sides held at the last sync.

Inventory validation lives in `CollectionInventory.IsValidFiles` and `IsValidManifest` (canonical safe paths, nonnegative sizes, lowercase SHA-256 values, no colliding paths or file/directory overlaps, matching recomputed fingerprint). The cloud reader and local preparation/download use it; the planner does not re-validate.

## Decisions

Equality compares paths, hashes and sizes after ordinal path sorting, so input order is ignored.

| Last-sync record | Condition | Result |
|---|---|---|
| Either | Local and cloud files equal, including both empty | InSync |
| Absent | Local empty, cloud populated | Download |
| Absent | Cloud empty, local populated | Upload |
| Absent | Both populated and different | Conflict |
| Present | Only local differs from the record | Upload |
| Present | Only cloud differs from the record | Download |
| Present | Both differ from the record | Conflict |

Deletions and renames are ordinary changes. Different-file edits on different sides remain a whole-collection conflict; there is no per-file merge.

## Scope

The planner only chooses an action. Every transfer re-checks its own preconditions: an upload publishes only on top of the pinned revision that was compared ([safe upload](safe-upload.md)), and a download re-verifies the pinned revision and backs up the local folder first ([backups and recovery](backups-recovery.md)). The dashboard reports In sync from the planner's InSync result; when both sides already match but the record is missing or stale, the next check (Refresh, startup or the re-check after a sync attempt) records the sync without transferring anything.
