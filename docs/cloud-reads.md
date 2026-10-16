# Verified cloud reads

Current task-8 extension: [safe upload](safe-upload.md) provides a coordinated initial-upload/publication-recovery API with repository/branch/pinned-head binding and durable candidate records, and [repository bootstrap](repository-bootstrap.md) adds explicit, user-confirmed initialization/adoption for unborn, empty-tree and unrelated repositories. The desktop offers an explicit reviewed upload (Sync All) only for a populated local folder into an empty verified managed collection or a reviewed bootstrap case with no existing baseline or retained evidence, plus a read-only Upload Recovery screen. Publication never establishes synchronization success and no baseline is fabricated. Earlier task-specific transfer limitations below describe their original delivery boundary. Live GitHub and native acceptance remain open.

Task 6 implementation contract, 2026-10-16. Cloud inspection is read-only. It classifies the selected private repository and supplies Core comparison only with a complete verified managed collection. It never initializes a repository, uploads/downloads into a folder, records a successful synchronization or persists a remote cache.

## Boundaries and triggers

`PD2Cloud.GitHub` retrieves bounded authenticated Git objects using the existing serialized credential/refresh boundary. It still has no Core, Local or Desktop dependency. `PD2Cloud.Remote` references GitHub and Core to verify the storage contract. Local inspection has no GitHub dependency. Desktop composes these through `CloudInspectionViewModel` and the existing shared workflow.

Startup checks cloud content after restoring a verified selection. A completed user's dashboard appears after local inspection without waiting for GitHub or cloud inspection. Selecting a repository and successful Settings reconnect also start a read-only check. Dashboard Refresh independently inspects local files and reloads connection metadata, then checks the selected repository's contents. Settings Check Connection and repository-list refresh remain metadata-only and invalidate previous cloud evidence. Local Retry Inspection and Settings → Saves inspection never request cloud data. There is no timer, background transfer or automatic repository initialization.

Starting a metadata/account/selection operation invalidates cloud results and cancels a pending cloud check. Disconnect, selection changes, cancellation, failed refresh and window disposal cannot retain an old comparison. Late results are rejected by generation and pairing checks. Cloud failures do not block local inspection. Check Cloud Files retries without rescanning the folder and is available on the shared cloud card, including onboarding and Settings. Cancel Cloud Check is available while reading. The checked commit is displayed with the result.

## Repository classification

| Result | Meaning and comparison boundary |
|---|---|
| Empty repository | Explicit empty commit-list response, or GitHub's specific HTTP 409 `Git Repository is empty.` response; rechecked before accepting. No commit or collection is fabricated. A pinned revision with an empty tree is also classified empty, without inventing a managed manifest. An explicit, reviewed initialization can create the first collection ([repository bootstrap](repository-bootstrap.md)). |
| Managed collection | Schema-1 namespace, complete tree and manifest agree, all file bytes pass verification. Only this classification supplies a verified `RemoteCollection`. |
| Unrelated | Complete pinned tree contains entries but no reserved namespace. An explicit, reviewed adoption can add the namespace while preserving every unrelated entry ([repository bootstrap](repository-bootstrap.md)); selection alone never adopts. |
| Incompatible | Reserved namespace exists but its layout, version, paths, objects or manifest are unsafe/incomplete/unsupported. No comparison, and no bootstrap. |
| Unavailable | HTTP, secure-store, access, identity, branch, resource-limit or freshness failure. No partial/cached comparison. Missing branches, 404s and arbitrary 409s never mean empty. |

Selection and setup completion remain account/folder preferences; neither authorizes adoption or synchronization on its own. Unrelated or incompatible repositories can remain selected for recovery in Settings. Unrelated or empty-tree repositories can supply comparison/classification evidence but never a fabricated `RemoteCollection`; an unborn repository has no commit. Empty **managed** collections at a real commit can produce an advisory initial-upload decision.

## Storage contract

The owned namespace is `.pd2cloud/`. It contains exactly `manifest.json` and a `files/` tree (including an explicit empty Git tree for an empty collection). Opaque file paths under `files/` match the selected folder's canonical relative paths. Unrelated entries outside this namespace are excluded from collection comparison and remain untouched. Case/Unicode aliases of the reserved root are incompatible, not unrelated. Unknown entries inside the owned namespace block verification.

The manifest is UTF-8 JSON with exactly these fields:

```json
{
  "Format": "PD2Cloud.Collection",
  "SchemaVersion": 1,
  "Files": [],
  "CollectionFingerprint": "<schema-1 fingerprint of the complete inventory>"
}
```

Each file object contains exactly `RelativePath` (canonical NFC, `/` separators), `SizeBytes` (nonnegative integer) and `Sha256` (lowercase 64-digit SHA-256). The fingerprint uses the existing Core schema-1 encoding; sizes are checked independently. The manifest contains no local root, credentials, timestamps or synchronization success. Duplicate/unknown/missing properties, unsafe paths, case/NFC collisions, directory/file overlaps, unsupported schemas and unlisted/missing files block verification. Managed files must use regular mode `100644`; symlinks, executable modes and submodules are incompatible. Managed directory paths are checked too, including empty directories.

## Pinned verification and resource bounds

Each operation refreshes expiring tokens durably before use and checks fresh installation permissions plus direct numeric repository ID/full name/default branch/private/nonarchived/enabled/push metadata. It resolves the exact branch reference to a commit, reads the commit's tree ID, then retrieves the complete recursive tree at that immutable ID. Nontruncated responses are mandatory. API-supplied URLs are never followed; object IDs and path parameters build fixed-origin HTTPS requests.

Every tree is reconstructed with Git's binary framing, mode/name ordering and SHA-1 object hashing, including unrelated entries and empty subtrees. Returned root/subtree IDs, parents, entry kinds and uniqueness must agree. Managed blobs are read by their immutable IDs, decoded from base64 and checked against actual byte length and Git blob SHA-1. The manifest's SHA-256, sizes and complete membership must match those bytes. Core's pure validation independently checks the inventory fingerprint and canonical paths. Opaque data is neither parsed nor executed. Unrelated blob bytes are not downloaded.

Repository access/identity and the selected branch head are checked again before accepting the snapshot. A moved branch or changed access rejects the observation and asks for Refresh. This is evidence at the checked revision, not a branch lock or future write authorization. Commit/tree associations and empty-repository signals are trusted from authenticated GitHub responses; tree/blob consistency is checked locally. No signed-commit verification is claimed.

| Bound | Limit |
|---|---|
| JSON response | 4 MiB, enforced during response buffering, with a 30-second body timeout |
| Complete repository tree | 10,000 entries, including unrelated files/directories |
| Managed files | 1,000 |
| Individual file | 2 MiB decoded |
| Manifest | 512 KiB decoded |
| Managed collection | 64 MiB decoded, including manifest; repeated blobs count once per file path |

Requests are sequential and cancellation-aware. Decoded blobs are retained only in the temporary read snapshot and discarded after verification; the UI retains inventory metadata, not file payloads. No disk staging/cache is created. Unsupported bounds fail closed. Credential storage and its existing in-process serialization/platform limits are unchanged.

## Trying the current app

Run `mise run run` using your existing ignored `.local.env` configuration. Connect the registered GitHub App to a disposable private repository, choose a temporary local folder with arbitrary files and nested directories, and complete setup. Check the local inventory and Cloud Collection card. Modify local files and use dashboard Refresh, restart to verify saved progress, and test offline behavior while local inspection remains available. Check Cloud Files retries only the cloud read; Cancel Cloud Check discards pending evidence.

A newly created repository should report Empty repository. A repository with only a README should report Unrelated. A disposable repository containing `.pd2cloud/manifest.json` with invalid JSON should report Incompatible. An empty or unrelated repository can now be initialized/adopted through the explicit reviewed bootstrap ([repository bootstrap](repository-bootstrap.md)); matching/conflicting managed comparisons are otherwise exercised by scripted fixtures. Manual seeding must follow the exact contract above, including the explicit files tree and Git object modes.

Run `mise run test`, `mise run build` and `mise run format:check` for the full checks. To run the cloud fixture cases alone, use `mise run test -- --filter FullyQualifiedName~CloudCollectionTests`. Automated fixtures do not establish live authentication or native platform acceptance.

## Advisory comparison and remaining work

Desktop compares the current complete local inventory with the verified managed remote inventory. No baseline is loaded or fabricated. Results are matching files, initial upload/download or whole-collection conflict; different files on different sides still conflict. Changing local inspection recomputes the advisory result; incomplete local inspection suppresses it. Matching inventories never display Synced or establish a baseline. The overall status remains Not yet synchronized or Needs attention (or a bootstrap readiness label). A verified empty managed collection with a populated local folder and no baseline enables the explicit reviewed upload (Sync All); an unborn, empty-tree or unrelated repository enables the explicit reviewed bootstrap; all other comparisons keep the action disabled.

There is no baseline persistence or ancestry lookup in this task because no successful device baseline exists. A future persisted baseline must supply verified ancestry against the exact pinned commit before the comparer can accept it. Task 7 now supplies [local immutable capture, verified complete backups and recovery journals](backups-recovery.md), without desktop invocation or source writes. Explicit transfer, source replacement, non-forced publication and removal/adoption decisions remain later gates. Task-6 cloud reads and their validation evidence are unchanged.

Validation on macOS: all **208 tests** pass (65 new cases), the standard Release build passes with zero warnings/errors, and formatting/diff checks pass. Tests cover scripted authenticated HTTP, synthetic Git objects/opaque bytes, limits, stale results, malformed content, access/head changes, durable token rotation and independent local inspection. The real MainWindow was rendered and inspected at normal/minimum sizes using synthetic HTTP and memory credentials; harness/captures are at `/private/tmp/pd2-cloud-ui/`, outside the repository. Native rendering required execution outside the sandbox after Avalonia error -6661; no product configuration changed. These are rendering checks, not native pointer/keyboard acceptance. Live GitHub reads/registration/authentication, native browser/picker/clipboard acceptance, signed macOS Keychain behavior, Windows/Linux execution and hosted CI remain unverified. Reference protocols: [Git trees](https://docs.github.com/en/rest/git/trees), [Git blobs](https://docs.github.com/en/rest/git/blobs), [Git references](https://docs.github.com/en/rest/git/refs).
