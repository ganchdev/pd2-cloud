using System.Text.Json;
using PD2Cloud.Core;
using PD2Cloud.GitHub;
using PD2Cloud.Local;
using PD2Cloud.Remote;
using Xunit;

namespace PD2Cloud.Tests;

// Pull and replacement-push use the same fake server as the publication tests. No live account is used.
public sealed class ResolveWorkflowTests : IDisposable
{
    private static readonly RepositoryChoice Repository = new(1, "player/saves", "main");
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pd2-resolve-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Data => Path.Combine(root, "app");

    public ResolveWorkflowTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "nested"));
        File.WriteAllBytes(Path.Combine(Source, "nested", "save.any"), [0, 255, 128, 13, 10]);
        File.WriteAllBytes(Path.Combine(Source, ".hidden"), []);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class Credentials : ICredentialStore
    {
        public string? Value = JsonSerializer.Serialize(new ConnectionCredentials { ClientId = "client", AccessToken = "token", Repository = Repository });
        public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task WriteAsync(string value, CancellationToken ct) { Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct) { Value = null; return Task.CompletedTask; }
    }

    private static GitHubConnection Connection(CollectionUploadTests.Server server) =>
        new(new GitHubApi(new HttpClient(server), "client"), new Credentials());

    [Fact]
    public async Task PullBacksUpCurrentFolderThenInstallsVerifiedCloudBytesAndRecordsTheRevision()
    {
        var server = new CollectionUploadTests.Server("populated");
        var connection = Connection(server);
        var cloud = await new CloudCollectionReader(connection).ReadAsync(Repository, default);
        Assert.Equal(RepositoryContent.ManagedCollection, cloud.Content);
        var writes = server.Writes.Count;

        var puller = new CollectionPuller(new OperationPreparationStore(Data), connection, new LocalSyncStateStore(Data));
        var result = await puller.PullAsync(new PullRequest(Source, Repository, cloud.Commit!, cloud.Collection!.Manifest!.CollectionFingerprint!));

        // The cloud file was installed and the previous local-only files were removed.
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(Source, "existing")));
        Assert.False(File.Exists(Path.Combine(Source, "nested", "save.any")));
        Assert.False(File.Exists(Path.Combine(Source, ".hidden")));
        Assert.Equal(writes, server.Writes.Count);

        // A complete backup of the original local folder remains available.
        Assert.Equal([0, 255, 128, 13, 10], await File.ReadAllBytesAsync(Path.Combine(result.BackupDirectory, "nested", "save.any")));
        Assert.True(File.Exists(Path.Combine(new DirectoryInfo(result.BackupDirectory).Parent!.FullName, "backup.json")));
        Assert.True(result.BaselineRecorded);
        var (state, notice) = await new LocalSyncStateStore(Data).LoadAsync();
        Assert.Null(notice);
        Assert.NotNull(state);
        Assert.Equal(cloud.Commit, state!.LastSynchronizedCommit);
        Assert.Equal(cloud.Collection.Manifest.CollectionFingerprint, state.CollectionFingerprint);
        Assert.Equal(Source, state.LocalRootPath);
    }

    [Fact]
    public async Task ReplacementPushPublishesLocalSnapshotPreservesUnrelatedRepositoryEntriesAndUpdatesBaseline()
    {
        var server = new CollectionUploadTests.Server("populated");
        var unrelatedBefore = server.Expand(server.Commits[server.Head!].Tree)
            .Where(e => !e.Path.StartsWith(CloudStorage.Root, StringComparison.Ordinal))
            .OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        var pinned = server.Head!;
        var preparations = new OperationPreparationStore(Data);
        var op = await preparations.PrepareAsync(Source);
        var uploader = new CollectionUploader(preparations, Connection(server), new LocalSyncStateStore(Data));
        var result = await uploader.UploadAsync(new UploadRequest(op.OperationId, op.SourceRoot, op.CollectionFingerprint,
            Repository, pinned, RepositoryBootstrapCase.None, UploadMode.ReplaceManaged));

        Assert.Equal(UploadStatus.Published, result.Status);
        Assert.NotEqual(pinned, server.Head);
        Assert.Equal(pinned, server.Commits[server.Head!].Parent);
        var after = server.Expand(server.Commits[server.Head!].Tree);
        Assert.DoesNotContain(after, e => e.Path == CloudStorage.FilesPath + "/existing");
        foreach (var file in op.Files)
        {
            var entry = Assert.Single(after, e => e.Path == CloudStorage.FilesPath + "/" + file.RelativePath);
            Assert.Equal(File.ReadAllBytes(Path.Combine(Source, file.RelativePath)), server.Blobs[entry.Sha]);
        }
        Assert.Equal(unrelatedBefore, after.Where(e => !e.Path.StartsWith(CloudStorage.Root, StringComparison.Ordinal))
            .OrderBy(e => e.Path, StringComparer.Ordinal).ToArray());
        var (state, _) = await new LocalSyncStateStore(Data).LoadAsync();
        Assert.NotNull(state);
        Assert.Equal(result.CandidateCommit, state!.LastSynchronizedCommit);
        Assert.Equal(op.CollectionFingerprint, state.CollectionFingerprint);
    }

    [Fact]
    public async Task PullRejectsAChangedRevisionOrFingerprintBeforeAnyLocalWrite()
    {
        var server = new CollectionUploadTests.Server("populated");
        var connection = Connection(server);
        var cloud = await new CloudCollectionReader(connection).ReadAsync(Repository, default);
        var before = await File.ReadAllBytesAsync(Path.Combine(Source, "nested", "save.any"));
        var puller = new CollectionPuller(new OperationPreparationStore(Data), connection, new LocalSyncStateStore(Data));

        await Assert.ThrowsAsync<RecoveryBlockedException>(() => puller.PullAsync(
            new PullRequest(Source, Repository, new string('b', 40), cloud.Collection!.Manifest!.CollectionFingerprint!)));
        await Assert.ThrowsAsync<RecoveryBlockedException>(() => puller.PullAsync(
            new PullRequest(Source, Repository, cloud.Commit!, new string('0', 64))));

        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(Source, "nested", "save.any")));
        Assert.False(File.Exists(Path.Combine(Source, "existing")));
    }
}
