# PD2 Cloud

A desktop app for manually synchronizing Project Diablo 2 single-player saves through GitHub.

This first slice contains an Avalonia hello-world window. Save discovery, GitHub account connection and synchronization will follow the MVP specification.

The proposed architecture and milestone backlog are in the [implementation plan](docs/implementation-plan.md).

## Development

Install [mise](https://mise.jdx.dev/getting-started.html), then from this directory run:

```sh
mise trust
mise install
mise run run
```

The SDK is pinned to .NET `10.0.401` in both `mise.toml` and `global.json`. Avalonia packages are pinned to `12.1.4`.

The run command opens a native desktop window displaying **PD2 Cloud** and **Hello, world!**. A desktop session is required.

```sh
mise run restore
mise run build
mise run format:check
```

`mise run format` applies C# formatting. CI builds and checks formatting on Windows, macOS and Linux.

## Layout

```text
PD2Cloud.slnx
mise.toml
global.json
src/PD2Cloud.Desktop/
  App.axaml
  Program.cs
  ViewModels/
  Views/
```

The desktop uses views and view models so subsequent product logic can remain independent of the UI. Additional core, local storage, GitHub and platform projects will be added as their implementation begins.

## Scope and privacy

The current hello world does not access save directories, contact GitHub or store credentials. There is no implemented synchronization behavior yet.

PD2 Cloud is not affiliated with Blizzard Entertainment or Project Diablo 2.
