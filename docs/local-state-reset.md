# Resetting local application state

PD2 Cloud keeps its local state in two places: an application-data directory and one native credential entry. Starting fresh means removing both. Doing only one leaves either stale setup/sync data or a saved GitHub authorization behind.

Resetting never modifies the selected save folder or the remote repository. It also does not revoke the GitHub App authorization on GitHub's side; do that separately in GitHub settings if desired.

## Local state inventory

The application-data directory is `Environment.SpecialFolder.LocalApplicationData/PD2Cloud` (see [folder inspection](folder-inspection.md) for the path rules):

- `%LOCALAPPDATA%\PD2Cloud` on Windows
- `~/Library/Application Support/PD2Cloud` on macOS
- `${XDG_DATA_HOME:-~/.local/share}/PD2Cloud` on Linux

It contains:

- `settings.json` — selected save folder, setup started/completed progress
- `local-sync-state.json` — the last sync record: repository, branch, local folder, cloud revision and the files both sides held at the last successful upload or download
- `instance.lock` — an empty file held exclusively while one desktop instance runs; it prevents a second launch from racing recovery storage
- `recovery/` — `operations.lock` plus scratch copies of the save files for the upload in progress; emptied after every upload and discarded by the next sync if a crash left anything behind
- `replacements/` — complete backups of the save folder taken before each download (the newest five are kept)

The GitHub credential envelope lives only in macOS Keychain, Windows Credential Manager or Linux Secret Service, never in these files (see [GitHub connection](github-connection.md)). Its native entry key is `PD2Cloud.GitHub.` plus the first 24 uppercase hex digits of SHA-256 over the public client ID; the macOS/Windows account label is `GitHub`.

## macOS

Quit the app, then remove the application-data directory and the Keychain entry:

```sh
rm -rf "$HOME/Library/Application Support/PD2Cloud"

security dump-keychain | grep -i "PD2Cloud.GitHub"     # find the service name
security delete-generic-password -s "PD2Cloud.GitHub.<hash>" -a GitHub
```

## Windows

Quit the app, delete `%LOCALAPPDATA%\PD2Cloud`, then remove the `PD2Cloud.GitHub.<hash>` entry in Credential Manager.

## Linux

Quit the app, delete `${XDG_DATA_HOME:-~/.local/share}/PD2Cloud`, then run:

```sh
secret-tool clear application "PD2Cloud.GitHub.<hash>"
```

## Preferred credential removal

Instead of deleting the native entry by hand, launch the app and use **Settings → GitHub → Disconnect**. Disconnect requires confirmation and removes only this client's stored authorization and repository choice, without network access. The file-based state still has to be deleted separately.

## Cautions

- **Download backups are destructive to discard.** `replacements/` holds copies of your save folder from before each download. Copy out anything you may still need before deleting it.
- Deleting `local-sync-state.json` forgets the last sync. If local and cloud files already match, the next check records the sync again; otherwise, when both sides have files and differ, Sync treats them as a conflict and asks which copy to keep.
- Do not delete only `settings.json` to "restart setup": a leftover `local-sync-state.json` can still describe a prior pairing. Use the full reset above.

## Expected result

Launching a downloaded executable after a complete reset opens **Get Started** with no saved account, repository, folder, setup progress, sync record or backups. Optional GitHub App configuration is unchanged: release artifacts embed the public Client ID and need no local configuration (see the README's GitHub connection section).
