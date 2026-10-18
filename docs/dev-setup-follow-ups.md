# Development setup follow-ups

Developer TODO list for the local setup process. These are process/tooling gaps
found while setting the project up on Intel macOS, not product behavior. None of
them block building, running or the test suite.

## TODOs

- **`mise run` needs an experimental flag on older mise.** The README instructs
  `mise run run`, but mise `2024.8.15` refuses `mise run` until
  `mise settings set experimental true` is set. Either document this, raise the
  minimum mise version, or ship the tasks in a form that works without the flag.
- **Missing `.local.env` fails the mise dotenv load.** `mise.toml` sets
  `[env] _.file = ".local.env"`, so `mise install`/`mise run` error with
  `failed to parse dotenv file: ... No such file or directory` when the file is
  absent, even though the README calls it optional. Make the load tolerate an
  absent file, or make the README's first step copy `.local.env.example` before
  any `mise` command.
- **Intel macOS (`osx-x64`) `fstat` ABI bug (fixed).** Plain `fstat` on Darwin
  x86_64 uses the legacy 32-bit-inode ABI, so `st_mode` is at byte 8; reading it
  at byte 4 rejected every regular file with `Source is not a regular file` and
  failed 127 tests. `FileFingerprintReader` now calls `fstat$INODE64` on x64 macOS
  so every platform reads the 64-bit-inode layout. Kept here for history.
- **Source builds ship no GitHub client ID.** A local build embeds an empty
  `DefaultGitHubClientId`, so `Connect` stays disabled until the developer
  registers their own app and sets `PD2CLOUD_GITHUB_CLIENT_ID`. Consider a
  clearer first-run hint or a documented single-command dev bootstrap.
- **Plain `dotnet` outside mise needs telemetry opt-out.** Running `dotnet`
  directly (not via `mise run`) requires `AVALONIA_TELEMETRY_OPTOUT=1` and
  `DOTNET_CLI_TELEMETRY_OPTOUT=1` to avoid Avalonia statistics logging failures;
  `mise` sets these, but the dependency is easy to miss.
