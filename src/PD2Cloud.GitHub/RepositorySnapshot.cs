using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PD2Cloud.GitHub;

public sealed record GitTreeEntry(string Path, string Mode, string Type, string Sha, long Size);
public sealed record RepositorySnapshot(RepositoryChoice Repository, string? Commit, string? Tree,
    IReadOnlyList<GitTreeEntry> Entries, IReadOnlyDictionary<string, byte[]> Blobs);

/// <summary>Read limits are deliberately lower than GitHub's API limits. No partial inventory is accepted.</summary>
public static class CloudStorage
{
    public const string Root = ".pd2cloud";
    public const string ManifestPath = Root + "/manifest.json";
    public const string FilesPath = Root + "/files";
    public const int MaxEntries = 10000;
    public const int MaxFiles = 1000;
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxManifestBytes = 512 * 1024;
    public const long MaxCollectionBytes = 64 * 1024 * 1024;

    public static string TreeId(IEnumerable<GitTreeEntry> entries)
    {
        using var content = new MemoryStream();
        var ordered = entries.Select(e => (Entry: e, Key: Encoding.UTF8.GetBytes(e.Path + (e.Type == "tree" ? "/" : ""))))
            .OrderBy(e => e.Key, Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)));
        foreach (var item in ordered)
        {
            content.Write(Encoding.UTF8.GetBytes((item.Entry.Type == "tree" ? "40000" : item.Entry.Mode) + " " + item.Entry.Path + "\0"));
            content.Write(Convert.FromHexString(item.Entry.Sha));
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes("tree " + content.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
        hash.AppendData(content.GetBuffer().AsSpan(0, (int)content.Length));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static string BlobId(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public sealed partial class GitHubApi
{
    internal async Task<RepositorySnapshot> ReadRepositoryAsync(RepositoryChoice expected, string token, CancellationToken ct)
    {
        var names = expected.FullName.Split('/');
        if (names.Length != 2 || names.Any(n => string.IsNullOrWhiteSpace(n) || n is "." or ".." || n.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
            throw new ConnectionException("Repository identity is invalid. Choose the repository again.");
        var branch = expected.DefaultBranch;
        if (string.IsNullOrWhiteSpace(branch) || branch.Length > 1024 || branch.Contains("..", StringComparison.Ordinal) || branch.Contains("@{", StringComparison.Ordinal)
            || branch.Any(c => char.IsControl(c) || " ~^:?*[\\".Contains(c))
            || branch.Split('/').Any(p => p.Length == 0 || p.StartsWith('.') || p.EndsWith('.') || p.EndsWith(".lock", StringComparison.Ordinal)))
            throw new ConnectionException("The selected branch name is invalid. Choose the repository again.");
        var url = "https://api.github.com/repos/" + string.Join('/', names.Select(Uri.EscapeDataString));
        await ValidateRepositoryAsync(expected, token, url, ct).ConfigureAwait(false);
        var commits = await SendAsync(HttpMethod.Get, url + "/commits?per_page=1", token, null, ct).ConfigureAwait(false);
        if (commits.ValueKind != System.Text.Json.JsonValueKind.Array) throw InvalidCloud();
        if (commits.GetArrayLength() == 0)
        {
            // This is an unborn repository, not a verified empty collection at a fabricated commit.
            await ValidateRepositoryAsync(expected, token, url, ct).ConfigureAwait(false);
            var again = await SendAsync(HttpMethod.Get, url + "/commits?per_page=1", token, null, ct).ConfigureAwait(false);
            if (again.ValueKind != System.Text.Json.JsonValueKind.Array || again.GetArrayLength() != 0) throw ChangedCloud();
            return new(expected, null, null, [], new Dictionary<string, byte[]>());
        }

        var branchUrl = url + "/git/ref/heads/" + Uri.EscapeDataString(expected.DefaultBranch);
        async Task<string> HeadAsync()
        {
            var reference = await SendAsync(HttpMethod.Get, branchUrl, token, null, ct).ConfigureAwait(false);
            if (Required(reference, "ref") != "refs/heads/" + expected.DefaultBranch) throw InvalidCloud();
            var obj = reference.GetProperty("object");
            if (Required(obj, "type") != "commit") throw InvalidCloud();
            return ObjectId(Required(obj, "sha"));
        }
        var commit = await HeadAsync().ConfigureAwait(false);
        var revision = await SendAsync(HttpMethod.Get, url + "/git/commits/" + commit, token, null, ct).ConfigureAwait(false);
        if (ObjectId(Required(revision, "sha")) != commit) throw InvalidCloud();
        var tree = ObjectId(Required(revision.GetProperty("tree"), "sha"));
        var listing = await SendAsync(HttpMethod.Get, url + "/git/trees/" + tree + "?recursive=1", token, null, ct).ConfigureAwait(false);
        if (ObjectId(Required(listing, "sha")) != tree || listing.GetProperty("truncated").GetBoolean())
            throw new ConnectionException("Cloud file listing is incomplete. No comparison is available.");
        var items = listing.GetProperty("tree");
        if (items.GetArrayLength() > CloudStorage.MaxEntries) throw LimitCloud();
        var entries = new List<GitTreeEntry>();
        foreach (var item in items.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var type = Required(item, "type");
            var size = type == "blob" ? item.GetProperty("size").GetInt64() : 0;
            if (size < 0) throw InvalidCloud();
            entries.Add(new(Required(item, "path"), Required(item, "mode"), type, ObjectId(Required(item, "sha")), size));
        }
        // Only managed regular blobs are fetched. The verifier rejects unsupported modes and namespace collisions.
        var managed = entries.Where(e => e.Type == "blob" && e.Mode == "100644" &&
            (e.Path == CloudStorage.ManifestPath || e.Path.StartsWith(CloudStorage.FilesPath + "/", StringComparison.Ordinal))).ToArray();
        if (managed.Length > CloudStorage.MaxFiles + 1) throw LimitCloud();
        long total = 0;
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in managed)
        {
            if (item.Size > (item.Path == CloudStorage.ManifestPath ? CloudStorage.MaxManifestBytes : CloudStorage.MaxFileBytes)) throw LimitCloud();
            total += item.Size;
            if (total > CloudStorage.MaxCollectionBytes) throw LimitCloud();
            if (blobs.TryGetValue(item.Sha, out var existing))
            {
                if (existing.LongLength != item.Size) throw InvalidCloud();
                continue;
            }
            var blob = await SendAsync(HttpMethod.Get, url + "/git/blobs/" + item.Sha, token, null, ct).ConfigureAwait(false);
            if (ObjectId(Required(blob, "sha")) != item.Sha || Required(blob, "encoding") != "base64"
                || blob.GetProperty("size").GetInt64() != item.Size) throw InvalidCloud();
            // The response is already bounded before decoding, and declared size is checked again against actual bytes.
            var bytes = Convert.FromBase64String(blob.GetProperty("content").GetString() ?? throw InvalidCloud());
            if (bytes.LongLength != item.Size || CloudStorage.BlobId(bytes) != item.Sha) throw InvalidCloud();
            blobs.Add(item.Sha, bytes);
        }
        await ValidateRepositoryAsync(expected, token, url, ct).ConfigureAwait(false);
        if (await HeadAsync().ConfigureAwait(false) != commit) throw ChangedCloud();
        return new(expected, commit, tree, entries.AsReadOnly(), blobs);
    }

    private async Task ValidateRepositoryAsync(RepositoryChoice expected, string token, string url, CancellationToken ct)
    {
        // Fresh installation permissions and direct metadata must both agree with the saved pairing.
        var accessible = await RepositoriesAsync(token, ct).ConfigureAwait(false);
        if (!accessible.Contains(expected)) throw new ConnectionException("Repository access or branch settings changed. Choose the repository again.");
        var repo = await SendAsync(HttpMethod.Get, url, token, null, ct).ConfigureAwait(false);
        if (repo.GetProperty("id").GetInt64() != expected.Id || Required(repo, "full_name") != expected.FullName
            || Required(repo, "default_branch") != expected.DefaultBranch || !repo.GetProperty("private").GetBoolean()
            || repo.GetProperty("archived").GetBoolean() || repo.GetProperty("disabled").GetBoolean()
            || !repo.GetProperty("permissions").GetProperty("push").GetBoolean())
            throw new ConnectionException("Repository identity, privacy or write access changed. Check GitHub access and choose it again.");
    }

    private static string ObjectId(string value) => value.Length == 40 && value.All(char.IsAsciiHexDigitLower) ? value : throw InvalidCloud();
    private static ConnectionException InvalidCloud() => new("Cloud data failed verification. No comparison is available; repository contents were left untouched.");
    private static ConnectionException LimitCloud() => new("Cloud data exceeds the supported read limits. No partial comparison is available.");
    private static ConnectionException ChangedCloud() => new("The repository changed during inspection. Refresh to check the latest revision.");
}
