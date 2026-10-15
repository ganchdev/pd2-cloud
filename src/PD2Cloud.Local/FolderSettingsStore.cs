using System.Text.Json;
using System.Text.Json.Nodes;
using PD2Cloud.Core;

namespace PD2Cloud.Local;

/// <summary>Updates only owned settings fields; unsupported or damaged documents are never overwritten.</summary>
public sealed class FolderSettingsStore(string? applicationDirectory = null) : IFolderSettingsStore
{
    private readonly string directory = Path.GetFullPath(applicationDirectory ?? ApplicationDataPaths.DirectoryPath);
    private readonly SemaphoreSlim gate = new(1, 1);
    private string SettingsPath => Path.Combine(directory, "settings.json");

    public bool IsSourceAllowed(string path) => Path.IsPathFullyQualified(path) && ApplicationDataPaths.IsSourceAllowed(path, directory);

    public async Task<FolderSettingsResult> LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    var document = Read();
                    var folder = document["SelectedFolder"]?.GetValue<string>();
                    if (folder is not null && (!Path.IsPathFullyQualified(folder) || !ApplicationDataPaths.IsSourceAllowed(folder, directory)))
                        return new FolderSettingsResult(null, "Saved folder is unsafe or invalid. Choose another folder.");
                    return new FolderSettingsResult(folder);
                }
                catch (Exception ex) when (IsSettingsError(ex))
                {
                    return new FolderSettingsResult(null, "Could not restore folder settings. Choose a folder; existing settings have been kept.");
                }
            });
        }
        finally { gate.Release(); }
    }

    public async Task SaveFolderAsync(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !ApplicationDataPaths.IsSourceAllowed(path, directory))
            throw new ArgumentException("Source overlaps application data.", nameof(path));
        await gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                var document = Read();
                document["SchemaVersion"] = 1;
                document["SelectedFolder"] = Path.GetFullPath(path);
                Directory.CreateDirectory(directory);
                CheckStoragePath();
                var temporary = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        JsonSerializer.Serialize(stream, document, new JsonSerializerOptions { WriteIndented = true });
                        stream.Flush(flushToDisk: true);
                    }
                    CheckStoragePath();
                    File.Move(temporary, SettingsPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            });
        }
        finally { gate.Release(); }
    }

    private JsonObject Read()
    {
        CheckStoragePath();
        if (!File.Exists(SettingsPath)) return new JsonObject();
        var document = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject
            ?? throw new JsonException("Settings must be an object.");
        // Missing version and version zero are older preference documents. Preserve unknown fields.
        var version = document["SchemaVersion"]?.GetValue<int>() ?? 0;
        if (version is < 0 or > 1) throw new JsonException("Unsupported settings version.");
        _ = document["SelectedFolder"]?.GetValue<string>();
        return document;
    }

    private void CheckStoragePath()
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked settings directories are unsupported.");
        if (new FileInfo(SettingsPath).LinkTarget is not null) throw new IOException("Linked settings are unsupported.");
        if (File.Exists(SettingsPath) && (File.GetAttributes(SettingsPath) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Settings must be a regular file.");
        if (Directory.Exists(SettingsPath)) throw new IOException("Settings path is a directory.");
    }

    private static bool IsSettingsError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException
        or InvalidOperationException or FormatException or ArgumentException or NotSupportedException;
}
