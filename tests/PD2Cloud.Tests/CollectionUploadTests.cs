using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PD2Cloud.Core;
using PD2Cloud.GitHub;
using PD2Cloud.Local;
using PD2Cloud.Remote;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class CollectionUploadTests : IDisposable
{
    private static readonly RepositoryChoice Repository = new(1, "player/saves", "main");
    private static readonly string Parent = new('a', 40);
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pd2-upload-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Data => Path.Combine(root, "app");
    private sealed class Interruption : Exception;
    private sealed class Observer(Action<string> action) : IPreparationObserver
    {
        public void Reached(string boundary, string id, string? path) => action(boundary);
    }
    public CollectionUploadTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "nested"));
        File.WriteAllBytes(Path.Combine(Source, "nested", "任意.tmp"), [0, 255, 128, 13, 10]);
        File.WriteAllBytes(Path.Combine(Source, ".hidden"), []);
    }
    public void Dispose() => Directory.Delete(root, true);
    private OperationPreparationStore Store(Action<string>? observer = null) => new(Data, observer is null ? null : new Observer(observer));
    private sealed class Credentials : ICredentialStore
    {
        public string? Value = JsonSerializer.Serialize(new ConnectionCredentials { ClientId = "client", AccessToken = "token", Repository = Repository });
        public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task WriteAsync(string value, CancellationToken ct) { Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct) { Value = null; return Task.CompletedTask; }
    }
    private static UploadRequest Request(PreparedOperation op) => new(op.OperationId, op.SourceRoot, op.CollectionFingerprint, Repository, Parent);
    private static GitHubConnection Connection(Server server, Credentials? credentials = null) => new(new GitHubApi(new HttpClient(server), "client"), credentials ?? new());
    private LocalSyncStateStore SyncStateStore() => new(Data);
    private string Record(PreparedOperation op, string name) => Path.Combine(Data, "recovery", op.OperationId, "upload", name + ".json");
    private bool EvidenceRemains(PreparedOperation op) => Directory.Exists(Path.Combine(Data, "recovery", op.OperationId));

    // Independent HTTP fixture retains unreachable objects, checks single-parent non-forced updates and serves real object bytes.
    internal sealed class Server : HttpMessageHandler
    {
        internal string? Head = Parent;
        internal string? Seed;
        internal readonly Dictionary<string, byte[]> Blobs = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, GitTreeEntry[]> Trees = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, (string Tree, string? Parent)> Commits = new(StringComparer.Ordinal);
        internal readonly List<string> Writes = [];
        internal bool LostResponse, RejectUpdate, BadBlob, BadTree, BadParent, Private = true;
        internal bool RejectSeed;
        internal Action<string>? Before;
        internal Action? AfterPublish;
        internal Func<string, Task>? Pause;
        internal Server(string classification = "managed")
        {
            var fixture = new CloudCollectionTests.Fixture();
            fixture.Files.Clear();
            fixture.Manifest["Files"] = new JsonArray();
            fixture.Manifest["CollectionFingerprint"] = CollectionInventory.Fingerprint([]);
            fixture.Files["README.md"] = ("100755", [19, 20]);
            fixture.Files["other/link"] = ("120000", [21]);
            if (classification == "populated")
            {
                fixture.Files[CloudStorage.FilesPath + "/existing"] = ("100644", [1]);
                FileEntry[] files = [new("existing", 1, Convert.ToHexStringLower(SHA256.HashData([1])))];
                fixture.Manifest["Files"] = JsonSerializer.SerializeToNode(files);
                fixture.Manifest["CollectionFingerprint"] = CollectionInventory.Fingerprint(files);
            }
            var snapshot = fixture.Snapshot(classification != "unrelated");
            if (classification == "unrelated") snapshot = snapshot with { Entries = snapshot.Entries.Where(e => !e.Path.StartsWith(CloudStorage.Root, StringComparison.Ordinal)).ToArray() };
            if (classification == "incompatible") fixture.Manifest["SchemaVersion"] = 2;
            if (classification == "incompatible") snapshot = fixture.Snapshot();
            foreach (var p in snapshot.Blobs) Blobs.Add(p.Key, p.Value);
            var entries = snapshot.Entries;
            foreach (var tree in entries.Where(e => e.Type == "tree")) Trees[tree.Sha] = Children(entries, tree.Path);
            var children = Children(entries, "");
            var treeId = CloudStorage.TreeId(children);
            Trees[treeId] = children;
            Commits[Parent] = (treeId, null);
            if (classification == "unborn") Head = null;
            if (classification == "empty-tree") { var empty = CloudStorage.TreeId([]); Trees[empty] = []; Commits[Parent] = (empty, null); }
        }
        private static GitTreeEntry[] Children(IEnumerable<GitTreeEntry> entries, string path) => entries
            .Where(e => (e.Path.Contains('/') ? e.Path[..e.Path.LastIndexOf('/')] : "") == path)
            .Select(e => e with { Path = e.Path.Split('/').Last() }).ToArray();
        internal GitTreeEntry[] Expand(string tree, string prefix = "") => Trees[tree].SelectMany(e => e.Type == "tree"
            ? new[] { e with { Path = prefix + e.Path } }.Concat(Expand(e.Sha, prefix + e.Path + "/"))
            : new[] { e with { Path = prefix + e.Path } }).ToArray();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri.AbsolutePath;
            var route = request.Method + " " + path;
            Before?.Invoke(route);
            if (Pause is not null) await Pause(route);
            ct.ThrowIfCancellationRequested();
            object repo = new { id = 1, full_name = "player/saves", default_branch = "main", @private = Private, archived = false, disabled = false, permissions = new { push = true } };
            object? body = null;
            if (request.Method == HttpMethod.Get)
            {
                body = path switch
                {
                    "/user/installations" => new { installations = new[] { new { id = 2, suspended_at = (string?)null, permissions = new { contents = "write" } } } },
                    "/user/installations/2/repositories" => new { repositories = new[] { repo } },
                    "/user" => new { login = "player" },
                    "/repos/player/saves" => repo,
                    "/repos/player/saves/commits" => Head is null ? Array.Empty<object>() : new object[] { new { sha = Head } },
                    "/repos/player/saves/git/ref/heads/main" => new { @ref = "refs/heads/main", @object = new { type = "commit", sha = Head } },
                    _ => null
                };
                var id = path.Split('/').Last();
                if (path.Contains("/git/commits/", StringComparison.Ordinal))
                {
                    var commit = Commits[id];
                    body = new { sha = id, tree = new { sha = commit.Tree }, parents = commit.Parent is null ? Array.Empty<object>() : new object[] { new { sha = BadParent ? Parent : commit.Parent } } };
                    if (BadParent && commit.Parent is not null) body = new { sha = id, tree = new { sha = commit.Tree }, parents = Array.Empty<object>() };
                }
                if (path.Contains("/git/trees/", StringComparison.Ordinal)) body = new { sha = id, truncated = false, tree = Expand(id).Select(e => new { path = e.Path, mode = e.Mode, type = e.Type, sha = e.Sha, size = e.Size }) };
                if (path.Contains("/git/blobs/", StringComparison.Ordinal)) body = new { sha = id, size = Blobs[id].Length, encoding = "base64", content = Convert.ToBase64String(Blobs[id]) };
            }
            else
            {
                Writes.Add(route);
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var value = json.RootElement;
                // GitHub's Git Data API rejects writes to an empty repository, so an unborn repository is seeded
                // through the Contents API. Both behaviours are modelled here.
                if (path.EndsWith("/contents/.pd2cloud/manifest.json", StringComparison.Ordinal))
                {
                    Assert.Equal(HttpMethod.Put, request.Method);
                    if (RejectSeed) return Response(new { message = "sha wasn't supplied" }, HttpStatusCode.UnprocessableEntity);
                    Assert.Null(Head);
                    var bytes = Convert.FromBase64String(value.GetProperty("content").GetString()!);
                    var blobId = CloudStorage.BlobId(bytes); Blobs[blobId] = bytes;
                    var manifestEntry = new GitTreeEntry("manifest.json", "100644", "blob", blobId, bytes.Length);
                    var pd2cloud = CloudStorage.TreeId([manifestEntry]); Trees[pd2cloud] = [manifestEntry];
                    var rootEntry = new GitTreeEntry(CloudStorage.Root, "040000", "tree", pd2cloud, 0);
                    var rootTree = CloudStorage.TreeId([rootEntry]); Trees[rootTree] = [rootEntry];
                    var seed = Convert.ToHexStringLower(SHA1.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
                    Commits[seed] = (rootTree, null);
                    Seed = seed;
                    Head = seed;
                    body = new { content = new { sha = blobId }, commit = new { sha = seed, tree = new { sha = rootTree } } };
                }
                else if (Head is null)
                {
                    return Response(new { message = "Git Repository is empty." }, HttpStatusCode.Conflict);
                }
                else if (path.EndsWith("/git/blobs", StringComparison.Ordinal))
                {
                    var bytes = Convert.FromBase64String(value.GetProperty("content").GetString()!);
                    var id = CloudStorage.BlobId(bytes); Blobs[id] = bytes;
                    body = new { sha = BadBlob ? new string('0', 40) : id };
                }
                else if (path.EndsWith("/git/trees", StringComparison.Ordinal))
                {
                    Assert.False(value.TryGetProperty("base_tree", out _));
                    var entries = value.GetProperty("tree").EnumerateArray().Select(e => new GitTreeEntry(e.GetProperty("path").GetString()!, e.GetProperty("mode").GetString()!, e.GetProperty("type").GetString()!, e.GetProperty("sha").GetString()!, 0))
                        .Select(e => e with { Size = e.Type == "blob" ? Blobs[e.Sha].Length : 0 }).ToArray();
                    var id = CloudStorage.TreeId(entries); Trees[id] = entries;
                    body = new { sha = BadTree ? new string('0', 40) : id };
                }
                else if (path.EndsWith("/git/commits", StringComparison.Ordinal))
                {
                    var parents = value.GetProperty("parents").EnumerateArray().ToArray();
                    var tree = value.GetProperty("tree").GetString()!;
                    var id = Convert.ToHexStringLower(SHA1.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
                    if (parents.Length == 0) Commits[id] = (tree, null);
                    else { Assert.Single(parents); Commits[id] = (tree, parents[0].GetString()!); }
                    body = new { sha = id };
                }
                else if (request.Method == HttpMethod.Patch)
                {
                    Assert.Equal("/repos/player/saves/git/refs/heads/main", path);
                    Assert.False(value.GetProperty("force").GetBoolean());
                    var sha = value.GetProperty("sha").GetString()!;
                    if (RejectUpdate || Commits[sha].Parent != Head) return Response(new { message = "update rejected" }, HttpStatusCode.UnprocessableEntity);
                    Head = sha;
                    AfterPublish?.Invoke();
                    if (LostResponse) throw new HttpRequestException("lost response");
                    body = new { @ref = "refs/heads/main", @object = new { type = "commit", sha } };
                }
            }
            Assert.NotNull(body);
            return Response(body);
        }
        private static HttpResponseMessage Response(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body)) };
    }

    [Fact]
    public async Task UploadPreservesOpaqueBytesUnrelatedEntriesAndSourceSettingsAndDiscardsEvidence()
    {
        var op = await Store().PrepareAsync(Source);
        await File.WriteAllTextAsync(Path.Combine(Data, "settings.json"), "unrelated");
        var server = new Server();
        var original = server.Expand(server.Commits[Parent].Tree).Where(e => !e.Path.StartsWith(CloudStorage.Root, StringComparison.Ordinal)).OrderBy(e => e.Path).ToArray();
        var uploader = new CollectionUploader(Store(), Connection(server), SyncStateStore());
        var result = await uploader.UploadAsync(Request(op));
        Assert.Equal(UploadStatus.Published, result.Status);
        Assert.NotEqual(Parent, server.Head);
        Assert.Equal(original, server.Expand(server.Commits[server.Head!].Tree).Where(e => !e.Path.StartsWith(CloudStorage.Root, StringComparison.Ordinal)).OrderBy(e => e.Path));
        foreach (var file in op.Files)
        {
            var entry = Assert.Single(server.Expand(server.Commits[server.Head!].Tree), e => e.Path == CloudStorage.FilesPath + "/" + file.RelativePath);
            Assert.Equal(File.ReadAllBytes(Path.Combine(Source, file.RelativePath)), server.Blobs[entry.Sha]);
        }
        Assert.Equal("unrelated", await File.ReadAllTextAsync(Path.Combine(Data, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Data, "local-sync-state.json")));
        Assert.False(EvidenceRemains(op));
        Assert.Empty(await Store().AssessRecoveryAsync());
        // The operation is spent; it can never be published a second time.
        await Assert.ThrowsAnyAsync<Exception>(() => uploader.UploadAsync(Request(op)));
        Assert.Single(server.Writes, p => p.StartsWith("PATCH", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unborn")]
    [InlineData("empty-tree")]
    [InlineData("unrelated")]
    [InlineData("incompatible")]
    public async Task UnsupportedRepositoryNeverWrites(string classification)
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server(classification);
        await Assert.ThrowsAsync<RecoveryBlockedException>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore()).UploadAsync(Request(op)));
        Assert.Empty(server.Writes);
        Assert.False(EvidenceRemains(op));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("capture")]
    [InlineData("backup")]
    [InlineData("fingerprint")]
    [InlineData("root")]
    [InlineData("head")]
    [InlineData("repository")]
    [InlineData("large-file")]
    public async Task ChangedOrUnsupportedEvidenceBlocksBeforeWrites(string fault)
    {
        if (fault == "large-file") await File.WriteAllBytesAsync(Path.Combine(Source, "large"), new byte[CloudStorage.MaxFileBytes + 1]);
        var op = await Store().PrepareAsync(Source);
        var request = Request(op);
        if (fault is "source" or "capture" or "backup")
        {
            var dir = fault == "source" ? Source : fault == "capture" ? op.CaptureDirectory : op.BackupDirectory;
            await File.WriteAllBytesAsync(Path.Combine(dir, ".hidden"), [99]);
        }
        if (fault == "fingerprint") request = request with { CollectionFingerprint = new string('0', 64) };
        if (fault == "root") request = request with { SourceRoot = root };
        if (fault == "head") request = request with { PinnedCommit = new string('b', 40) };
        if (fault == "repository") request = request with { Repository = Repository with { Id = 2 } };
        var server = new Server();
        await Assert.ThrowsAnyAsync<Exception>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore()).UploadAsync(request));
        Assert.Empty(server.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostResponseAndRejectedUpdateLeaveNoEvidenceOrSyncRecord(bool published)
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server { LostResponse = published, RejectUpdate = !published };
        await Assert.ThrowsAnyAsync<Exception>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore()).UploadAsync(Request(op)));
        Assert.Equal(published, server.Head != Parent);
        Assert.False(EvidenceRemains(op));
        Assert.False(File.Exists(Path.Combine(Data, "local-sync-state.json")));
        // A fresh sync prepares again; the next refresh decides from the repository itself.
        Assert.NotEqual(op.OperationId, (await Store().PrepareAsync(Source)).OperationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentUpdateBeforeOrDuringPublicationNeverReparentsOrOverwrites(bool duringPatch)
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server();
        var competitor = new string('b', 40);
        server.Commits[competitor] = server.Commits[Parent];
        server.Before = route =>
        {
            if (duringPatch ? route.StartsWith("PATCH", StringComparison.Ordinal) : route.StartsWith("GET /repos/player/saves/git/commits/", StringComparison.Ordinal) && !route.EndsWith(Parent, StringComparison.Ordinal))
                server.Head = competitor;
        };
        await Assert.ThrowsAnyAsync<Exception>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore()).UploadAsync(Request(op)));
        Assert.Equal(competitor, server.Head);
        Assert.False(File.Exists(Path.Combine(Data, "local-sync-state.json")));
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("tree")]
    [InlineData("parent")]
    public async Task InvalidObjectResponsesNeverPublish(string fault)
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server { BadBlob = fault == "blob", BadTree = fault == "tree", BadParent = fault == "parent" };
        await Assert.ThrowsAsync<ConnectionException>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore()).UploadAsync(Request(op)));
        Assert.DoesNotContain(server.Writes, p => p.StartsWith("PATCH", StringComparison.Ordinal));
        Assert.False(EvidenceRemains(op));
    }

    [Fact]
    public async Task EditsDuringObjectCreationAndAfterPublicationArePreserved()
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server();
        server.Before = route => { if (route.EndsWith("/git/blobs", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(Source, ".hidden"), "external"); };
        await Assert.ThrowsAsync<RecoveryBlockedException>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore()).UploadAsync(Request(op)));
        Assert.Equal("external", File.ReadAllText(Path.Combine(Source, ".hidden")));
        Assert.DoesNotContain(server.Writes, p => p.StartsWith("PATCH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InterruptionAtEveryPublicationBoundaryDiscardsEvidenceAndPreservesSource()
    {
        var boundaries = new List<string>();
        var op = await Store().PrepareAsync(Source);
        await new CollectionUploader(Store(b => { if (b.StartsWith("Upload:", StringComparison.Ordinal)) boundaries.Add(b); }), Connection(new Server()), SyncStateStore()).UploadAsync(Request(op));
        Assert.Equal(9, boundaries.Count);
        for (var stop = 0; stop < boundaries.Count; stop++)
        {
            using var fixture = new CollectionUploadTests();
            var prepared = await fixture.Store().PrepareAsync(fixture.Source);
            var server = new Server();
            var seen = 0;
            var interruptedStore = fixture.Store(b =>
            {
                if (b.StartsWith("Upload:", StringComparison.Ordinal) && seen++ == stop) throw new Interruption();
            });
            await Assert.ThrowsAsync<Interruption>(() => new CollectionUploader(interruptedStore, Connection(server), SyncStateStore()).UploadAsync(Request(prepared)));
            Assert.False(Directory.Exists(Path.Combine(fixture.Data, "recovery", prepared.OperationId)));
            Assert.False(File.Exists(Path.Combine(fixture.Data, "local-sync-state.json")));
            Assert.Equal(prepared.CollectionFingerprint, (await new CollectionScanner().ScanAsync(fixture.Source, default)).CollectionFingerprint);
        }
    }

    [Fact]
    public async Task CancellationBeforePublicationNeverPublishes()
    {
        var op = await Store().PrepareAsync(Source);
        using var cancellation = new CancellationTokenSource();
        var store = Store(b => { if (b == "Upload:candidate.json:DirectoryFlushed") cancellation.Cancel(); });
        var server = new Server();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionUploader(store, Connection(server), SyncStateStore()).UploadAsync(Request(op), cancellation.Token));
        Assert.DoesNotContain(server.Writes, p => p.StartsWith("PATCH", StringComparison.Ordinal));
        Assert.False(EvidenceRemains(op));
    }

    [Fact]
    public async Task CoordinatorExcludesPreparationAssessmentSelectionAndDisconnectAndCancelledWaiters()
    {
        var op = await Store().PrepareAsync(Source);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Server();
        server.Pause = async route =>
        {
            if (route == "POST /repos/player/saves/git/blobs") { entered.TrySetResult(); await release.Task; }
        };
        var credentials = new Credentials();
        var connection = Connection(server, credentials);
        var upload = new CollectionUploader(Store(), connection, SyncStateStore()).UploadAsync(Request(op));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var cancelled = new CancellationTokenSource();
            var prepare = Store().PrepareAsync(Source, cancelled.Token);
            var assessment = Store().AssessRecoveryAsync(cancelled.Token);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assessment);
            using var secondWaiter = new CancellationTokenSource();
            var stillLocked = Store().AssessRecoveryAsync(secondWaiter.Token);
            Assert.False(stillLocked.IsCompleted);
            secondWaiter.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stillLocked);
            var disconnect = connection.DisconnectAsync(default);
            Assert.False(disconnect.IsCompleted);
            Assert.NotNull(credentials.Value);
            release.SetResult();
            Assert.Equal(UploadStatus.Published, (await upload).Status);
            await disconnect;
            Assert.Null(credentials.Value);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("backup")]
    [InlineData("source")]
    [InlineData("access")]
    public async Task RevalidationAfterCandidateBlocksChangedBytesOrAccess(string fault)
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server();
        var store = Store(b =>
        {
            if (b != "Upload:candidate.json:DirectoryFlushed") return;
            if (fault == "access") { server.Private = false; return; }
            var dir = fault == "source" ? Source : fault == "capture" ? op.CaptureDirectory : op.BackupDirectory;
            File.WriteAllText(Path.Combine(dir, ".hidden"), "changed");
        });
        await Assert.ThrowsAnyAsync<Exception>(() => new CollectionUploader(store, Connection(server), SyncStateStore()).UploadAsync(Request(op)));
        Assert.DoesNotContain(server.Writes, p => p.StartsWith("PATCH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SourceEditAtPublicationReturnsPublicationOnlyAndPreservesEdit()
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server { AfterPublish = () => File.WriteAllText(Path.Combine(Source, ".hidden"), "external edit") };
        var result = await new CollectionUploader(Store(), Connection(server), SyncStateStore()).UploadAsync(Request(op));
        Assert.Equal(UploadStatus.PublishedSourceChanged, result.Status);
        Assert.Equal("external edit", File.ReadAllText(Path.Combine(Source, ".hidden")));
        Assert.False(EvidenceRemains(op));
    }

    [Fact]
    public async Task EmptyLocalCollectionDoesNotBootstrapOrClaimSync()
    {
        Directory.Delete(Source, true);
        Directory.CreateDirectory(Source);
        var op = await Store().PrepareAsync(Source);
        var server = new Server();
        await Assert.ThrowsAsync<RecoveryBlockedException>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore()).UploadAsync(Request(op)));
        Assert.Empty(server.Writes);
    }

    [Fact]
    public async Task SuccessfulUploadPersistsSyncStateMatchingPublicationRecords()
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server();
        var uploader = new CollectionUploader(Store(), Connection(server), SyncStateStore());
        var result = await uploader.UploadAsync(Request(op));
        Assert.Equal(UploadStatus.Published, result.Status);
        var syncStatePath = Path.Combine(Data, "local-sync-state.json");
        Assert.True(File.Exists(syncStatePath));
        var store = SyncStateStore();
        var (state, notice) = await store.LoadAsync();
        Assert.NotNull(state);
        Assert.Null(notice);
        Assert.Equal(1, state.SchemaVersion);
        Assert.Equal(Repository.FullName, state.RepositoryIdentity);
        Assert.Equal(Repository.DefaultBranch, state.Branch);
        Assert.Equal(result.CandidateCommit, state.LastSynchronizedCommit);
        Assert.Equal(op.SourceRoot, state.LocalRootPath);
        Assert.Equal(op.Files.Count, state.Files.Count);
        Assert.Equal(op.CollectionFingerprint, state.CollectionFingerprint);
        Assert.NotEqual(default, state.SynchronizedAt);
    }

    [Fact]
    public async Task SyncStateMatchesCandidateCommitFromPublishedReceipt()
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server();
        var uploader = new CollectionUploader(Store(), Connection(server), SyncStateStore());
        var result = await uploader.UploadAsync(Request(op));
        Assert.Equal(UploadStatus.Published, result.Status);
        var syncStatePath = Path.Combine(Data, "local-sync-state.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(syncStatePath))!.AsObject();
        Assert.Equal(result.CandidateCommit, json["LastSynchronizedCommit"]!.GetValue<string>());
        Assert.Equal(Repository.FullName, json["RepositoryIdentity"]!.GetValue<string>());
        Assert.Equal(Repository.DefaultBranch, json["Branch"]!.GetValue<string>());
        Assert.Equal(op.SourceRoot, json["LocalRootPath"]!.GetValue<string>());
        Assert.Equal(op.CollectionFingerprint, json["CollectionFingerprint"]!.GetValue<string>());
    }

    [Fact]
    public async Task FailedUploadDoesNotPersistSyncState()
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server { LostResponse = true };
        await Assert.ThrowsAnyAsync<Exception>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore()).UploadAsync(Request(op)));
        Assert.False(File.Exists(Path.Combine(Data, "local-sync-state.json")));
    }

    [Fact]
    public async Task SyncStateIsVersionedAndFollowsStoreConventions()
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server();
        var uploader = new CollectionUploader(Store(), Connection(server), SyncStateStore());
        await uploader.UploadAsync(Request(op));
        var syncStatePath = Path.Combine(Data, "local-sync-state.json");
        var bytes = await File.ReadAllBytesAsync(syncStatePath);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("RepositoryIdentity", out _));
        Assert.True(document.RootElement.TryGetProperty("Branch", out _));
        Assert.True(document.RootElement.TryGetProperty("LastSynchronizedCommit", out _));
        Assert.True(document.RootElement.TryGetProperty("LocalRootPath", out _));
        Assert.True(document.RootElement.TryGetProperty("Files", out _));
        Assert.True(document.RootElement.TryGetProperty("SynchronizedAt", out _));
        Assert.True(document.RootElement.TryGetProperty("CollectionFingerprint", out _));
    }

    private static UploadRequest Bootstrap(PreparedOperation op, RepositoryBootstrapCase bootstrap, string pinned = "") =>
        new(op.OperationId, op.SourceRoot, op.CollectionFingerprint, Repository, pinned, bootstrap);

    [Fact]
    public async Task UnbornRepositoryIsSeededThroughContentsApiThenPublishedAsChildCommit()
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server("unborn");
        var uploader = new CollectionUploader(Store(), Connection(server), SyncStateStore());
        var result = await uploader.UploadAsync(Bootstrap(op, RepositoryBootstrapCase.UnbornRepository));
        Assert.Equal(UploadStatus.Published, result.Status);
        Assert.Equal(result.CandidateCommit, server.Head);
        // The first commit is a zero-parent seed created via the Contents API; the collection is its child commit.
        Assert.NotNull(server.Seed);
        Assert.Equal(server.Seed, server.Commits[server.Head!].Parent);
        var seedEntries = server.Expand(server.Commits[server.Seed!].Tree);
        Assert.Contains(seedEntries, e => e.Path == CloudStorage.ManifestPath);
        Assert.DoesNotContain(seedEntries, e => e.Path.StartsWith(CloudStorage.FilesPath, StringComparison.Ordinal));
        foreach (var file in op.Files)
            Assert.Contains(server.Expand(server.Commits[server.Head!].Tree), e => e.Path == CloudStorage.FilesPath + "/" + file.RelativePath);
        Assert.Contains(server.Writes, p => p == "PUT /repos/player/saves/contents/.pd2cloud/manifest.json");
        Assert.Contains(server.Writes, p => p.EndsWith("/git/commits", StringComparison.Ordinal));
        Assert.Single(server.Writes, p => p.StartsWith("PATCH", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Writes, p => p.StartsWith("POST /repos/player/saves/git/refs", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(Data, "local-sync-state.json")));
        Assert.False(EvidenceRemains(op));
    }

    [Fact]
    public async Task EmptyTreeRepositoryIsBootstrappedWithChildCommitAndNonForcedPatch()
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server("empty-tree");
        var result = await new CollectionUploader(Store(), Connection(server), SyncStateStore())
            .UploadAsync(Bootstrap(op, RepositoryBootstrapCase.EmptyTree, Parent));
        Assert.Equal(UploadStatus.Published, result.Status);
        Assert.Equal(Parent, server.Commits[result.CandidateCommit!].Parent);
        Assert.Single(server.Writes, p => p.StartsWith("PATCH", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Writes, p => p.StartsWith("POST /repos/player/saves/git/refs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnrelatedRepositoryIsAdoptedPreservingEveryUnrelatedEntryVerbatim()
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server("unrelated");
        var before = server.Expand(server.Commits[Parent].Tree).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        Assert.Contains(before, e => e.Path == "README.md" && e.Mode == "100755");
        Assert.Contains(before, e => e.Path == "other/link" && e.Mode == "120000");
        var result = await new CollectionUploader(Store(), Connection(server), SyncStateStore())
            .UploadAsync(Bootstrap(op, RepositoryBootstrapCase.Unrelated, Parent));
        Assert.Equal(UploadStatus.Published, result.Status);
        Assert.Equal(Parent, server.Commits[result.CandidateCommit!].Parent);
        var after = server.Expand(server.Commits[result.CandidateCommit!].Tree)
            .Where(e => !e.Path.StartsWith(CloudStorage.Root, StringComparison.Ordinal)).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        Assert.Equal(before, after);
        Assert.Contains(server.Expand(server.Commits[result.CandidateCommit!].Tree), e => e.Path == CloudStorage.ManifestPath);
        Assert.Single(server.Writes, p => p.StartsWith("PATCH", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unborn", RepositoryBootstrapCase.None)]
    [InlineData("unborn", RepositoryBootstrapCase.EmptyTree)]
    [InlineData("unborn", RepositoryBootstrapCase.Unrelated)]
    [InlineData("empty-tree", RepositoryBootstrapCase.UnbornRepository)]
    [InlineData("unrelated", RepositoryBootstrapCase.UnbornRepository)]
    [InlineData("incompatible", RepositoryBootstrapCase.UnbornRepository)]
    [InlineData("incompatible", RepositoryBootstrapCase.EmptyTree)]
    [InlineData("incompatible", RepositoryBootstrapCase.Unrelated)]
    [InlineData("managed", RepositoryBootstrapCase.UnbornRepository)]
    [InlineData("managed", RepositoryBootstrapCase.Unrelated)]
    public async Task BootstrapRequiresTheExactReviewedClassificationAndNeverWrites(string classification, RepositoryBootstrapCase bootstrap)
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server(classification);
        var pinned = bootstrap == RepositoryBootstrapCase.UnbornRepository ? "" : Parent;
        await Assert.ThrowsAsync<RecoveryBlockedException>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore())
            .UploadAsync(Bootstrap(op, bootstrap, pinned)));
        Assert.Empty(server.Writes);
        Assert.False(EvidenceRemains(op));
    }

    [Theory]
    [InlineData("unborn", RepositoryBootstrapCase.UnbornRepository)]
    [InlineData("empty-tree", RepositoryBootstrapCase.EmptyTree)]
    [InlineData("unrelated", RepositoryBootstrapCase.Unrelated)]
    public async Task LostBootstrapResponseLeavesPublicationWithoutReplayOrSyncRecord(string classification, RepositoryBootstrapCase bootstrap)
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server(classification) { LostResponse = true };
        var pinned = bootstrap == RepositoryBootstrapCase.UnbornRepository ? "" : Parent;
        await Assert.ThrowsAnyAsync<Exception>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore())
            .UploadAsync(Bootstrap(op, bootstrap, pinned)));
        Assert.Single(server.Writes, p => p.StartsWith("PATCH", StringComparison.Ordinal));
        Assert.Contains(server.Expand(server.Commits[server.Head!].Tree), e => e.Path == CloudStorage.ManifestPath);
        Assert.False(EvidenceRemains(op));
        Assert.False(File.Exists(Path.Combine(Data, "local-sync-state.json")));
    }

    [Fact]
    public async Task ConcurrentUpdateDuringUnbornBootstrapNeverReparentsOrOverwrites()
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server("unborn");
        var competitor = new string('b', 40);
        server.Commits[competitor] = server.Commits[Parent];
        server.Before = route => { if (route == "PATCH /repos/player/saves/git/refs/heads/main") server.Head = competitor; };
        await Assert.ThrowsAnyAsync<Exception>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore())
            .UploadAsync(Bootstrap(op, RepositoryBootstrapCase.UnbornRepository)));
        Assert.Equal(competitor, server.Head);
        Assert.False(File.Exists(Path.Combine(Data, "local-sync-state.json")));
    }

    [Fact]
    public async Task UnbornBootstrapSeedFailureNeverClaimsPublication()
    {
        var op = await Store().PrepareAsync(Source);
        var server = new Server("unborn") { RejectSeed = true };
        await Assert.ThrowsAsync<ConnectionException>(() => new CollectionUploader(Store(), Connection(server), SyncStateStore())
            .UploadAsync(Bootstrap(op, RepositoryBootstrapCase.UnbornRepository)));
        Assert.Null(server.Head);
        Assert.False(EvidenceRemains(op));
        Assert.False(File.Exists(Path.Combine(Data, "local-sync-state.json")));
    }

}
