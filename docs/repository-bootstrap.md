# Repository initialization/adoption (bootstrap)

Automatic cloud-storage setup for the first upload. This extends [safe upload](safe-upload.md). A freshly created private repository is *unborn* (no commits, no default-branch reference) and is classified `EmptyRepository`; a repository with files but no `.pd2cloud/` namespace is classified `Unrelated`. When the selected repository is in either state and the local folder has files, the dashboard shows "Cloud storage is not set up yet" and **Sync** sets it up and uploads the saves in one step; there is no separate review or acknowledgement. See [sync](sync.md).

Selection, startup, Refresh and Disconnect never write; only Sync does.

## Supported cases

`SyncViewModel` derives the `RepositoryBootstrapCase` from the verified cloud result and passes it in the `UploadRequest`. The uploader requires the repository to still have exactly that classification at the pinned commit (empty for the unborn case).

| Case | Required classification | Publication |
|---|---|---|
| UnbornRepository | `EmptyRepository` with no commit and no reference | The manifest is written with the Contents API (which creates the first commit and the default branch), then one child commit adds the complete `.pd2cloud/manifest.json` + `.pd2cloud/files/` and the branch is updated with a non-forced PATCH |
| EmptyTree | `EmptyRepository` at a real pinned commit with an empty tree | One child commit adding `.pd2cloud/`, non-forced PATCH of the existing branch |
| Unrelated | `Unrelated` (complete pinned tree with entries and no reserved namespace) | One child commit adding `.pd2cloud/`, preserving every unrelated root entry/subtree/mode/id verbatim; non-forced PATCH |

The local inventory must be populated; with an empty folder and no cloud storage the dashboard shows "Nothing to sync yet". `Incompatible` managed data and mismatched classifications block before any write. After a successful setup the repository holds a managed collection and later syncs use the ordinary plan.

## Authorization and coordination

Bootstrap uses the same gates, preparation evidence, payload verification and round-trip checks as every upload; see [safe upload](safe-upload.md). Local files are never replaced.

## GitHub primitives

GitHub's Git Data API (`/git/blobs`, `/git/trees`, `/git/commits`, `/git/refs`) refuses to write to an **empty** repository, returning `409 Git Repository is empty.`. An unborn repository therefore cannot be populated with a directly created root commit. The bootstrap instead uses:

- **Contents API file creation** (`PUT /repos/{owner}/{repo}/contents/.pd2cloud/manifest.json`). For an unborn repository this creates the first commit and the default branch. The returned content id must equal the computed manifest blob id, and the returned commit id/tree are bound for the subsequent checks.
- **Child commit + non-forced PATCH** for all three cases: the complete tree is created with direct-child trees (never `base_tree`), a commit is created with the observed head as its single parent, and the branch is updated once with `force: false`.

If the seed succeeds but the subsequent child commit or branch update fails, the branch may be left at the manifest-only seed commit. That revision classifies as `Incompatible` (the reserved namespace exists without a files tree), which blocks Sync; nothing is replayed automatically.

## Durable records

The upload's `intent.json` and `candidate.json` bind the **direction**: `Upload` (existing managed collection), `Initialize` (unborn; null parent/tree), `Bootstrap` (empty tree) or `Adopt` (unrelated). Records exist only for the duration of the upload call; see [safe upload](safe-upload.md).

## Verification

Synthetic tests cover unborn bootstrap (Contents API seed commit, then a child commit and non-forced PATCH), empty-tree bootstrap, adopted unrelated repositories (every unrelated root entry/subtree/mode/id preserved verbatim), incompatible/mismatched classifications blocked before writes, lost responses and concurrent updates without replay or overwrite, and seed failure. No live account or real save data is used.
