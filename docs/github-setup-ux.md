# GitHub setup and Settings UX

Implemented 2026-10-16. This replaces the single combined inspection/connection panel with a dark Avalonia desktop workflow. Task 6 now extends this journey with [verified cloud reads](cloud-reads.md) and advisory comparison. Transfers and successful-sync persistence remain unimplemented.

## Navigation and saved progress

`SetupWorkflowViewModel` owns screen navigation and origin-aware return destinations. `MainWindowViewModel` retains folder inspection/preferences; `GitHubConnectionViewModel` retains authentication, secure storage and repository metadata operations. One set of connection, authorization and repository screens serves onboarding and Settings.

| Entry | Journey |
|---|---|
| New installation | Welcome → Connect → Authorize → Repository → Folder → Dashboard |
| Interrupted before authentication | Connect, when Get Started was previously saved |
| Connected without a repository | Repository |
| Repository chosen, setup not completed | Folder; Continue requires a fresh valid inspection |
| Completed setup | Dashboard, including offline, disconnected and missing-folder states; the dashboard opens after local inspection without waiting for GitHub |
| Settings → GitHub → Change Repository | Repository → Settings; existing authorization is reused |
| Settings → GitHub → Connect | Connect → Authorize → Repository → Settings |
| Settings → GitHub → Reconnect | Connect → Authorize → Settings when the previous repository remains accessible; otherwise Repository → Settings |

`settings.json` gains optional boolean `SetupStarted` and `SetupCompleted` fields in schema 1. Absent fields mean false. The existing safe read/update/flush/rename boundary preserves unrelated fields and rejects damaged/future documents. Completing setup retries persistence of the current selected folder before saving completion. A failed completion write keeps the user on Folder with an actionable notice. Interrupted setup uses securely stored account/repository selection and folder preferences; no code, pending authentication or secret is stored in settings. Setup completion remains separate from synchronization success. Settings connection changes never reset completion or force a completed user into onboarding.

With no developer client ID, Connect explains that the build needs configuration and offers Inspect Local Saves. This does not claim setup completion. Developer configuration remains in ignored `.local.env`; it is never displayed as an editable product setting.

## Browser authorization and repository management

The existing GitHub device flow runs unchanged. Once a code arrives on the UI thread, the desktop opens the validated HTTPS GitHub authorization address using Avalonia's native launcher. The authorization page shows the code, Copy Code, Open GitHub, expiry, progress and Cancel. Only the temporary user code is copied through the native clipboard. Browser/clipboard failures show recovery text; the address/code remain selectable. Terminal failures show Retry and Cancel. Authorization success loads account/repository metadata and routes automatically; Settings reconnect reselects an accessible previous repository through the existing fresh validation/storage boundary.

The repository list displays name, owner and Private, with a single selected row. Continue revalidates privacy/access and securely saves selection before navigating. Create Private Repository opens GitHub's creation page; Manage GitHub Access opens installed-app settings. Inline help explains installation links, organization approval, private creation and returning to refresh. With an empty eligible list the UI says **No available repositories**, explaining both creation and granting access; metadata cannot establish that the account has no repositories. A previously stored selection that no longer matches current metadata is explicitly flagged.

After selection is saved, task 6 checks content without writing: empty repository, existing verified PD2 Cloud collection, unrelated content or incompatible managed data. The cloud card reports the classification or a safe unavailable notice. Unrelated content requires future adoption confirmation, and incompatible content blocks comparison. Selection never initializes or overwrites a repository. Exact schema and verification are in [cloud reads](cloud-reads.md).

Settings contains General, GitHub and Saves. GitHub shows account, selected repository, current connection result and session-local last successful metadata check. Check Connection, Change Repository, Reconnect, Disconnect, Open in Browser and Manage GitHub Access share the existing services. Disconnect requires an owned modal dialog with Keep Connected as the default; only explicit confirmation removes the local native credential entry. Local settings/files and the remote repository remain intact. Remote revocation is separate.

## Folder and dashboard boundaries

The native picker and generic opaque-file scanner are reused. Continue requires a complete valid inspection, even for empty folders. Empty folders explain future downloading; populated folders explain that comparison must precede synchronization. No profiles, format parsing or extension rules are introduced.

The dashboard shows connection, repository, folder and local files. Its status is **Not yet synchronized** when inspection and selection are valid, otherwise **Needs attention**. No Synced state is fabricated. Sync All is visibly disabled with an availability explanation. Dashboard Refresh runs independent local inspection and account/repository metadata checks, followed by pinned cloud verification. A cloud card reports the checked revision and advisory matching-files/initial-upload/initial-download/conflict result only for verified managed inventories; empty uninitialized, unrelated and incompatible repositories never fabricate comparison inputs. Retry Inspection and Settings → Saves use local inspection only.

Changing repositories clears the displayed verified choice while validation runs. No comparison result or successful-sync status is retained for a new pairing. A fresh advisory comparison uses current local inspection and task-6 verified cloud inputs; Core comparison is unchanged. Metadata/account/selection operations cancel and invalidate cloud evidence. Startup shows a returning dashboard without waiting for cloud reads. Check Cloud Files, Cancel Cloud Check and independent local Retry Inspection remain available. No account/setup/refresh action writes source files or repository contents.

## Verification and remaining acceptance

The figures below record the original UX follow-up. Task-6 implementation and verification are recorded in [cloud reads](cloud-reads.md).

All 143 tests pass on macOS (16 new workflow/preference cases). The standard Release build passes with zero warnings/errors; formatting and `git diff --check` pass. Behavior tests cover new/interrupted/completed setup, successful authorization destinations, Settings connect/reconnect/change, cancellation, offline restart, missing folders, empty eligible lists and refresh, failed selection/completion persistence, unknown-field preservation and disconnect confirmation. Existing authentication/scanner/comparison/settings tests continue to apply.

Real MainWindow rendering with scripted HTTP and an in-memory test credential store was inspected on macOS at normal and minimum sizes. The temporary harness and captures live at `/private/tmp/pd2-setup-ui/`; they are not product dependencies or live authentication evidence. Native rendering requires execution outside the sandbox on this Mac. Live browser/clipboard/picker interaction, installed-app navigation and device approval still require manual acceptance, as do Windows/Linux native behavior and signed macOS secure storage. No live GitHub authorization, remote writes, hosted CI or cross-platform execution is claimed.

Temporary harness builds encountered an inaccessible NuGet vulnerability cache and intermittent MSBuild worker exits. A single-worker harness build followed by running the built executable outside the sandbox completed rendering successfully. No dependency/audit/telemetry configuration was changed.
