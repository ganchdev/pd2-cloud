using Avalonia.Platform.Storage;

namespace PD2Cloud.Desktop.Services;

public sealed class NativeFolderPicker(IStorageProvider storageProvider) : IFolderPicker
{
    public async Task<string?> PickAsync()
    {
        if (!storageProvider.CanPickFolder)
            throw new InvalidOperationException("Folder selection is unavailable in this desktop session.");

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a save folder to inspect",
            AllowMultiple = false
        });
        try
        {
            if (folders.Count == 0)
                return null;
            return folders[0].TryGetLocalPath()
                ?? throw new InvalidOperationException("Select a local filesystem folder.");
        }
        finally
        {
            foreach (var folder in folders)
                folder.Dispose();
        }
    }
}
