# PD2 Cloud

PD2 Cloud keeps a save folder in sync with a private GitHub repository. All regular files within the selected directory are treated as opaque binary data. One **Sync** button uploads this PC's changes, downloads newer cloud saves (backing up the local folder first), sets up cloud storage the first time, and asks which copy to keep when both sides changed. See [how Sync works](docs/sync.md).

First-time setup guides you through **Get Started → Connect with GitHub → Select Repository → Find Your Saves → Dashboard**. Interrupted setup resumes from the saved account/repository/folder progress; completed setup opens the dashboard, including when GitHub is offline. **Settings → GitHub** manages the account and repository; **Settings → Saves** changes or reinspects the local folder. Files are treated as opaque data. Inspection success does not mean files are synchronized with GitHub. See the [setup UX contract](docs/github-setup-ux.md).

Only one PD2 Cloud instance runs at a time: launching a second shows an already-running notice, because uploads and downloads coordinate through a filesystem lease. Quit every window (Command-Q on macOS, not just closing it) before opening the app again.

The window is one shell: a title bar with the app name and the current page action, a **Saves**/**Settings** sidebar, and dark card pages. **Your Saves** shows the repository, the sync status (In sync / Unsynced), the **Sync** and **Refresh** actions and tiles into the read-only **Cloud Collection** and **Save Folder** file views. Those views list the inspected files by name, type, size and last-modified time, with inspection details (validation issues, SHA-256 hashes, collection fingerprint) behind a disclosure. Settings keeps the General/GitHub/Saves tabs. Every file view is read-only and never modifies source files.

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

- `src/PD2Cloud.Core`: generic manifests, the three-way sync planner, validation and the last-sync model; scanner interface and portable path rules.
- `src/PD2Cloud.Local`: recursive inspection, read-only hashing, folder settings, upload capture, download backups and last-sync persistence in application data.
- `src/PD2Cloud.GitHub`: device authorization, serialized token refresh, private repository metadata selection, bounded Git object reads and internal non-forced publication primitives.
- `src/PD2Cloud.Remote`: pinned Git verification, repository classification, upload and download, bridging GitHub/Core/Local.
- `src/PD2Cloud.Platform`: native Keychain, Credential Manager and Linux Secret Service credential adapters.
- `src/PD2Cloud.Desktop`: Avalonia views, independent inspection/connection view models and native folder picker.
- `tests/PD2Cloud.Tests`: sync decision table, filesystem, upload/download and view-model behavior tests, including a two-PC sync journey.

See [folder inspection](docs/folder-inspection.md), [verified cloud reads](docs/cloud-reads.md), [Sync](docs/sync.md), the [implementation plan](docs/implementation-plan.md) and the [delivery tracker](docs/delivery-plan.md) for rules and limitations. Folder selection is saved in platform application data and restored with a fresh read-only inspection on restart. Settings notices explain restoration or save failures.

Sync compares the local folder, the verified cloud collection and the files recorded at this PC's last sync: only local changes upload, only cloud changes download, and changes on both sides ask whether to keep this PC's saves or the cloud saves. Uploads are a single non-forced branch update, so a cloud copy that changed in the meantime is never overwritten. Downloads back up the whole local folder first, restore it on failure, and keep the newest five backups. Empty or unrelated repositories are set up by the first Sync; unrelated files are preserved. Unknown remote state never counts as empty, and incompatible PD2 Cloud data blocks Sync. There is no background sync and no per-file merge.

The source repository is public. Storage selection requires a separate private repository.

## GitHub connection

Follow the [five-step GitHub App setup guide](docs/github-connection.md#create-and-connect-a-github-app-in-five-steps) to register, install and connect the app.

Create `.local.env` in the project root using [.local.env.example](.local.env.example), and set `PD2CLOUD_GITHUB_CLIENT_ID` to your own GitHub App public Client ID. Mise loads this optional file for development runs; `.local.env` is ignored by Git and no personal Client ID is supplied by tracked configuration. Restart after editing. Downloaded release artifacts embed a public Client ID and need no configuration; a launch outside mise can also supply the variable separately, and the environment value takes precedence over the embedded one. The desktop does not parse dotenv files itself. Local inspection works with the value blank. The registered app must enable device flow, request Contents read/write and Metadata read, and be installed on the chosen private repository. Never configure an app secret, private key or PAT.

Use **Connect with GitHub** to open authorization in your default browser. **Copy Code** and **Open GitHub** are available while waiting. Repository selection lists eligible private repositories; **Continue** rechecks access and saves the choice securely, then checks its contents without writing. **Create Private Repository**, **Manage GitHub Access**, and **Refresh Repositories** support setup on GitHub. Settings reuses the same screens for connecting, reconnecting and changing repositories. **Disconnect** requires confirmation and removes only this client's stored authorization and repository choice; remote revocation is separate in GitHub settings. Dashboard **Refresh** inspects local files, checks connection/repository metadata and verifies pinned cloud contents without writing. **Cancel Cloud Check** leaves local inspection available. Only **Sync** writes local or repository contents.

Credentials use macOS Keychain, Windows Credential Manager or Linux Secret Service (`secret-tool`, a session bus and available/unlocked service required), with no plaintext fallback. Native storage failure blocks connection while inspection remains available. See [GitHub setup, security boundaries and acceptance gates](docs/github-connection.md). To start fresh locally, remove both the application-data directory and the native credential; see [resetting local application state](docs/local-state-reset.md). Live authentication and repository acceptance remain unverified. A disposable macOS Keychain round trip and scripted real-window rendering passed; Windows/Linux native acceptance remains open.

PD2 Cloud is not affiliated with Blizzard Entertainment or Project Diablo 2.
