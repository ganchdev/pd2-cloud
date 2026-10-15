namespace PD2Cloud.Local;

/// <summary>Boundary for future configuration and successful-sync persistence; never writes inside inspected folders.</summary>
public static class ApplicationDataPaths
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PD2Cloud");
    public static string LocalSyncStatePath => Path.Combine(DirectoryPath, "local-sync-state.json");
}
