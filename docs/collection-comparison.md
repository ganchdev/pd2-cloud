# Pure collection comparison

Task 4 implementation contract, 2026-10-15. `CollectionComparer.Compare` in Core consumes a `CollectionPairing`, a local `CollectionManifest`, a `RemoteCollection` and an optional supplied `LocalSyncState`. It returns a `CollectionComparison`: decision, typed blocking reason and sorted baseline paths missing on each side. It has no filesystem, GitHub, authentication, persistence or desktop dependency.

## Inputs and trust boundary

The pairing identifies repository, branch and selected local root. Identifiers are compared ordinally, without path normalization or filesystem queries. Callers must supply the same canonical root spelling used by inspection and the accepted baseline; a spelling change conservatively blocks reuse. Local inspection must match that root. Remote manifest roots and inspection timestamps do not participate in equality.

`RemoteVerification.Unavailable` and `Unverified` always block, even with a cached or empty manifest. `Verified` requires a nonempty pinned commit and a complete valid manifest for the selected repository/branch. This is an assertion by the caller, not verification performed by the comparer. Task 6 now verifies pinned manifest/tree/blob consistency, repository identity, freshness and resource limits before asserting it; see [cloud reads](cloud-reads.md). Missing branches and network failures cannot supply a verified empty collection.

A supplied baseline must have schema 1, a nonempty accepted commit, matching repository/branch/local root and a valid file inventory. A different remote commit requires `BaselineHistory.ContainsBaseline`, based on verified ancestry relative to that exact accepted commit. `Unknown` blocks; `Rewritten` blocks even if inventories match. An identical accepted/current commit needs no additional ancestry evidence. No baseline is inferred from a successful inspection or equality.

Inventory checks require schema 1, stability, no issues, a complete file list and a matching recomputed collection fingerprint. File checks require canonical safe paths, nonnegative sizes, lowercase 64-digit SHA-256 values, no duplicate/colliding paths or implied directories, and no file/directory overlaps. Baseline files receive the same file checks. These checks cannot prove the provenance or completeness of a caller's data or recover omitted empty-directory diagnostics; the scanner/remote verifier must provide that evidence. Inputs must remain unchanged during the synchronous call.

## Decision order

Validation blocks precede all inventory decisions. Missing baseline paths precede equality and directional decisions. Within each valid inventory, input ordering is ignored; equality compares canonical paths, hashes and sizes. Collection fingerprints exclude sizes by schema design, so a matching fingerprint alone is insufficient for equality.

| Baseline | Local/remote condition | Result |
|---|---|---|
| Present | Any baseline path missing on either side, including identical removals or renames | MissingFileReview, with per-side missing paths |
| Either | Complete local and remote inventories equal, including both empty | NoTransfer |
| Absent | Local populated, verified remote empty | InitialUpload |
| Absent | Local empty, verified remote populated | InitialDownload |
| Absent | Both populated and different | Conflict |
| Present | Only local differs | Upload |
| Present | Only remote differs | Download |
| Present | Both differ and are unequal | Conflict |

Different-file edits on different sides remain a whole-collection conflict. Additions on only one side can produce a directional decision; deletion of any previously synchronized file requires review. An empty successful baseline is valid and uses the normal one-sided rules rather than initial-sync decisions.

## Scope and verification

Results are advisory observations, never permission to transfer or advance a baseline. There is no automatic merge/deletion, side-selection workflow, baseline persistence, background synchronization or fabricated successful synchronization. Future transfers still require immutable capture, revalidation, backups, journals and an explicit removal/archival policy. Future review choices must be invalidated if either side changes. The desktop now invokes advisory comparison for a current valid local inventory and a task-6 verified managed remote collection. It supplies no baseline, because successful-sync persistence is unimplemented. Empty uninitialized repositories, unrelated content, incompatible data and unavailable reads supply no fabricated inventory. Connection/selection never fabricates a baseline. See [GitHub connection](github-connection.md).

On macOS, 52 added pure cases cover the decision table, matching/disjoint changes, missing paths, invalid manifests/baselines, identity mismatch, unknown remote state and history checks. All 97 project tests passed; standard `mise run build` passed with zero warnings/errors and `mise run format:check` passed. Existing scanner fingerprint vectors still pass after sharing the encoding in Core. No UI changes were required. Native Windows/Linux and hosted CI results have not been checked for this task.

The [setup UX follow-up](github-setup-ux.md) keeps these boundaries: changing repositories clears the displayed verified selection before revalidation, and no comparison result or synchronization status is reused for a new pairing. The UI reports Not yet synchronized or Needs attention and shows task-6 advisory decisions only with verified cloud inputs. Matching files never establish synchronization success. Metadata/account/selection changes cancel and invalidate cloud evidence.

## Shared validation and task 7

Inventory/manifest validation now lives in `CollectionInventory.IsValidFiles` and `IsValidManifest`, shared by the comparer and [local preparation/recovery](backups-recovery.md). The comparison decision order and results are unchanged. Task 7 produces retained local evidence, not remote verification, ancestry evidence or a successful baseline; no preparation record can authorize a comparison-driven write.
