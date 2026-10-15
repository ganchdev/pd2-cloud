# PD2 Cloud

PD2 Cloud is a generic folder synchronization engine. All regular files within the selected directory are treated as opaque binary data. GitHub-backed synchronization and version history are planned; the current application provides read-only local inspection.

Choose **Select Folder** to inspect any physical local folder, including its subdirectories. **Refresh** reflects added, changed and removed files. File sizes, SHA-256 hashes and filesystem issues are available under **Inspection Details**. Inspection success does not mean files are synchronized with GitHub.

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
- `src/PD2Cloud.Local`: recursive inspection, read-only hashing and versioned folder settings in application data.
- `src/PD2Cloud.Desktop`: Avalonia views, view model and native folder picker.
- `tests/PD2Cloud.Tests`: comparison decision tables, filesystem and view-model behavior tests.

See [folder inspection](docs/folder-inspection.md), the [implementation plan](docs/implementation-plan.md) and the [delivery tracker](docs/delivery-plan.md) for rules, limitations and remaining work. Folder selection is saved in platform application data and restored with a fresh read-only inspection on restart. Settings notices explain restoration or save failures. No authentication, remote access, baseline persistence or synchronization is implemented.

Task 4 adds a pure Core comparison API for complete baseline/local/verified-remote inventories. It reports no transfer, initial upload/download, one-sided upload/download, whole-collection conflict, missing-file review or a blocking reason. Disjoint edits still conflict; missing baseline files require review even when both sides match. Unknown remote state never means empty. Comparison is not connected to the desktop and does not perform transfers or create a baseline. See the [comparison contract](docs/collection-comparison.md).

The source repository is public. Future save storage will use a separate private repository.

PD2 Cloud is not affiliated with Blizzard Entertainment or Project Diablo 2.
