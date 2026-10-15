namespace PD2Cloud.Local;

/// <summary>Platform storage for preferences and future synchronization state, outside source folders.</summary>
public static class ApplicationDataPaths
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PD2Cloud");
    public static string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public static string LocalSyncStatePath => Path.Combine(DirectoryPath, "local-sync-state.json");

    // Reject both ancestors of app storage (even before it exists) and app-owned subdirectories.
    public static bool IsSourceAllowed(string source, string? applicationDirectory = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var owned = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory ?? DirectoryPath));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        static string Prefix(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
        return !root.Equals(owned, comparison) && !owned.StartsWith(Prefix(root), comparison)
            && !root.StartsWith(Prefix(owned), comparison);
    }
}
