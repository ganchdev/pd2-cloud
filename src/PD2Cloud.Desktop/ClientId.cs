using System.Reflection;

namespace PD2Cloud.Desktop;

/// <summary>
/// Resolves the public GitHub App client ID used for the device flow.
/// </summary>
/// <remarks>
/// The environment variable wins so a development run can target a different
/// registration than the one embedded in a released artifact. The value is
/// public metadata, never a secret.
/// </remarks>
internal static class ClientId
{
    internal const string EnvironmentVariableName = "PD2CLOUD_GITHUB_CLIENT_ID";
    internal const string MetadataKey = "PD2Cloud.DefaultGitHubClientId";

    internal static string Resolve() => Choose(
        Environment.GetEnvironmentVariable(EnvironmentVariableName),
        Embedded(typeof(ClientId).Assembly));

    internal static string Choose(string? configured, string? embedded)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        return embedded?.Trim() ?? "";
    }

    private static string? Embedded(Assembly assembly) => assembly
        .GetCustomAttributes(typeof(AssemblyMetadataAttribute), inherit: false)
        .Cast<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == MetadataKey)?.Value;
}
