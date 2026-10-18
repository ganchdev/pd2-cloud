using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PD2Cloud.Core;
using PD2Cloud.Desktop.ViewModels;
using PD2Cloud.GitHub;
using PD2Cloud.Remote;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class CloudCollectionTests
{
    private static readonly RepositoryChoice Repository = new(1, "player/saves", "main");
    private static readonly string Commit = new('a', 40);

    // Fixtures model Git's binary object framing independently of the reader; no live account or Git CLI.
    private static string Id(string kind, byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(
        Encoding.ASCII.GetBytes($"{kind} {bytes.Length}\0").Concat(bytes).ToArray()));
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal sealed class Fixture
    {
        public readonly Dictionary<string, (string Mode, byte[] Bytes)> Files = new(StringComparer.Ordinal);
        public JsonObject Manifest;
        public readonly HashSet<string> EmptyDirectories = [CloudStorage.FilesPath];
        public Fixture()
        {
            byte[] bytes = [0, 255, 128, 13, 10];
            Files[CloudStorage.FilesPath + "/nested/save.any"] = ("100644", bytes);
            FileEntry[] files = [new("nested/save.any", bytes.Length, Sha256(bytes))];
            Manifest = JsonSerializer.SerializeToNode(new { Format = "PD2Cloud.Collection", SchemaVersion = 1, Files = files, CollectionFingerprint = CollectionInventory.Fingerprint(files) })!.AsObject();
        }
        public RepositorySnapshot Snapshot(bool includeManifest = true)
        {
            var all = new Dictionary<string, (string Mode, byte[] Bytes)>(Files, StringComparer.Ordinal);
            if (includeManifest) all[CloudStorage.ManifestPath] = ("100644", Encoding.UTF8.GetBytes(Manifest.ToJsonString()));
            var paths = new HashSet<string>(EmptyDirectories, StringComparer.Ordinal);
            foreach (var path in all.Keys.Concat(EmptyDirectories).ToArray())
            {
                var parent = path;
                while (parent.LastIndexOf('/') is var slash && slash >= 0) { parent = parent[..slash]; paths.Add(parent); }
            }
            var entries = all.Select(p => new GitTreeEntry(p.Key, p.Value.Mode, p.Value.Mode == "160000" ? "commit" : "blob", Id("blob", p.Value.Bytes), p.Value.Bytes.Length)).ToList();
            var blobs = all.Values.GroupBy(p => Id("blob", p.Bytes)).ToDictionary(g => g.Key, g => g.First().Bytes, StringComparer.Ordinal);
            string Tree(string path)
            {
                static string Parent(string p) => p.Contains('/') ? p[..p.LastIndexOf('/')] : "";
                foreach (var dir in paths.Where(p => Parent(p) == path).ToArray())
                    entries.Add(new(dir, "040000", "tree", Tree(dir), 0));
                var children = entries.Where(e => Parent(e.Path) == path)
                    .Select(e => (Entry: e, Name: e.Path.Split('/').Last()))
                    .OrderBy(e => Encoding.UTF8.GetBytes(e.Name + (e.Entry.Type == "tree" ? "/" : "")), Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)));
                using var bytes = new MemoryStream();
                foreach (var child in children)
                {
                    bytes.Write(Encoding.UTF8.GetBytes($"{(child.Entry.Type == "tree" ? "40000" : child.Entry.Mode)} {child.Name}\0"));
                    bytes.Write(Convert.FromHexString(child.Entry.Sha));
                }
                return Id("tree", bytes.ToArray());
            }
            var root = Tree("");
            return new(Repository, Commit, root, entries.ToArray(), blobs);
        }
    }

    [Fact]
    public void VerifiesOpaqueBytesPinnedIdentityAndIgnoresUnrelatedContentForInventory()
    {
        var fixture = new Fixture();
        fixture.Files["README.md"] = ("100644", Encoding.UTF8.GetBytes("unrelated"));
        fixture.Files["other/link"] = ("120000", Encoding.UTF8.GetBytes("/outside"));
        var snapshot = fixture.Snapshot();
        var result = CloudCollectionReader.Verify(snapshot);
        Assert.Equal(RepositoryContent.ManagedCollection, result.Content);
        Assert.Equal(Commit, result.Collection!.Commit);
        Assert.Equal("1", result.Collection.RepositoryIdentity);
        Assert.Equal("main", result.Collection.Branch);
        var file = Assert.Single(result.Collection.Manifest!.Files);
        Assert.Equal("nested/save.any", file.RelativePath);
        Assert.Equal(5, file.SizeBytes);
        Assert.Equal(Sha256([0, 255, 128, 13, 10]), file.Sha256);
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", CloudStorage.BlobId([]));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("format")]
    [InlineData("fingerprint")]
    [InlineData("sha256")]
    [InlineData("size")]
    [InlineData("duplicate")]
    [InlineData("unsafe")]
    [InlineData("noncanonical")]
    [InlineData("null")]
    [InlineData("unknown-field")]
    [InlineData("missing-file")]
    [InlineData("extra-file")]
    [InlineData("symlink")]
    [InlineData("executable")]
    [InlineData("submodule")]
    [InlineData("namespace-case")]
    [InlineData("namespace-extra")]
    [InlineData("empty-directory-collision")]
    [InlineData("directory-file-collision")]
    [InlineData("file-limit")]
    public void RejectsIncompatibleManagedCollections(string fault)
    {
        var fixture = new Fixture();
        var files = fixture.Manifest["Files"]!.AsArray();
        var file = files[0]!.AsObject();
        var path = CloudStorage.FilesPath + "/nested/save.any";
        switch (fault)
        {
            case "schema": fixture.Manifest["SchemaVersion"] = 2; break;
            case "format": fixture.Manifest["Format"] = "something else"; break;
            case "fingerprint": fixture.Manifest["CollectionFingerprint"] = new string('0', 64); break;
            case "sha256": file["Sha256"] = new string('0', 64); break;
            case "size": file["SizeBytes"] = 4; break;
            case "duplicate": files.Add(file.DeepClone()); break;
            case "unsafe": file["RelativePath"] = "../outside"; break;
            case "noncanonical": file["RelativePath"] = "e\u0301"; break;
            case "null": file["RelativePath"] = null; break;
            case "unknown-field": fixture.Manifest["Unexpected"] = true; break;
            case "missing-file": fixture.Files.Remove(path); break;
            case "extra-file": fixture.Files[CloudStorage.FilesPath + "/unlisted.tmp"] = ("100644", [42]); break;
            case "symlink": fixture.Files[path] = ("120000", [1]); break;
            case "executable": fixture.Files[path] = ("100755", [1]); break;
            case "submodule": fixture.Files[path] = ("160000", [1]); break;
            case "namespace-case": fixture.Files[".PD2CLOUD/other"] = ("100644", [1]); break;
            case "namespace-extra": fixture.Files[CloudStorage.Root + "/other"] = ("100644", [1]); break;
            case "empty-directory-collision": fixture.EmptyDirectories.Add(CloudStorage.FilesPath + "/NESTED"); break;
            case "directory-file-collision": fixture.EmptyDirectories.Add(path); break;
            case "file-limit": file["SizeBytes"] = CloudStorage.MaxFileBytes + 1; break;
        }
        var result = CloudCollectionReader.Verify(fixture.Snapshot());
        Assert.Equal(RepositoryContent.Incompatible, result.Content);
        Assert.Null(result.Collection);
    }

    [Fact]
    public void ManagedNamespaceWithoutManifestNeverMeansEmpty()
    {
        var result = CloudCollectionReader.Verify(new Fixture().Snapshot(includeManifest: false));
        Assert.Equal(RepositoryContent.Incompatible, result.Content);
        Assert.Null(result.Collection);
    }

    [Theory]
    [InlineData("file-count")]
    [InlineData("collection-bytes")]
    [InlineData("manifest-bytes")]
    [InlineData("file-bytes")]
    public async Task ResourceLimitsBlockBeforePartialInventoryCanBeUsed(string limit)
    {
        var fixture = new Fixture();
        if (limit is "file-count" or "collection-bytes")
        {
            fixture.Files.Clear();
            var bytes = limit == "collection-bytes" ? new byte[CloudStorage.MaxFileBytes] : new byte[] { 42 };
            var count = limit == "collection-bytes" ? 33 : CloudStorage.MaxFiles + 1;
            var hash = Sha256(bytes);
            var files = Enumerable.Range(0, count).Select(i => new FileEntry($"file-{i}", bytes.Length, hash)).ToArray();
            foreach (var file in files) fixture.Files[CloudStorage.FilesPath + "/" + file.RelativePath] = ("100644", bytes);
            fixture.Manifest["Files"] = JsonSerializer.SerializeToNode(files);
            fixture.Manifest["CollectionFingerprint"] = CollectionInventory.Fingerprint(files);
        }
        if (limit == "manifest-bytes") fixture.Manifest["Extra"] = new string('x', CloudStorage.MaxManifestBytes);
        if (limit == "file-bytes") fixture.Files[CloudStorage.FilesPath + "/nested/save.any"] = ("100644", new byte[CloudStorage.MaxFileBytes + 1]);
        var snapshot = fixture.Snapshot();
        Assert.Equal(RepositoryContent.Incompatible, CloudCollectionReader.Verify(snapshot).Content);
        var handler = new Handler(snapshot);
        var reader = new CloudCollectionReader(new GitHubConnection(new GitHubApi(new HttpClient(handler), "client"), new Store()));
        await Assert.ThrowsAsync<ConnectionException>(() => reader.ReadAsync(Repository, default));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Format\":\"PD2Cloud.Collection\",\"Format\":\"other\",\"SchemaVersion\":1,\"Files\":[],\"CollectionFingerprint\":\"x\"}")]
    public void MalformedOrDuplicateManifestFieldsBlockVerification(string json)
    {
        var fixture = new Fixture();
        fixture.Files[CloudStorage.ManifestPath] = ("100644", Encoding.UTF8.GetBytes(json));
        Assert.Equal(RepositoryContent.Incompatible, CloudCollectionReader.Verify(fixture.Snapshot(false)).Content);
    }

    [Fact]
    public void EmptyManagedCollectionIsVerifiedAndCanProduceInitialUpload()
    {
        var fixture = new Fixture(); fixture.Files.Clear();
        fixture.Manifest["Files"] = new JsonArray();
        fixture.Manifest["CollectionFingerprint"] = CollectionInventory.Fingerprint([]);
        var result = CloudCollectionReader.Verify(fixture.Snapshot());
        Assert.Equal(RepositoryContent.ManagedCollection, result.Content);
        Assert.Empty(result.Collection!.Manifest!.Files);
        var local = CloudCollectionReader.Verify(new Fixture().Snapshot()).Collection!.Manifest!;
        Assert.Equal(SyncAction.Upload, SyncPlanner.Plan(local.Files, result.Collection.Manifest.Files, null));
    }

    [Fact]
    public void UnbornEmptyTreeAndUnrelatedRepositoriesNeverFabricateVerifiedInventories()
    {
        RepositorySnapshot unborn = new(Repository, null, null, [], new Dictionary<string, byte[]>());
        Assert.Equal(RepositoryContent.EmptyRepository, CloudCollectionReader.Verify(unborn).Content);
        Assert.Null(CloudCollectionReader.Verify(unborn).Collection);
        var fixture = new Fixture(); fixture.Files.Clear(); fixture.EmptyDirectories.Clear();
        var empty = CloudCollectionReader.Verify(fixture.Snapshot(false));
        Assert.Equal(RepositoryContent.EmptyRepository, empty.Content); Assert.Null(empty.Collection);
        fixture.Files["README.md"] = ("100644", [1]);
        var unrelated = CloudCollectionReader.Verify(fixture.Snapshot(false));
        Assert.Equal(RepositoryContent.Unrelated, unrelated.Content); Assert.Null(unrelated.Collection);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("subtree")]
    [InlineData("omission")]
    [InlineData("parent")]
    [InlineData("blob")]
    [InlineData("blob-size")]
    [InlineData("duplicate-tree-path")]
    [InlineData("commit")]
    public void RejectsCorruptGitObjectsAndIncompleteTrees(string fault)
    {
        var snapshot = new Fixture().Snapshot();
        var entries = snapshot.Entries.ToArray();
        var blob = entries.First(e => e.Type == "blob" && e.Path != CloudStorage.ManifestPath);
        switch (fault)
        {
            case "root": snapshot = snapshot with { Tree = new string('b', 40) }; break;
            case "subtree": entries = entries.Select(e => e.Path == CloudStorage.Root ? e with { Sha = new string('b', 40) } : e).ToArray(); break;
            case "omission": entries = entries.Where(e => e != blob).ToArray(); break;
            case "parent": entries = entries.Where(e => e.Path != CloudStorage.FilesPath).ToArray(); break;
            case "blob": ((Dictionary<string, byte[]>)snapshot.Blobs)[blob.Sha] = [42]; break;
            case "blob-size": entries = entries.Select(e => e == blob ? e with { Size = 2 } : e).ToArray(); break;
            case "duplicate-tree-path": entries = entries.Append(entries[0]).ToArray(); break;
            case "commit": snapshot = snapshot with { Commit = "main" }; break;
        }
        snapshot = snapshot with { Entries = entries };
        Assert.Equal(RepositoryContent.Incompatible, CloudCollectionReader.Verify(snapshot).Content);
    }

    private sealed class Store : ICredentialStore
    {
        public bool FailWrites;
        public string? Value = JsonSerializer.Serialize(new ConnectionCredentials { ClientId = "client", AccessToken = "synthetic", Repository = Repository });
        public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task WriteAsync(string value, CancellationToken ct) { if (FailWrites) throw new IOException(); Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct) { Value = null; return Task.CompletedTask; }
    }
    private sealed class Handler(RepositorySnapshot snapshot) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        public Func<string, object?, (HttpStatusCode Status, object? Body)>? Override;
        private int headReads;
        public bool MoveHead;
        public string ExpectedToken = "synthetic";
        public Action? BeforeMetadata;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsoluteUri == "https://github.com/login/oauth/access_token")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Requests.Add(request.RequestUri.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"token_type":"bearer","access_token":"rotated","refresh_token":"rotated-refresh","expires_in":3600,"refresh_token_expires_in":7200}""")
                });
            }
            BeforeMetadata?.Invoke();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("api.github.com", request.RequestUri.Host);
            Assert.Equal(ExpectedToken, request.Headers.Authorization?.Parameter);
            var path = request.RequestUri.AbsolutePath; Requests.Add(request.RequestUri.AbsoluteUri);
            object repo = new { id = 1, full_name = "player/saves", default_branch = "main", @private = true, archived = false, disabled = false, permissions = new { push = true } };
            object? body = path switch
            {
                "/user/installations" => new { installations = new[] { new { id = 2, suspended_at = (string?)null, permissions = new { contents = "write" } } } },
                "/user/installations/2/repositories" => new { repositories = new[] { repo } },
                "/repos/player/saves" => repo,
                "/repos/player/saves/commits" => new[] { new { sha = snapshot.Commit } },
                "/repos/player/saves/git/ref/heads/main" => new { @ref = "refs/heads/main", @object = new { type = "commit", sha = MoveHead && ++headReads > 1 ? new string('b', 40) : snapshot.Commit } },
                _ => null
            };
            if (path.Contains("/git/commits/", StringComparison.Ordinal)) body = new { sha = snapshot.Commit, tree = new { sha = snapshot.Tree } };
            if (path.Contains("/git/trees/", StringComparison.Ordinal)) body = new { sha = snapshot.Tree, truncated = false, tree = snapshot.Entries.Select(e => new { path = e.Path, mode = e.Mode, type = e.Type, sha = e.Sha, size = e.Size }) };
            if (path.Contains("/git/blobs/", StringComparison.Ordinal))
            {
                var sha = path.Split('/').Last(); var bytes = snapshot.Blobs[sha];
                body = new { sha, encoding = "base64", size = bytes.Length, content = Convert.ToBase64String(bytes) };
            }
            var response = Override?.Invoke(path, body) ?? (HttpStatusCode.OK, body);
            return Task.FromResult(new HttpResponseMessage(response.Item1) { Content = new StringContent(JsonSerializer.Serialize(response.Item2)) });
        }
    }

    [Fact]
    public async Task AuthenticatedReadPinsObjectsRechecksHeadAndOnlyUsesGetRequests()
    {
        var snapshot = new Fixture().Snapshot(); var handler = new Handler(snapshot);
        var connection = new GitHubConnection(new GitHubApi(new HttpClient(handler), "client"), new Store());
        var result = await new CloudCollectionReader(connection).ReadAsync(Repository, default);
        Assert.Equal(RepositoryContent.ManagedCollection, result.Content);
        Assert.Equal(2, handler.Requests.Count(p => p.EndsWith("/git/ref/heads/main", StringComparison.Ordinal)));
        Assert.Contains(handler.Requests, p => p.EndsWith("/git/commits/" + Commit, StringComparison.Ordinal));
        Assert.Contains(handler.Requests, p => p.EndsWith("/git/trees/" + snapshot.Tree + "?recursive=1", StringComparison.Ordinal));
        Assert.Equal(2, handler.Requests.Count(p => p.EndsWith("/repos/player/saves", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Requests, p => p.Contains("/contents", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("wrong-tree")]
    [InlineData("wrong-blob")]
    [InlineData("wrong-bytes")]
    [InlineData("encoding")]
    [InlineData("blob-limit")]
    [InlineData("entry-limit")]
    [InlineData("branch-moved")]
    [InlineData("missing-branch")]
    [InlineData("offline")]
    [InlineData("private")]
    [InlineData("identity")]
    [InlineData("permissions")]
    [InlineData("rate-limit")]
    [InlineData("empty-conflict")]
    [InlineData("response-limit")]
    public async Task TransportFailuresNeverYieldPartialOrEmptyVerifiedCollections(string fault)
    {
        var handler = new Handler(new Fixture().Snapshot()) { MoveHead = fault == "branch-moved" };
        handler.Override = (path, body) =>
        {
            var node = JsonSerializer.SerializeToNode(body);
            if (fault == "offline") throw new HttpRequestException("sensitive response");
            if (fault == "rate-limit") return (HttpStatusCode.Forbidden, new { message = "sensitive response" });
            if (path.EndsWith("/commits", StringComparison.Ordinal) && fault == "empty-conflict") return (HttpStatusCode.Conflict, new { message = "Something else" });
            if (path.Contains("/ref/", StringComparison.Ordinal) && fault == "missing-branch") return (HttpStatusCode.NotFound, new { message = "Not Found" });
            if (path == "/repos/player/saves")
            {
                if (fault == "private") node!["private"] = false;
                if (fault == "identity") node!["id"] = 2;
                if (fault == "permissions") node!["permissions"]!["push"] = false;
            }
            if (path.Contains("/git/trees/", StringComparison.Ordinal))
            {
                if (fault == "truncated") node!["truncated"] = true;
                if (fault == "wrong-tree") node!["sha"] = new string('0', 40);
                if (fault == "blob-limit") node!["tree"]![0]!["size"] = CloudStorage.MaxFileBytes + 1;
                if (fault == "entry-limit") node!["tree"] = JsonSerializer.SerializeToNode(Enumerable.Repeat(node!["tree"]![0], CloudStorage.MaxEntries + 1).ToArray());
                if (fault == "response-limit") return (HttpStatusCode.OK, new { huge = new string('x', 4 * 1024 * 1024) });
            }
            if (path.Contains("/git/blobs/", StringComparison.Ordinal))
            {
                if (fault == "wrong-blob") node!["sha"] = new string('0', 40);
                if (fault == "wrong-bytes") node!["content"] = "AQ==";
                if (fault == "encoding") node!["encoding"] = "utf-8";
            }
            return (HttpStatusCode.OK, node);
        };
        var reader = new CloudCollectionReader(new GitHubConnection(new GitHubApi(new HttpClient(handler), "client"), new Store()));
        var model = new CloudInspectionViewModel(reader.ReadAsync);
        await model.RefreshAsync(Repository);
        Assert.Null(model.Result); Assert.False(model.IsBusy);
        Assert.DoesNotContain("sensitive response", model.Notice);
    }

    [Fact]
    public async Task ExplicitEmptyResponseIsClassifiedWithoutInventingCommitOrBaseline()
    {
        var handler = new Handler(new Fixture().Snapshot());
        handler.Override = (path, body) => path.EndsWith("/commits", StringComparison.Ordinal)
            ? (HttpStatusCode.Conflict, new { message = "Git Repository is empty." }) : (HttpStatusCode.OK, body);
        var result = await new CloudCollectionReader(new GitHubConnection(new GitHubApi(new HttpClient(handler), "client"), new Store())).ReadAsync(Repository, default);
        Assert.Equal(RepositoryContent.EmptyRepository, result.Content); Assert.Null(result.Collection); Assert.Null(result.Commit);
        Assert.DoesNotContain(handler.Requests, p => p.Contains("/git/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedStoredPairingAndCancellationBlockBeforeNetworkAccess()
    {
        var handler = new Handler(new Fixture().Snapshot());
        var connection = new GitHubConnection(new GitHubApi(new HttpClient(handler), "client"), new Store());
        await Assert.ThrowsAsync<ConnectionException>(() => connection.ReadRepositoryAsync(Repository with { Id = 9 }, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.ReadRepositoryAsync(Repository, cancelled.Token));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloudReadRequiresDurableRotatedCredentialsBeforeAnyDataRequest(bool saveFails)
    {
        var store = new Store
        {
            FailWrites = saveFails,
            Value = JsonSerializer.Serialize(new ConnectionCredentials
            {
                ClientId = "client",
                AccessToken = "old",
                RefreshToken = "old-refresh",
                Repository = Repository,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                RefreshExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            })
        };
        var handler = new Handler(new Fixture().Snapshot()) { ExpectedToken = "rotated", BeforeMetadata = () => Assert.Contains("rotated", store.Value) };
        var reader = new CloudCollectionReader(new GitHubConnection(new GitHubApi(new HttpClient(handler), "client"), store));
        if (saveFails)
        {
            await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(Repository, default));
            Assert.Single(handler.Requests); Assert.Contains("old-refresh", store.Value);
        }
        else Assert.Equal(RepositoryContent.ManagedCollection, (await reader.ReadAsync(Repository, default)).Content);
    }

    [Fact]
    public async Task RefreshFailureInvalidationCancellationAndLateResultsDiscardPreviousComparison()
    {
        var verified = CloudCollectionReader.Verify(new Fixture().Snapshot());
        var pending = new TaskCompletionSource<CloudReadResult>();
        var count = 0;
        using var model = new CloudInspectionViewModel((_, _) => ++count switch
        {
            1 => Task.FromResult(verified),
            2 => Task.FromException<CloudReadResult>(new HttpRequestException()),
            _ => pending.Task
        });
        await model.RefreshAsync(Repository);
        Assert.Same(verified, model.Result);
        await model.RefreshAsync(Repository); Assert.Null(model.Result);
        var delayed = model.RefreshAsync(Repository); Assert.True(model.IsBusy);
        model.Cancel(); pending.SetResult(verified); await delayed;
        Assert.Null(model.Result); Assert.False(model.IsBusy); Assert.Contains("cancelled", model.Notice);
    }

    [Fact]
    public async Task DisposedCloudViewRejectsLateCompletion()
    {
        var pending = new TaskCompletionSource<CloudReadResult>();
        var model = new CloudInspectionViewModel((_, _) => pending.Task);
        var operation = model.RefreshAsync(Repository);
        model.Dispose(); pending.SetResult(CloudCollectionReader.Verify(new Fixture().Snapshot()));
        await operation;
        Assert.Null(model.Result); Assert.False(model.IsBusy);
    }
}
