# GitHub connection

Current task-8 extension: [safe upload](safe-upload.md) adds a coordinated initial-upload/publication-recovery API with repository/branch/pinned-head binding and durable candidate records. The desktop remains read-only with Sync All disabled. Successful-sync persistence and baseline ancestry remain unimplemented; publication does not establish synchronization success. Earlier task-specific transfer limitations below describe their original delivery boundary. Live GitHub and native acceptance remain open.

Task 5 implementation contract, updated 2026-10-16. The [setup UX contract](github-setup-ux.md) describes the shared onboarding/Settings journey and supersedes the original single-panel presentation. GitHub access is independent of local inspection and pure Core comparison. It connects an account and selects repository metadata only. Task 6 adds [verified cloud reads](cloud-reads.md) through a separate Remote bridge. Transfers, baseline persistence and synchronization are not implemented. The remaining task-5 sections below describe the authentication/metadata boundary unless explicitly extended by that contract.

## Configuration and onboarding

### Create and connect a GitHub App in five steps

1. Open [New GitHub App](https://github.com/settings/apps/new) under your GitHub account. You can also navigate through **Settings → Developer settings → GitHub Apps → New GitHub App**.

2. Configure the registration, then click **Create GitHub App**:

   - **GitHub App name:** a unique name, such as `PD2 Cloud by ganchdev`.
   - **Homepage URL:** `https://github.com/ganchdev/pd2-cloud`.
   - **Enable Device Flow:** checked.
   - **Expire user authorization tokens:** checked.
   - **Request user authorization (OAuth) during installation:** unchecked.
   - **Callback URL** and **Setup URL:** leave blank.
   - **Webhook → Active:** unchecked; no webhook URL is needed.
   - **Repository permissions → Contents:** **Read & write**.
   - **Repository permissions → Metadata:** **Read-only**. Leave other permissions unset.
   - **Where can this GitHub App be installed?:** **Only on this account** for personal testing, or **Any account** when sharing it with other users.

   Skip client-secret and private-key generation. The desktop device flow and its token refresh use the public Client ID without either secret. See GitHub's [registration guide](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/registering-a-github-app) and [device-flow refresh requirements](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens).

3. Create a separate **private repository** for storage, initially using disposable test data. Install the GitHub App with access to that repository using the [repository installation and permissions steps](#install-the-app-on-your-repository-and-grant-permissions) below. The public `ganchdev/pd2-cloud` source repository is not storage. Organization policies may require approval.

4. Copy your GitHub App's **Client ID** (not its numeric App ID) from its settings. In the project root, copy `.local.env.example` to `.local.env` if you do not already have a local file, then edit its value:

   ```dotenv
   PD2CLOUD_GITHUB_CLIENT_ID=your-public-GitHub-App-client-ID
   ```

   This is the only runtime GitHub setting. Do not add `PD2CLOUD_GITHUB_CLIENT_SECRET`, a private key, PAT or access/refresh token. The planned desktop synchronization flow also uses user tokens, so it does not require a client secret. Tokens are obtained through authorization and stored in the native credential facility.

5. From the project root, run `mise run run` (restart if already running). Choose **Get Started → Connect with GitHub**. Your default browser opens GitHub; use **Copy Code** to enter the displayed code and approve access. Back in PD2 Cloud, choose your private repository and click **Continue**, then inspect your local save folder and continue to the dashboard. If it is missing, check the installation's repository access and permissions, then click **Refresh Repositories**.

App installation (step 3) and user authorization (step 5) are both required. Connection saves account credentials and repository selection; the shared workflow then runs task-6 read-only cloud inspection. Neither action synchronizes files or initializes repository contents. Native secure storage must be available; platform requirements are listed below.

`mise.toml` loads the optional project-root `.local.env` through `[env]` / `_.file = ".local.env"`, using [mise's dotenv support](https://mise.jdx.dev/environments/#env-file). The actual local file is ignored by Git; the tracked `.local.env.example` contains an empty value and no personal Client ID. Missing/blank configuration keeps local inspection available and GitHub Connect disabled. The existing local Client ID was moved out of tracked mise configuration without changing its value. The Client ID remains public metadata; this separation lets each developer use their own registration. Do not store client secrets, private keys, PATs or access/refresh tokens in this file.

Launches outside mise need `PD2CLOUD_GITHUB_CLIENT_ID` in their environment; the desktop only reads that environment variable and does not load dotenv files itself. Configuration alone does not establish successful live authentication or validate the app registration/installation.

Without a client ID, Connect is disabled with a setup notice; folder selection/inspection still work. With configuration, opening the window makes one connection restoration/metadata request sequence alongside independent local startup. The shared workflow also checks cloud contents after restoring a valid selection. There is no periodic refresh or synchronization.

Choose **Connect with GitHub**, open the displayed `https://github.com/login/device` address in a browser and enter the selectable user code. The app waits at least GitHub's polling interval before each request; pending continues, each slowdown adds five seconds, and denial, expiry or other authorization errors stop. Cancel stops polling; closing rejects late UI results. A completed native mutation finishes before the serialized operation releases its gate. Cancellation at that boundary may leave saved credentials; Reload verifies the state and Disconnect removes it.

Authorization validates the account before saving credentials. Failed storage never creates a session-only credential fallback. If authorization succeeded at GitHub but local storage failed/cancellation intervened, remote authorization may still exist: remove it separately in GitHub settings if desired.

**Refresh Repositories** fetches account, app installations and paginated repository metadata. It offers only private, nonarchived, enabled repositories with user push permission under nonsuspended installations granting Contents write. **Continue** repeats those checks before saving the numeric repository ID, full name and advertised default branch. Selection does not read/verify branch existence or contents; an empty repository is selectable without bootstrap. Changed name/default branch or lost access prevents displaying an old stored choice as verified on reload. Future cloud operations must independently verify identity/privacy/branch/access again.

**Disconnect**, after a modal confirmation, deletes this client's native credential entry, including selection. It works without a GitHub network request and preserves local folder settings and source files. A deletion failure remains visible; it does not claim success. During an operation choose Cancel first, then Disconnect when it finishes. Local disconnect does not revoke GitHub authorization. Changing the configured client ID uses a separate entry; to remove an old client's entry, restore its original ID and Disconnect.

## Install the app on your repository and grant permissions

Use these steps after creating the GitHub App and your private storage repository:

1. Open [GitHub App settings](https://github.com/settings/apps) and click **Edit** beside your app.
2. Open **Permissions & events**. Under **Repository permissions**, confirm **Contents → Read & write** and **Metadata → Read-only**. Save any changes.
3. In the left sidebar, click **Install App**, then click **Install** beside the account that owns your private storage repository.
4. Choose **Only select repositories**. In **Select repositories**, select the private repository you created for storage.
5. Review the requested permissions and click **Install** to approve them.
6. Return to PD2 Cloud. If not yet connected, click **Connect with GitHub** and approve the displayed device code in your browser. Click **Refresh Repositories**, choose the private repository, then click **Continue**.

If the app is **already installed**, open [Installed GitHub Apps](https://github.com/settings/installations), click **Configure** beside your app, add the repository under **Repository access**, and click **Save**. Then click **Refresh Repositories** in PD2 Cloud. For an organization-owned repository, manage the installation from that organization's settings; an owner may need to approve it.

If you increased the app's permissions after installing it, approve the pending permission update for that installation before retrying. Saving the app registration alone does not grant new permissions to existing installations.

If the repository still does not appear, confirm that it is private, enabled and not archived; that your GitHub user has write access; and that the installation includes it with Contents read/write permission. The account used for device authorization must have access to the repository. Selection does not upload files or create repository contents.

References: GitHub's [installation instructions](https://docs.github.com/en/apps/using-github-apps/installing-your-own-github-app) and [permission-update approval requirements](https://docs.github.com/en/apps/maintaining-github-apps/modifying-a-github-app-registration#changing-the-permissions-of-a-github-app).

## Credentials and HTTP boundaries

`PD2Cloud.GitHub` has no Core, Local, filesystem or Desktop dependency. `GitHubConnection` serializes credential operations and token rotation through one gate per service instance. Expiring device-flow tokens refresh without an app secret; both rotated tokens and expiry metadata must be saved before any metadata request uses the replacement. Expired/rejected refresh requires reauthorization. A crash or save failure after server-side rotation can invalidate the previously saved pair; reconnect to recover. There is no cross-process refresh coordination; use one desktop instance per client/account.

`PD2Cloud.Platform.NativeCredentialStore` stores a versioned JSON credential envelope in:

| Platform | Native facility |
|---|---|
| macOS | Keychain generic password, Security.framework APIs |
| Windows | Credential Manager generic credential, current-user local-machine persistence |
| Linux | Secret Service through libsecret's `secret-tool`; requires the executable, session bus and an available/unlocked native service |

The native entry key is `PD2Cloud.GitHub.` plus the first 24 uppercase hex digits of SHA-256 over the public client ID; account label is GitHub on macOS/Windows. One account/selection per client ID is supported. Tokens, refresh tokens, expiry metadata and repository selection remain inside that entry, never `settings.json` or a baseline file. No plaintext file, environment-token, Git CLI or memory-persistence fallback exists. Native denial/unavailability blocks connection operations while leaving local inspection usable. Managed strings cannot be reliably zeroed; temporary native/write byte buffers are cleared where owned. The desktop uses Avalonia’s native launcher for fixed HTTPS GitHub pages and its native clipboard API for the temporary user code. Browser/clipboard failures show a manual recovery notice. Secrets are never copied.

Production HTTP disables redirects, uses HTTPS fixed GitHub endpoints, a 30-second request timeout, a 4 MiB response bound, and no automatic retry of token rotation or rate-limit failures. Pagination constructs fixed-origin numbered requests rather than following arbitrary links. Lists are bounded to 100 pages per endpoint and 10,000 examined repositories per enumeration; reaching a bound fails rather than accepting partial metadata. No credentials or raw response/exception bodies are displayed or logged. Errors distinguish authorization, configuration, rate/access denial and terminal device-flow states; other transport/native errors show a safe retry notice.

OAuth device/token POSTs and metadata GETs remain the connection boundary. Task 6 adds only read endpoints for direct repository metadata, commit lists, exact references, commits, trees and blobs; no repository-content write API exists. Reads use the same credential/rotation gate. Local-only inspection and Retry Inspection never invoke this service. Dashboard Refresh checks local inspection and metadata, then verifies cloud contents through the Remote bridge. Listing/selection alone is never a verified inventory or sync permission. Only the bridge supplies complete verified managed inventories to advisory Core comparison; no successful baseline/upload is inferred.

Protocol references: [GitHub App user/device authorization](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app), [device-flow token refresh without a secret](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens), and [user-accessible installation repositories](https://docs.github.com/en/rest/apps/installations#list-repositories-accessible-to-the-user-access-token).

## Verification and remaining acceptance gates

Task-5 macOS validation: `mise run test` passed all 127 cases (30 new); `mise run build` passed with zero warnings/errors; `mise run format:check` and `git diff --check` passed. Initial NuGet vulnerability-cache warnings persisted even with escalation. A forced restore using `NUGET_HTTP_CACHE_PATH=/private/tmp/pd2-task5-nuget-cache` cleared them; subsequent standard mise commands passed without overrides. No package, project audit setting or telemetry setting was changed.

The local dotenv follow-up also passed all 127 tests, Release build (zero warnings/errors), formatting and diff checks. Direct mise checks verified existing-value preservation, missing/blank/custom files, personal-file exclusion and template inclusion. No runtime/UI code changed for this follow-up.

Behavior tests use scripted HTTP, injected credential stores and an advancing clock. They cover polling/slowdown, cancellation/expiry/denial, malformed tokens, safe errors, storage failure, serialized concurrent refresh, refresh expiry, pagination, repository privacy/write/install filters, revalidation before selection, restart selection, stale selection, disconnect failure/recovery, UI state/disposal and inspection while GitHub is stalled. They are not live authentication evidence.

A disposable unique macOS Keychain entry passed absent/read/write/replace/delete/repeated-delete checks using the production adapter outside the sandbox; the entry was removed. The initial sandbox attempt failed with Keychain status 100001. This establishes the adapter round trip from a temporary .NET harness on this Mac, not signed desktop identity/ACL or locked/denied-store behavior. An escalated build attempt hit an MSBuild worker failure; running the already-built harness succeeded.

The actual Avalonia MainWindow was rendered with scripted HTTP and an injected memory test store. Inspected unconfigured, device-code, cancelled, connected, selected, disconnected and storage-error states, including device/error at minimum size. Captures and harness are outside the repo at `/private/tmp/pd2-task5-ui/`. This is visual rendering evidence, not live account, native picker or pointer/keyboard/browser acceptance.

Remaining gates before task 5 can be accepted live:

1. Verify the registration and installation for the configured public client ID (or supply your own registration). Verify device approval/denial/expiry/cancel, installation scope and expiring-token rotation with a disposable account/private repository. No live authentication is claimed.
2. Verify real private/public/read-only/archived/revoked-install/organization-policy states, an empty repository, persisted selection across desktop processes and disconnect. Confirm repository tree/commit IDs remain unchanged throughout onboarding, startup and local Refresh.
3. Exercise native secure read/write/replace/delete, locked/unavailable service and user-denied access on Windows Credential Manager and Linux Secret Service, plus signed desktop Keychain identity/access prompts on macOS. Verify no fallback secret files and that disconnect removes only this app/client's entry. Windows/Linux execution has not been performed.
4. Verify native keyboard/pointer, browser code entry, cancellation/close and local inspection under offline/rate-limited GitHub conditions on the target platforms. No new hosted CI, packaging or multi-process acceptance is claimed.

Task 6 (verified pinned cloud reads) is implemented as described in [cloud reads](cloud-reads.md). No transfers, automatic merging/deletion, background synchronization or baseline persistence exist. Task-5 live/native acceptance above remains open.

## Task-7 local safety boundary

[Backups and recovery](backups-recovery.md) adds a local-only preparation/assessment API. It has no GitHub dependency and is not invoked by this desktop journey. Connection, cloud reads, startup and Refresh keep their existing behavior; Sync All remains disabled. No source/repository writes, initialization/adoption, baseline persistence, automatic restoration or background synchronization were enabled. Live GitHub and native acceptance gates above remain open.
