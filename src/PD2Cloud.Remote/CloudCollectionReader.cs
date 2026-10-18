using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PD2Cloud.Core;
using PD2Cloud.GitHub;

namespace PD2Cloud.Remote;

public enum RepositoryContent { EmptyRepository, ManagedCollection, Unrelated, Incompatible }
public sealed record CloudReadResult(RepositoryChoice Repository, RepositoryContent Content, string? Commit,
    RemoteCollection? Collection, string Message);

/// <summary>Bridges authenticated Git objects to Core's explicit verified-inventory contract; never writes files.</summary>
public sealed class CloudCollectionReader(GitHubConnection connection)
{
    public async Task<CloudReadResult> ReadAsync(RepositoryChoice repository, CancellationToken ct) =>
        Verify(await connection.ReadRepositoryAsync(repository, ct).ConfigureAwait(false), ct);

    public static CloudReadResult Verify(RepositorySnapshot snapshot, CancellationToken ct = default)
    {
        CloudReadResult Result(RepositoryContent content, string message, RemoteCollection? collection = null) =>
            new(snapshot.Repository, content, snapshot.Commit, collection, message);
        try
        {
            ct.ThrowIfCancellationRequested();
            Require(snapshot.Entries.Count <= CloudStorage.MaxEntries);
            if (snapshot.Commit is null)
            {
                Require(snapshot.Tree is null && snapshot.Entries.Count == 0 && snapshot.Blobs.Count == 0);
                return Result(RepositoryContent.EmptyRepository, "Cloud storage is not set up yet. Sync sets it up and uploads your saves.");
            }
            Require(IsId(snapshot.Commit) && IsId(snapshot.Tree));
            VerifyTrees(snapshot, ct);
            var entries = snapshot.Entries;
            if (entries.Count == 0)
                return Result(RepositoryContent.EmptyRepository, "Cloud storage is not set up yet. Sync sets it up and uploads your saves.");
            var owned = entries.Where(e => RelativePaths.CollisionKey(e.Path.Split('/')[0]) == RelativePaths.CollisionKey(CloudStorage.Root)).ToArray();
            if (owned.Length == 0)
                return Result(RepositoryContent.Unrelated, "This repository has other files but no saves yet. Sync adds your saves alongside them; existing files are kept.");

            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in owned)
            {
                Require(RelativePaths.IsSafe(item.Path) && RelativePaths.Canonical(item.Path) == item.Path);
                Require(paths.TryAdd(RelativePaths.CollisionKey(item.Path), item.Path));
                Require(item.Type == "tree" && item.Mode == "040000" || item.Type == "blob" && item.Mode == "100644");
                Require(item.Path is CloudStorage.Root or CloudStorage.ManifestPath or CloudStorage.FilesPath
                    || item.Path.StartsWith(CloudStorage.FilesPath + "/", StringComparison.Ordinal));
            }
            Require(owned.SingleOrDefault(e => e.Path == CloudStorage.Root)?.Type == "tree");
            Require(owned.SingleOrDefault(e => e.Path == CloudStorage.FilesPath)?.Type == "tree");
            var manifestEntry = owned.SingleOrDefault(e => e.Path == CloudStorage.ManifestPath);
            Require(manifestEntry?.Type == "blob" && manifestEntry.Size <= CloudStorage.MaxManifestBytes);
            byte[] Bytes(GitTreeEntry entry, int limit)
            {
                Require(entry.Size >= 0 && entry.Size <= limit && snapshot.Blobs.TryGetValue(entry.Sha, out _));
                var bytes = snapshot.Blobs[entry.Sha];
                Require(bytes.LongLength == entry.Size && CloudStorage.BlobId(bytes) == entry.Sha);
                return bytes;
            }
            using var json = JsonDocument.Parse(Bytes(manifestEntry!, CloudStorage.MaxManifestBytes));
            var root = json.RootElement;
            Properties(root, "Format", "SchemaVersion", "Files", "CollectionFingerprint");
            Require(root.GetProperty("Format").GetString() == "PD2Cloud.Collection" && root.GetProperty("SchemaVersion").GetInt32() == 1);
            var fileList = root.GetProperty("Files");
            Require(fileList.ValueKind == JsonValueKind.Array && fileList.GetArrayLength() <= CloudStorage.MaxFiles);
            var files = new List<FileEntry>();
            long total = manifestEntry!.Size;
            foreach (var entry in fileList.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                Properties(entry, "RelativePath", "SizeBytes", "Sha256");
                var file = new FileEntry(entry.GetProperty("RelativePath").GetString()!, entry.GetProperty("SizeBytes").GetInt64(), entry.GetProperty("Sha256").GetString()!);
                Require(file.RelativePath is not null && RelativePaths.IsSafe(file.RelativePath) && file.RelativePath == RelativePaths.Canonical(file.RelativePath));
                Require(file.SizeBytes >= 0 && file.SizeBytes <= CloudStorage.MaxFileBytes);
                total += file.SizeBytes;
                Require(total <= CloudStorage.MaxCollectionBytes);
                files.Add(file);
            }
            files.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
            var manifest = new CollectionManifest(1, "remote", files.AsReadOnly(), root.GetProperty("CollectionFingerprint").GetString(), DateTimeOffset.UtcNow, true, []);
            var remote = new RemoteCollection(snapshot.Repository.Id.ToString(CultureInfo.InvariantCulture), snapshot.Repository.DefaultBranch, snapshot.Commit, manifest, RemoteVerification.Verified);
            // Reuse Core's canonical inventory, hash, collision and fingerprint validation.
            Require(CollectionInventory.IsValidManifest(manifest));
            var actual = owned.Where(e => e.Type == "blob" && e.Path.StartsWith(CloudStorage.FilesPath + "/", StringComparison.Ordinal)).ToDictionary(e => e.Path, StringComparer.Ordinal);
            Require(actual.Count == files.Count);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                Require(actual.TryGetValue(CloudStorage.FilesPath + "/" + file.RelativePath, out _));
                var item = actual[CloudStorage.FilesPath + "/" + file.RelativePath];
                Require(item.Size == file.SizeBytes && Convert.ToHexStringLower(SHA256.HashData(Bytes(item, CloudStorage.MaxFileBytes))) == file.Sha256);
            }
            return Result(RepositoryContent.ManagedCollection, $"Verified PD2 Cloud collection: {files.Count} {(files.Count == 1 ? "file" : "files")}.", remote);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or OverflowException)
        {
            return Result(RepositoryContent.Incompatible, "This repository contains PD2 Cloud data this version cannot read. Nothing was changed.");
        }
    }

    private static void Properties(JsonElement element, params string[] names)
    {
        Require(element.ValueKind == JsonValueKind.Object);
        var properties = element.EnumerateObject().Select(p => p.Name).ToArray();
        Require(properties.Length == names.Length && properties.ToHashSet(StringComparer.Ordinal).SetEquals(names));
    }
    private static bool IsId(string? id) => id is { Length: 40 } && id.All(char.IsAsciiHexDigitLower);
    private static void Require(bool condition) { if (!condition) throw new InvalidDataException(); }

    // Reconstruct every tree object, including unrelated entries and empty directories. This detects omitted
    // entries/parents and inconsistent subtree IDs rather than trusting only the recursive response's label.
    private static void VerifyTrees(RepositorySnapshot snapshot, CancellationToken ct)
    {
        var entries = new Dictionary<string, GitTreeEntry>(StringComparer.Ordinal);
        foreach (var item in snapshot.Entries)
        {
            ct.ThrowIfCancellationRequested();
            Require(!string.IsNullOrEmpty(item.Path) && item.Path.Split('/').All(p => p.Length > 0 && p is not ("." or "..") && !p.Contains('\0')));
            Require(IsId(item.Sha) && entries.TryAdd(item.Path, item));
            Require((item.Type, item.Mode) is ("tree", "040000") or ("blob", "100644") or ("blob", "100755") or ("blob", "120000") or ("commit", "160000"));
        }
        var children = new Dictionary<string, List<GitTreeEntry>>(StringComparer.Ordinal);
        foreach (var item in entries.Values)
        {
            var slash = item.Path.LastIndexOf('/');
            var parent = slash < 0 ? "" : item.Path[..slash];
            Require(parent.Length == 0 || entries.TryGetValue(parent, out var ancestor) && ancestor.Type == "tree");
            if (!children.TryGetValue(parent, out var list)) children.Add(parent, list = []);
            list.Add(item);
        }
        var trees = entries.Values.Where(e => e.Type == "tree").Select(e => (e.Path, e.Sha)).Append(("", snapshot.Tree!));
        foreach (var (path, sha) in trees)
        {
            ct.ThrowIfCancellationRequested();
            using var content = new MemoryStream();
            if (children.TryGetValue(path, out var items))
            {
                var ordered = items.Select(e => (Entry: e, Name: e.Path[(e.Path.LastIndexOf('/') + 1)..]))
                    .Select(e => (e.Entry, e.Name, SortKey: Encoding.UTF8.GetBytes(e.Name + (e.Entry.Type == "tree" ? "/" : ""))))
                    .OrderBy(e => e.SortKey, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)));
                foreach (var item in ordered)
                {
                    content.Write(Encoding.UTF8.GetBytes((item.Entry.Type == "tree" ? "40000" : item.Entry.Mode) + " " + item.Name + "\0"));
                    content.Write(Convert.FromHexString(item.Entry.Sha));
                }
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.ASCII.GetBytes("tree " + content.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
            hash.AppendData(content.GetBuffer().AsSpan(0, (int)content.Length));
            Require(Convert.ToHexStringLower(hash.GetHashAndReset()) == sha);
        }
    }
}
