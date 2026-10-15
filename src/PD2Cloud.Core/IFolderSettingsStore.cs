namespace PD2Cloud.Core;

public sealed record FolderSettingsResult(string? SelectedFolder, string Notice = "");

/// <summary>Non-secret preferences, independent of successful synchronization state.</summary>
public interface IFolderSettingsStore
{
    bool IsSourceAllowed(string path);
    Task<FolderSettingsResult> LoadAsync();
    Task SaveFolderAsync(string path);
}
