# PD2 Cloud

PD2 Cloud is a generic folder synchronization engine. All regular files within the selected directory are treated as opaque binary data. The current application provides local inspection, GitHub App connection/private repository selection, verified cloud reads and advisory whole-collection comparison. The task-8 publication engine is available as an explicit API; desktop transfers and version history remain gated.

First-time setup guides you through **Get Started → Connect with GitHub → Select Repository → Find Your Saves → Dashboard**. Interrupted setup resumes from the saved account/repository/folder progress; completed setup opens the dashboard, including when GitHub is offline. **Settings → GitHub** manages the account and repository; **Settings → Saves** changes or reinspects the local folder. Files are treated as opaque data. Inspection success does not mean files are synchronized with GitHub. See the [setup UX contract](docs/github-setup-ux.md).

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

Task 7 adds [immutable capture, verified complete backups and durable recovery journals](docs/backups-recovery.md) through a local-only API. It serializes preparation/assessment across store instances and cooperating processes, preserves all evidence and blocks interrupted, corrupt or changed-source reuse. The desktop does not invoke this API yet; Sync All stays disabled. No source replacement, automatic restore or baseline persistence exists.

Task 8 adds the [safe-upload publication engine](docs/safe-upload.md): explicit repository/branch/pinned-head binding, byte revalidation, durable candidate/receipt records, non-forced publication and lost-response/concurrent-update recovery. It supports only a populated capture into an already verified empty managed collection. No implicit initialization/adoption exists. Successful-sync persistence now stores the pinned remote revision, verified collection fingerprint and local/remote pairing. Journal/baseline reconciliation persists the sync state after a successful upload so the recovery notes and the "last uploaded" note match. **Sync All remains disabled** while baseline ancestry and desktop recovery/review integration remain open. Publication is never reported as successful synchronization.

The source repository is public. Storage selection requires a separate private repository.

## GitHub connection

Follow the [five-step GitHub App setup guide](docs/github-connection.md#create-and-connect-a-github-app-in-five-steps) to register, install and connect the app.

Create `.local.env` in the project root using [.local.env.example](.local.env.example), and set `PD2CLOUD_GITHUB_CLIENT_ID` to your own GitHub App public Client ID. Mise loads this optional file for development runs; `.local.env` is ignored by Git and no personal Client ID is supplied by tracked configuration. Restart after editing. Launches outside mise need that environment variable supplied separately; the desktop does not parse dotenv files itself. Local inspection works with the file missing or the value blank. The registered app must enable device flow, request Contents read/write and Metadata read, and be installed on the chosen private repository. Never configure an app secret, private key or PAT.

Use **Connect with GitHub** to open authorization in your default browser. **Copy Code** and **Open GitHub** are available while waiting. Repository selection lists eligible private repositories; **Continue** rechecks access and saves the choice securely, then checks its contents without writing. **Create Private Repository**, **Manage GitHub Access**, and **Refresh Repositories** support setup on GitHub. Settings reuses the same screens for connecting, reconnecting and changing repositories. **Disconnect** requires confirmation and removes only this client's stored authorization and repository choice; remote revocation is separate in GitHub settings. Dashboard **Refresh** inspects local files, checks connection/repository metadata and verifies pinned cloud contents. **Cancel Cloud Check** leaves local inspection available. Unrelated repositories require future adoption confirmation; incompatible data blocks comparison. **Sync All** remains disabled; no source or repository contents are written.

Credentials use macOS Keychain, Windows Credential Manager or Linux Secret Service (`secret-tool`, a session bus and available/unlocked service required), with no plaintext fallback. Native storage failure blocks connection while inspection remains available. See [GitHub setup, security boundaries and acceptance gates](docs/github-connection.md). Live authentication and repository acceptance remain unverified. A disposable macOS Keychain round trip and scripted real-window rendering passed; Windows/Linux native acceptance remains open.

PD2 Cloud is not affiliated with Blizzard Entertainment or Project Diablo 2.
