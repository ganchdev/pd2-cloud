using System.Text.Json;

namespace PD2Cloud.GitHub;

public sealed partial class GitHubConnection
{
    // Holds selection/disconnect/refresh coordination throughout the complete publication or assessment.
    internal async Task<T> CoordinateRepositoryAsync<T>(RepositoryChoice expected,
        Func<RepositoryWriter, Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var credentials = await ReadAsync(ct).ConfigureAwait(false) ?? throw new ConnectionException("Connect to GitHub first.");
            if (credentials.Repository != expected) throw new ConnectionException("Repository selection changed. Review the operation again.");
            credentials = await CurrentAsync(credentials, ct).ConfigureAwait(false);
            var writer = new RepositoryWriter(api, expected, credentials.AccessToken, ct);
            try { return await action(writer).ConfigureAwait(false); }
            finally { writer.Close(); }
        }
        finally { gate.Release(); }
    }
}

internal sealed class RepositoryWriter(GitHubApi api, RepositoryChoice repository, string token, CancellationToken ct)
{
    private bool closed;
    internal void Close() => closed = true;
    private void Check() { if (closed) throw new InvalidOperationException("Credential lease ended."); }
    internal Task<RepositorySnapshot> ReadAsync() { Check(); return api.ReadRepositoryAsync(repository, token, ct); }
    internal Task<string> BlobAsync(byte[] bytes) { Check(); return api.CreateBlobAsync(repository, token, bytes, ct); }
    internal Task<string> TreeAsync(IReadOnlyList<GitTreeEntry> entries) { Check(); return api.CreateTreeAsync(repository, token, entries, ct); }
    internal Task<string> CommitAsync(string tree, string parent, string operation) { Check(); return api.CreateCommitAsync(repository, token, tree, parent, operation, ct); }
    // GitHub's Git Data API rejects writes to an empty repository, so the first commit must be created through the Contents API.
    internal Task<(string Commit, string Tree)> CreateFileAsync(string path, byte[] content, string operation) { Check(); return api.CreateFileAsync(repository, token, path, content, operation, ct); }
    internal Task PublishAsync(string commit) { Check(); return api.PublishAsync(repository, token, commit, ct); }
}

public sealed partial class GitHubApi
{
    private static string RepositoryUrl(RepositoryChoice repository) => "https://api.github.com/repos/" + string.Join('/', repository.FullName.Split('/').Select(Uri.EscapeDataString));
    internal async Task<string> CreateBlobAsync(RepositoryChoice repository, string token, byte[] bytes, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Post, RepositoryUrl(repository) + "/git/blobs", token, null, ct,
            new { content = Convert.ToBase64String(bytes), encoding = "base64" }).ConfigureAwait(false);
        var sha = ObjectId(Required(result, "sha"));
        if (sha != CloudStorage.BlobId(bytes)) throw InvalidCloud();
        return sha;
    }
    internal async Task<string> CreateTreeAsync(RepositoryChoice repository, string token, IReadOnlyList<GitTreeEntry> entries, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Post, RepositoryUrl(repository) + "/git/trees", token, null, ct,
            new { tree = entries.Select(e => new { path = e.Path, mode = e.Mode, type = e.Type, sha = e.Sha }).ToArray() }).ConfigureAwait(false);
        var sha = ObjectId(Required(result, "sha"));
        if (sha != CloudStorage.TreeId(entries)) throw InvalidCloud();
        return sha;
    }
    internal async Task<string> CreateCommitAsync(RepositoryChoice repository, string token, string tree, string parent, string operation, CancellationToken ct)
    {
        var url = RepositoryUrl(repository) + "/git/commits";
        var result = await SendAsync(HttpMethod.Post, url, token, null, ct,
            new { message = "PD2 Cloud upload " + operation, tree = ObjectId(tree), parents = new[] { ObjectId(parent) } }).ConfigureAwait(false);
        var sha = ObjectId(Required(result, "sha"));
        // Verify the candidate's immutable tree and single parent before allowing a reference update.
        result = await SendAsync(HttpMethod.Get, url + "/" + sha, token, null, ct).ConfigureAwait(false);
        if (ObjectId(Required(result, "sha")) != sha || ObjectId(Required(result.GetProperty("tree"), "sha")) != tree
            || result.GetProperty("parents").GetArrayLength() != 1 || ObjectId(Required(result.GetProperty("parents")[0], "sha")) != parent)
            throw InvalidCloud();
        return sha;
    }
    // Seed the first file (and therefore the first commit and default branch) through the Contents API. This is the
    // only way to create the initial commit: the Git Data API returns 409 "Git Repository is empty." until a commit exists.
    internal async Task<(string Commit, string Tree)> CreateFileAsync(RepositoryChoice repository, string token, string path, byte[] content, string operation, CancellationToken ct)
    {
        var url = RepositoryUrl(repository) + "/contents/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var result = await SendAsync(HttpMethod.Put, url, token, null, ct,
            new { message = "PD2 Cloud initialize " + operation, content = Convert.ToBase64String(content) }).ConfigureAwait(false);
        var contentSha = ObjectId(Required(result.GetProperty("content"), "sha"));
        if (contentSha != CloudStorage.BlobId(content)) throw InvalidCloud();
        var commit = result.GetProperty("commit");
        var commitSha = ObjectId(Required(commit, "sha"));
        var treeSha = ObjectId(Required(commit.GetProperty("tree"), "sha"));
        return (commitSha, treeSha);
    }
    internal async Task PublishAsync(RepositoryChoice repository, string token, string commit, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Patch, RepositoryUrl(repository) + "/git/refs/heads/" + Uri.EscapeDataString(repository.DefaultBranch), token, null, ct,
            new { sha = ObjectId(commit), force = false }).ConfigureAwait(false);
        if (Required(result, "ref") != "refs/heads/" + repository.DefaultBranch || Required(result.GetProperty("object"), "type") != "commit"
            || ObjectId(Required(result.GetProperty("object"), "sha")) != commit) throw InvalidCloud();
    }
}
