namespace PD2Cloud.Core;

public sealed record FolderSettingsResult(string? SelectedFolder, string Notice = "", bool SetupCompleted = false, bool SetupStarted = false);

/// <summary>Non-secret preferences, independent of successful synchronization state.</summary>
public interface IFolderSettingsStore
{
    bool IsSourceAllowed(string path);
    Task<FolderSettingsResult> LoadAsync();
    Task SaveFolderAsync(string path);
    Task SaveSetupStartedAsync() => Task.FromException(new NotSupportedException("Setup preferences are unavailable."));
    Task SaveSetupCompletedAsync() => Task.FromException(new NotSupportedException("Setup preferences are unavailable."));
}
