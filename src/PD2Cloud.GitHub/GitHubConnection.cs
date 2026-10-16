using System.Text.Json;

namespace PD2Cloud.GitHub;

// All credential mutation and rotation share one gate, including disconnect.
public sealed class GitHubConnection(GitHubApi api, ICredentialStore store, TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public bool IsConfigured => api.IsConfigured;

    public async Task ConnectAsync(Action<DeviceAuthorization> showCode, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Verify native storage is reachable before starting authorization. No secret is cached in memory.
            _ = await store.ReadAsync(ct).ConfigureAwait(false);
            var credentials = await api.AuthorizeAsync(showCode, ct).ConfigureAwait(false);
            _ = await api.AccountAsync(credentials.AccessToken, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await SaveAsync(credentials, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<ConnectionOverview?> LoadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var credentials = await ReadAsync(ct).ConfigureAwait(false);
            if (credentials is null) return null;
            credentials = await CurrentAsync(credentials, ct).ConfigureAwait(false);
            var account = await api.AccountAsync(credentials.AccessToken, ct).ConfigureAwait(false);
            var repositories = await api.RepositoriesAsync(credentials.AccessToken, ct).ConfigureAwait(false);
            // Stored selection is only displayed as valid after fresh metadata validation.
            var selected = repositories.SingleOrDefault(r => r.Id == credentials.Repository?.Id && r.FullName == credentials.Repository.FullName && r.DefaultBranch == credentials.Repository.DefaultBranch);
            return new(account, repositories, selected, credentials.Repository is not null && selected is null);
        }
        finally { gate.Release(); }
    }

    public async Task<RepositoryChoice> SelectAsync(long repositoryId, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var credentials = await ReadAsync(ct).ConfigureAwait(false) ?? throw new ConnectionException("Connect to GitHub first.");
            credentials = await CurrentAsync(credentials, ct).ConfigureAwait(false);
            var repositories = await api.RepositoriesAsync(credentials.AccessToken, ct).ConfigureAwait(false);
            var selected = repositories.SingleOrDefault(r => r.Id == repositoryId)
                ?? throw new ConnectionException("Repository is no longer private and writable through this app. Reload repositories.");
            credentials.Repository = selected;
            await SaveAsync(credentials, ct).ConfigureAwait(false);
            return selected;
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await store.DeleteAsync(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<ConnectionCredentials?> ReadAsync(CancellationToken ct)
    {
        var json = await store.ReadAsync(ct).ConfigureAwait(false);
        if (json is null) return null;
        ConnectionCredentials? credentials;
        try { credentials = JsonSerializer.Deserialize<ConnectionCredentials>(json); }
        catch (JsonException) { throw new ConnectionException("Stored GitHub credentials are invalid. Disconnect to remove them."); }
        if (credentials is null || credentials.SchemaVersion != 1 || credentials.ClientId != api.ClientId || string.IsNullOrWhiteSpace(credentials.AccessToken))
            throw new ConnectionException("Stored GitHub credentials do not match this app. Disconnect and connect again.");
        return credentials;
    }

    private async Task<ConnectionCredentials> CurrentAsync(ConnectionCredentials credentials, CancellationToken ct)
    {
        if (credentials.ExpiresAt is null || credentials.ExpiresAt > clock.GetUtcNow().AddMinutes(1)) return credentials;
        var refreshed = await api.RefreshAsync(credentials, ct).ConfigureAwait(false);
        // A rotated token must be durable before it is used. Storage failure blocks the operation.
        await SaveAsync(refreshed, ct).ConfigureAwait(false);
        return refreshed;
    }

    private Task SaveAsync(ConnectionCredentials credentials, CancellationToken ct) => store.WriteAsync(JsonSerializer.Serialize(credentials), ct);
}

public sealed record ConnectionOverview(string Account, IReadOnlyList<RepositoryChoice> Repositories, RepositoryChoice? Selected, bool SelectionUnavailable = false);
