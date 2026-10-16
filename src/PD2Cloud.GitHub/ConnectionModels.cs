namespace PD2Cloud.GitHub;

// This boundary has no dependency on inspection, manifests or local source paths.
public interface ICredentialStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(string secret, CancellationToken cancellationToken);
    Task DeleteAsync(CancellationToken cancellationToken);
}

public sealed record RepositoryChoice(long Id, string FullName, string DefaultBranch)
{
    public override string ToString() => FullName;
}

// Secrets intentionally have no generated record ToString(). Only the native store serializes them.
public sealed class ConnectionCredentials
{
    public int SchemaVersion { get; init; } = 1;
    public string ClientId { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string? RefreshToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? RefreshExpiresAt { get; init; }
    public RepositoryChoice? Repository { get; set; }
}

public sealed record DeviceAuthorization(string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt);
public sealed class ConnectionException(string message) : Exception(message);
