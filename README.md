# PD2 Cloud

PD2 Cloud is a generic folder synchronization engine. All regular files within the selected directory are treated as opaque binary data. The current application provides local inspection, GitHub App connection/private repository selection, verified cloud reads, advisory whole-collection comparison, an explicit reviewed initial upload (including explicit, user-confirmed repository initialization/adoption) with read-only publication recovery, and an explicit reviewed **Resolve** flow for real local/cloud differences: pull the verified cloud revision after a complete local backup, or push the local snapshot as a replacement child commit. Automatic merge, per-file conflict choices and version history remain gated.

First-time setup guides you through **Get Started → Connect with GitHub → Select Repository → Find Your Saves → Dashboard**. Interrupted setup resumes from the saved account/repository/folder progress; completed setup opens the dashboard, including when GitHub is offline. **Settings → GitHub** manages the account and repository; **Settings → Saves** changes or reinspects the local folder. Files are treated as opaque data. Inspection success does not mean files are synchronized with GitHub. See the [setup UX contract](docs/github-setup-ux.md).

The window is one shell: a title bar with the app name and the current page action, a **Saves**/**Settings** sidebar, and dark card pages. **Your Saves** shows the repository, the reviewed publication status, the primary sync/refresh actions and tiles into the read-only **Cloud Collection** and **Save Folder** file views. Those views list the inspected files by name, type, size and last-modified time, with inspection details (validation issues, SHA-256 hashes, collection fingerprint) behind a disclosure. Settings keeps the General/GitHub/Saves tabs. Every file view is read-only and never modifies source files.

## Development

Install [mise](https://mise.jdx.dev/getting-started.html), then run:

```sh
mise trust
mise install
mise run run
```

A desktop session is required. .NET `10.0.401` and Avalonia `12.1.4` are pinned.

```sh
mise run build
mise run format:check
mise run test
```

`mise.toml` opts out of .NET and Avalonia build-time telemetry. This avoids Avalonia statistics logging failures in restricted environments. When invoking `dotnet` outside mise, set `AVALONIA_TELEMETRY_OPTOUT=1` in that environment too.

`mise run format` applies C# formatting. CI builds, tests and checks formatting on Windows, macOS and Linux. Tests use synthetic binary files; no game saves are required.

## Layout

- `src/PD2Cloud.Core`: generic manifests, pure collection comparison, validation and future baseline models; scanner interface and portable path rules.
- `src/PD2Cloud.Local`: recursive inspection, read-only hashing, folder settings, local capture/backup/recovery preparation and successful-sync state persistence in application data.
- `src/PD2Cloud.GitHub`: device authorization, serialized token refresh, private repository metadata selection, bounded Git object reads and internal non-forced publication primitives.
- `src/PD2Cloud.Remote`: pinned Git verification, repository classification and coordinated initial-upload/recovery, bridging GitHub/Core/Local.
- `src/PD2Cloud.Platform`: native Keychain, Credential Manager and Linux Secret Service credential adapters.
- `src/PD2Cloud.Desktop`: Avalonia views, independent inspection/connection view models and native folder picker.
- `tests/PD2Cloud.Tests`: comparison decision tables, filesystem and view-model behavior tests.

See [folder inspection](docs/folder-inspection.md), [verified cloud reads](docs/cloud-reads.md), the [implementation plan](docs/implementation-plan.md) and the [delivery tracker](docs/delivery-plan.md) for rules, limitations and remaining work. Folder selection is saved in platform application data and restored with a fresh read-only inspection on restart. Settings notices explain restoration or save failures. GitHub access stays separate from filesystem inspection; desktop transfers remain unimplemented. Successful-sync persistence now stores the pinned remote revision, verified collection fingerprint and local/remote pairing.

Core compares complete baseline/local/verified-remote inventories. Disjoint edits still conflict; missing baseline files require review even when both sides match. Unknown remote state never means empty. Task 6 connects verified managed collections to advisory desktop comparison without a baseline. Matching files do not establish successful synchronization. Empty uninitialized, unrelated and incompatible repositories cannot supply a fabricated verified collection. See the [comparison contract](docs/collection-comparison.md) and [remote storage format and limits](docs/cloud-reads.md).

Task 7 adds [immutable capture, verified complete backups and durable recovery journals](docs/backups-recovery.md) through a local-only API. It serializes preparation/assessment across store instances and cooperating processes, preserves all evidence and blocks interrupted, corrupt or changed-source reuse. The reviewed upload is the only desktop path that invokes it. No source replacement, automatic restore or baseline fabrication exists.

Task 8 adds the [safe-upload publication engine](docs/safe-upload.md), its explicit [repository initialization/adoption](docs/repository-bootstrap.md) and its explicit desktop reviewed-upload/recovery integration. **Sync All** opens a review that shows exactly what will be published and then reports exactly what happened, but it is enabled only for a populated local folder into an already verified empty managed collection or an explicitly reviewed bootstrap case (unborn, empty tree or unrelated) with no existing baseline or retained evidence. It binds repository/branch/pinned head and direction, revalidates bytes, records durable candidate/receipt evidence and performs one non-forced branch update (for an unborn repository it first seeds the initial commit and branch through the Contents API), recovering lost responses without replay. No implicit initialization/adoption exists. Successful-sync persistence stores the pinned remote revision, fingerprint and pairing only after publication, and the saved revision's ancestry is verified before it is trusted. **Publication is never reported as synchronization success.**

When the verified cloud collection and the local folder differ, the dashboard offers **Resolve**. Pull re-reads the exact reviewed revision, writes a complete retained backup under application data, installs only the verified bytes, verifies the result and records the revision; a failed or cancelled install attempts to restore the backup. Push prepares another immutable local capture and replaces the managed cloud collection as a non-forced child commit while preserving every unrelated repository entry. Neither path merges files or chooses individual winners. See the [resolve contract](docs/resolve-differences.md).

The source repository is public. Storage selection requires a separate private repository.

## GitHub connection

Follow the [five-step GitHub App setup guide](docs/github-connection.md#create-and-connect-a-github-app-in-five-steps) to register, install and connect the app.

Create `.local.env` in the project root using [.local.env.example](.local.env.example), and set `PD2CLOUD_GITHUB_CLIENT_ID` to your own GitHub App public Client ID. Mise loads this optional file for development runs; `.local.env` is ignored by Git and no personal Client ID is supplied by tracked configuration. Restart after editing. Downloaded release artifacts embed a public Client ID and need no configuration; a launch outside mise can also supply the variable separately, and the environment value takes precedence over the embedded one. The desktop does not parse dotenv files itself. Local inspection works with the value blank. The registered app must enable device flow, request Contents read/write and Metadata read, and be installed on the chosen private repository. Never configure an app secret, private key or PAT.

Use **Connect with GitHub** to open authorization in your default browser. **Copy Code** and **Open GitHub** are available while waiting. Repository selection lists eligible private repositories; **Continue** rechecks access and saves the choice securely, then checks its contents without writing. **Create Private Repository**, **Manage GitHub Access**, and **Refresh Repositories** support setup on GitHub. Settings reuses the same screens for connecting, reconnecting and changing repositories. **Disconnect** requires confirmation and removes only this client's stored authorization and repository choice; remote revocation is separate in GitHub settings. Dashboard **Refresh** inspects local files, checks connection/repository metadata and verifies pinned cloud contents. **Cancel Cloud Check** leaves local inspection available. Incompatible data blocks comparison. **Sync All** opens a reviewed step when the cloud collection is empty and verified: an initial upload for a managed-empty collection, or an explicit **Initialize Repository** / **Create First Collection** / **Review Adoption** step for an unborn, empty-tree or unrelated repository (adoption requires an explicit acknowledgement and preserves every unrelated entry). It never replaces local files. Interrupted, lost or concurrent uploads are handled on the read-only **Review Upload Recovery** screen. No local or repository contents are otherwise written.

Credentials use macOS Keychain, Windows Credential Manager or Linux Secret Service (`secret-tool`, a session bus and available/unlocked service required), with no plaintext fallback. Native storage failure blocks connection while inspection remains available. See [GitHub setup, security boundaries and acceptance gates](docs/github-connection.md). To start fresh locally, remove both the application-data directory and the native credential; see [resetting local application state](docs/local-state-reset.md). Live authentication and repository acceptance remain unverified. A disposable macOS Keychain round trip and scripted real-window rendering passed; Windows/Linux native acceptance remains open.

PD2 Cloud is not affiliated with Blizzard Entertainment or Project Diablo 2.
