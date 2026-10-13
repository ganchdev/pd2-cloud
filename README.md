# PD2 Cloud

A desktop app for manually synchronizing Project Diablo 2 single-player saves through GitHub.

Select a local folder to inspect filenames and byte sizes, then Refresh after external changes. This is read-only folder inspection; save validation and synchronization come later.

The proposed architecture and milestone backlog are in the [implementation plan](docs/implementation-plan.md).

## Development

Install [mise](https://mise.jdx.dev/getting-started.html), then from this directory run:

```sh
mise trust
mise install
mise run run
```

The SDK is pinned to .NET `10.0.401` in both `mise.toml` and `global.json`. Avalonia packages are pinned to `12.1.4`.

The run command opens the desktop app. Choose **Select folder…**, select a physical folder, then use **Refresh**. A desktop session is required.

```sh
mise run restore
mise run build
mise run format:check
mise run test
```

`mise run format` applies C# formatting. CI builds, tests and checks formatting on Windows, macOS and Linux.

## Layout

- `src/PD2Cloud.Core`: inspection models and interface.
- `src/PD2Cloud.Local`: filesystem metadata inspection on a worker thread.
- `src/PD2Cloud.Desktop`: Avalonia views, view model and native picker adapter.
- `tests/PD2Cloud.Tests`: filesystem and view-model behavior tests.

## Scope and privacy

Inspection lists direct children, including unrecognized files, without opening file contents or changing source files. Subfolders are not inspected. Links are flagged and skipped; selected folders with linked ancestors are rejected—select their physical location. Refresh discards superseded results.

Metadata is a point-in-time listing, not a stable or validated save collection. Folder selection is not persisted. Profile detection, hashing, GitHub and synchronization are not implemented. Native picker/UI checks on macOS, Windows and Linux remain pending; automated Unix permission/link tests do not verify Windows ACLs or junctions.

PD2 Cloud is not affiliated with Blizzard Entertainment or Project Diablo 2.
