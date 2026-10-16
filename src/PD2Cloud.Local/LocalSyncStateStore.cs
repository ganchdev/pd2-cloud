using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using PD2Cloud.Core;

namespace PD2Cloud.Local;

/// <summary>Versioned local store for successful-sync state. Persists only the pinned remote revision, verified collection fingerprint and local/remote pairing.</summary>
public sealed class LocalSyncStateStore
{
    private const int MaxSyncStateBytes = 64 * 1024;
    private readonly string directory;
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        RespectRequiredConstructorParameters = true
    };

    public LocalSyncStateStore(string? applicationDirectory = null)
    {
        directory = Path.GetFullPath(applicationDirectory ?? ApplicationDataPaths.DirectoryPath);
        path = Path.Combine(directory, "local-sync-state.json");
    }

    public async Task<(LocalSyncState? State, string? Notice)> LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            return LoadCore();
        }
        finally { gate.Release(); }
    }

    /// <summary>Loads the sync state and verifies that the saved commit is an ancestor of the current remote HEAD.</summary>
    public async Task<(LocalSyncState? State, string? Notice)> VerifyAncestryAsync(Func<string, Task<bool>> isAncestorOfRemoteHead)
    {
        await gate.WaitAsync();
        try
        {
            var (state, notice) = LoadCore();
            if (state is null) return (null, notice);
            if (!await isAncestorOfRemoteHead(state.LastSynchronizedCommit).ConfigureAwait(false))
                return (null, "Saved sync commit is not an ancestor of the current remote head. The sync state has been preserved for review.");
            return (state, null);
        }
        finally { gate.Release(); }
    }

    private (LocalSyncState? State, string? Notice) LoadCore()
    {
        try
        {
            if (!File.Exists(path)) return (null, null);
            var bytes = FileFingerprintReader.ReadBounded(path, MaxSyncStateBytes, CancellationToken.None);
            using var document = JsonDocument.Parse(bytes);
            CheckStoragePath();
            if (!document.RootElement.TryGetProperty("SchemaVersion", out var versionElement) || versionElement.GetInt32() is not 1)
                throw new JsonException("Unsupported sync state version.");
            var state = JsonSerializer.Deserialize<LocalSyncState>(bytes, JsonOptions)
                ?? throw new JsonException("Missing sync state.");
            if (state.SchemaVersion != 1 || string.IsNullOrEmpty(state.RepositoryIdentity)
                || string.IsNullOrEmpty(state.Branch) || string.IsNullOrEmpty(state.LastSynchronizedCommit)
                || string.IsNullOrEmpty(state.LocalRootPath) || state.Files is null
                || state.SynchronizedAt == default || string.IsNullOrEmpty(state.CollectionFingerprint))
                throw new JsonException("Invalid sync state.");
            return (state, null);
        }
        catch (Exception ex) when (IsSyncStateError(ex))
        {
            return (null, "Could not restore successful-sync state. Existing sync progress has been preserved.");
        }
    }

    public async Task SaveSyncStateAsync(LocalSyncState state)
    {
        if (state.SchemaVersion != 1) throw new ArgumentException("Sync state must have schema version 1.", nameof(state));
        if (string.IsNullOrEmpty(state.RepositoryIdentity)) throw new ArgumentException("Repository identity is required.", nameof(state));
        if (string.IsNullOrEmpty(state.Branch)) throw new ArgumentException("Branch is required.", nameof(state));
        if (string.IsNullOrEmpty(state.LastSynchronizedCommit)) throw new ArgumentException("Last synchronized commit is required.", nameof(state));
        if (string.IsNullOrEmpty(state.LocalRootPath)) throw new ArgumentException("Local root path is required.", nameof(state));
        if (state.Files is null) throw new ArgumentException("Files are required.", nameof(state));
        if (state.SynchronizedAt == default) throw new ArgumentException("Synchronized timestamp is required.", nameof(state));
        if (string.IsNullOrEmpty(state.CollectionFingerprint)) throw new ArgumentException("Collection fingerprint is required.", nameof(state));
        await gate.WaitAsync();
        try
        {
            SaveCore(state);
        }
        finally { gate.Release(); }
    }

    private void SaveCore(LocalSyncState state)
    {
        if (!Path.IsPathFullyQualified(state.LocalRootPath) || !ApplicationDataPaths.IsSourceAllowed(state.LocalRootPath, directory))
            throw new ArgumentException("Local root path overlaps application data.", nameof(state));
        CheckStoragePath();
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (bytes.Length > MaxSyncStateBytes) throw new IOException("Sync state exceeds the size limit.");
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            CheckStoragePath();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public bool IsSourceAllowed(string sourceRoot) => Path.IsPathFullyQualified(sourceRoot) && ApplicationDataPaths.IsSourceAllowed(sourceRoot, directory);

    private void CheckStoragePath()
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked sync state directories are unsupported.");
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Linked sync state file is unsupported.");
        if (File.Exists(path) && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Sync state must be a regular file.");
        if (Directory.Exists(path)) throw new IOException("Sync state path is a directory.");
    }

    private static bool IsSyncStateError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException
        or InvalidOperationException or FormatException or ArgumentException or NotSupportedException;
}
