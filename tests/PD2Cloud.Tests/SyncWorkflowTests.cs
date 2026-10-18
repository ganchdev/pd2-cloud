using System.Security.Cryptography;
using System.Text.Json;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;
using PD2Cloud.Desktop.ViewModels;
using PD2Cloud.GitHub;
using PD2Cloud.Local;
using PD2Cloud.Remote;
using Xunit;

namespace PD2Cloud.Tests;

// Exercises the desktop Sync journey against the real coordinated upload/download APIs and the shared fake
// GitHub server. No live account or real save data is used.
public sealed class SyncWorkflowTests : IDisposable
{
    private static readonly RepositoryChoice Repository = new(1, "player/saves", "main");
    private static readonly string ParentCommit = new('a', 40);
    private static readonly byte[] SourceBytes = [0, 255, 128, 13, 10];
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pd2-sync-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string App => Path.Combine(root, "app");
    private string Prefs => Path.Combine(root, "prefs");

    public SyncWorkflowTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "nested"));
        File.WriteAllBytes(Path.Combine(Source, "nested", "save.any"), SourceBytes);
        File.WriteAllBytes(Path.Combine(Source, ".hidden"), []);
        Directory.CreateDirectory(Path.Combine(Source, "empty"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class Credentials(RepositoryChoice? repository) : ICredentialStore
    {
        public string? Value = JsonSerializer.Serialize(new ConnectionCredentials { ClientId = "client", AccessToken = "token", Repository = repository ?? Repository });
        public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task WriteAsync(string value, CancellationToken ct) { Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct) { Value = null; return Task.CompletedTask; }
    }
    private sealed class Picker(string path) : IFolderPicker { public Task<string?> PickAsync() => Task.FromResult<string?>(path); }

    private static GitHubConnection Connection(CollectionUploadTests.Server server, Credentials? credentials = null) =>
        new(new GitHubApi(new HttpClient(server), "client"), credentials ?? new Credentials(null));

    private Task<SetupWorkflowViewModel> CreateAsync(CollectionUploadTests.Server server) => CreateAsync(server, "");

    // Each "PC" has its own save folder, preferences and application data; they share only the repository.
    private async Task<SetupWorkflowViewModel> CreateAsync(CollectionUploadTests.Server server, string pc)
    {
        var source = Source + pc;
        Directory.CreateDirectory(source);
        var settings = new FolderSettingsStore(Prefs + pc);
        await settings.SaveFolderAsync(source);
        await settings.SaveSetupCompletedAsync();
        var connection = Connection(server);
        var github = new GitHubConnectionViewModel(connection);
        var local = new MainWindowViewModel(new CollectionScanner(), new Picker(source), settings, github);
        return new SetupWorkflowViewModel(local, new SyncOperations(connection, App + pc));
    }

    private string SyncStatePath => Path.Combine(App, "local-sync-state.json");
    private static int Patches(CollectionUploadTests.Server server) => server.Writes.Count(p => p.StartsWith("PATCH", StringComparison.Ordinal));
    private static string[] Evidence(string app) => Directory.Exists(Path.Combine(app, "recovery"))
        ? Directory.EnumerateFileSystemEntries(Path.Combine(app, "recovery")).Select(p => Path.GetFileName(p)).Where(n => n != "operations.lock").ToArray()
        : [];
    private static Dictionary<string, string> Contents(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(folder, f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));

    [Theory]
    [InlineData("unborn")]
    [InlineData("empty-tree")]
    [InlineData("unrelated")]
    [InlineData("managed")]
    public async Task FirstSyncSetsUpCloudStorageAndUploads(string classification)
    {
        var server = new CollectionUploadTests.Server(classification);
        var unrelated = server.Head is null ? [] : server.Expand(server.Commits[server.Head].Tree)
            .Where(e => !e.Path.StartsWith(CloudStorage.Root, StringComparison.Ordinal)).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();

        Assert.True(workflow.IsDashboard);
        Assert.Equal(classification == "managed" ? SyncState.Upload : SyncState.Initialize, workflow.Sync.State);
        Assert.Equal("Unsynced", workflow.SyncStatus);
        Assert.True(workflow.CanSync);
        Assert.Empty(server.Writes);

        await workflow.SyncAsync();
        Assert.False(workflow.IsSyncMessageError);
        Assert.Contains("Uploaded 2 files", workflow.SyncMessage);
        Assert.Equal(SyncState.InSync, workflow.Sync.State);
        Assert.Equal("In sync", workflow.SyncStatus);
        Assert.True(workflow.IsSyncHealthy);
        Assert.False(workflow.CanSync);
        Assert.Contains("Last synced", workflow.BaselineLine);
        Assert.Equal(1, Patches(server));
        Assert.True(File.Exists(SyncStatePath));
        Assert.Empty(Evidence(App));
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(Path.Combine(Source, "nested", "save.any")));
        var after = server.Expand(server.Commits[server.Head!].Tree);
        Assert.Equal(unrelated, after.Where(e => !e.Path.StartsWith(CloudStorage.Root, StringComparison.Ordinal)).OrderBy(e => e.Path, StringComparer.Ordinal));
    }

    [Fact]
    public async Task TwoComputersTakeTurnsWithOneSyncButton()
    {
        var server = new CollectionUploadTests.Server("unborn");
        var pc2Source = Source + "-pc2";

        // PC1 sets up cloud storage and uploads.
        using var pc1 = await CreateAsync(server);
        await pc1.InitializeAsync();
        await pc1.SyncAsync();
        Assert.Equal(SyncState.InSync, pc1.Sync.State);

        // PC1 plays, saves and quits.
        await File.WriteAllTextAsync(Path.Combine(Source, "nested", "save.any"), "pc1 progress");
        await pc1.RefreshAsync();
        Assert.Equal(SyncState.Upload, pc1.Sync.State);
        Assert.Equal("Unsynced", pc1.SyncStatus);
        await pc1.SyncAsync();
        Assert.Equal(SyncState.InSync, pc1.Sync.State);

        // PC2 starts with an empty save folder and downloads the cloud saves.
        using var pc2 = await CreateAsync(server, "-pc2");
        await pc2.InitializeAsync();
        Assert.Equal(SyncState.Download, pc2.Sync.State);
        Assert.Equal("Unsynced", pc2.SyncStatus);
        var writes = server.Writes.Count;
        await pc2.SyncAsync();
        Assert.Equal(writes, server.Writes.Count);
        Assert.Contains("Downloaded 2 files", pc2.SyncMessage);
        Assert.Equal(SyncState.InSync, pc2.Sync.State);
        Assert.Equal(Contents(Source), Contents(pc2Source));

        // PC2 plays, saves and uploads.
        await File.WriteAllTextAsync(Path.Combine(pc2Source, "nested", "save.any"), "pc2 progress");
        await File.WriteAllTextAsync(Path.Combine(pc2Source, "new-character.d2s"), "pc2 character");
        await pc2.RefreshAsync();
        Assert.Equal(SyncState.Upload, pc2.Sync.State);
        await pc2.SyncAsync();
        Assert.Equal(SyncState.InSync, pc2.Sync.State);

        // PC1 sees the newer cloud saves, downloads them and backs up what it had.
        await pc1.RefreshAsync();
        Assert.Equal(SyncState.Download, pc1.Sync.State);
        Assert.Equal("Unsynced", pc1.SyncStatus);
        await pc1.SyncAsync();
        Assert.Equal(SyncState.InSync, pc1.Sync.State);
        Assert.Equal("pc2 progress", await File.ReadAllTextAsync(Path.Combine(Source, "nested", "save.any")));
        Assert.Equal(Contents(pc2Source), Contents(Source));
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(App, "replacements")));
        Assert.Equal("pc1 progress", await File.ReadAllTextAsync(Path.Combine(backup, "files", "nested", "save.any")));

        // PC1 plays again; a deleted file is an ordinary change that reaches PC2 too.
        File.Delete(Path.Combine(Source, "new-character.d2s"));
        await File.WriteAllTextAsync(Path.Combine(Source, "nested", "save.any"), "pc1 again");
        await pc1.RefreshAsync();
        Assert.Equal(SyncState.Upload, pc1.Sync.State);
        await pc1.SyncAsync();
        await pc2.RefreshAsync();
        Assert.Equal(SyncState.Download, pc2.Sync.State);
        await pc2.SyncAsync();
        Assert.False(File.Exists(Path.Combine(pc2Source, "new-character.d2s")));
        Assert.Equal(Contents(Source), Contents(pc2Source));

        Assert.Empty(Evidence(App));
        Assert.Empty(Evidence(App + "-pc2"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangesOnBothSidesOpenRestoreToChooseWhichCopyToKeep(bool push)
    {
        // The cloud already holds different saves and this PC has never synced.
        var server = new CollectionUploadTests.Server("populated");
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        Assert.Equal(SyncState.Conflict, workflow.Sync.State);
        Assert.Equal("Unsynced", workflow.SyncStatus);

        // Sync never decides a conflict; it opens Restore. Leaving Restore changes nothing.
        await workflow.SyncAsync();
        Assert.True(workflow.IsRestore);
        Assert.True(workflow.CanPull);
        Assert.True(workflow.CanPush);
        await workflow.BackAsync();
        Assert.True(workflow.IsDashboard);
        Assert.Empty(server.Writes);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(Path.Combine(Source, "nested", "save.any")));

        workflow.OpenRestore();
        if (push) await workflow.PushAsync();
        else await workflow.PullAsync();

        Assert.True(workflow.IsDashboard);
        Assert.False(workflow.IsSyncMessageError);
        Assert.Equal(SyncState.InSync, workflow.Sync.State);
        var cloud = server.Expand(server.Commits[server.Head!].Tree);
        if (push)
        {
            Assert.Equal(1, Patches(server));
            Assert.Contains(cloud, e => e.Path == CloudStorage.FilesPath + "/nested/save.any");
            Assert.DoesNotContain(cloud, e => e.Path == CloudStorage.FilesPath + "/existing");
            Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(Path.Combine(Source, "nested", "save.any")));
        }
        else
        {
            Assert.Empty(server.Writes);
            Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(Source, "existing")));
            Assert.False(File.Exists(Path.Combine(Source, "nested", "save.any")));
            var backup = Assert.Single(Directory.GetDirectories(Path.Combine(App, "replacements")));
            Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(Path.Combine(backup, "files", "nested", "save.any")));
        }
    }

    [Fact]
    public async Task RestoreCanPullOrPushEvenWhenSyncHasNothingToDo()
    {
        var server = new CollectionUploadTests.Server();
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        await workflow.SyncAsync();
        Assert.Equal(SyncState.InSync, workflow.Sync.State);
        Assert.False(workflow.CanSync);
        Assert.True(workflow.CanRestore);

        // An explicit pull reinstalls the cloud copy over a local edit that Sync would otherwise upload.
        await File.WriteAllTextAsync(Path.Combine(Source, "nested", "save.any"), "unwanted local edit");
        await workflow.RefreshAsync();
        Assert.Equal(SyncState.Upload, workflow.Sync.State);
        workflow.OpenRestore();
        Assert.True(workflow.IsRestore);
        await workflow.PullAsync();
        Assert.True(workflow.IsDashboard);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(Path.Combine(Source, "nested", "save.any")));
        Assert.Equal(SyncState.InSync, workflow.Sync.State);
        Assert.Equal(1, Patches(server));
    }

    [Fact]
    public async Task UnreadableCloudSavesBlockSyncButRestoreCanReplaceThem()
    {
        var server = new CollectionUploadTests.Server("incompatible");
        var unrelated = server.Expand(server.Commits[server.Head!].Tree)
            .Where(e => !e.Path.StartsWith(CloudStorage.Root, StringComparison.Ordinal)).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        Assert.Equal(SyncState.Unavailable, workflow.Sync.State);
        Assert.False(workflow.CanSync);
        Assert.Contains("Use Restore", workflow.SyncDetail);
        await workflow.SyncAsync();
        Assert.Empty(server.Writes);

        workflow.OpenRestore();
        Assert.True(workflow.IsCloudUnreadable);
        Assert.False(workflow.CanPull);
        Assert.Contains("can't be verified", workflow.PullReason);
        Assert.True(workflow.CanPush);
        await workflow.PushAsync();

        Assert.True(workflow.IsDashboard);
        Assert.Equal(SyncState.InSync, workflow.Sync.State);
        var after = server.Expand(server.Commits[server.Head!].Tree);
        Assert.Equal(unrelated, after.Where(e => !e.Path.StartsWith(CloudStorage.Root, StringComparison.Ordinal)).OrderBy(e => e.Path, StringComparer.Ordinal));
        Assert.Contains(after, e => e.Path == CloudStorage.FilesPath + "/nested/save.any");
    }

    [Fact]
    public async Task LostUploadResponseIsRecognisedAsInSyncOnTheNextRefresh()
    {
        var server = new CollectionUploadTests.Server { LostResponse = true };
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        await workflow.SyncAsync();

        Assert.True(workflow.IsSyncMessageError);
        Assert.Empty(Evidence(App));
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(Path.Combine(Source, "nested", "save.any")));

        // The upload did land. Refresh sees matching files, records the sync and never uploads again.
        server.LostResponse = false;
        await workflow.RefreshAsync();
        Assert.Equal(SyncState.InSync, workflow.Sync.State);
        Assert.True(File.Exists(SyncStatePath));
        Assert.False(workflow.HasSyncMessage);
        Assert.Equal(1, Patches(server));
    }

    [Fact]
    public async Task RejectedUploadNeverOverwritesTheCloudOrRecordsASync()
    {
        var server = new CollectionUploadTests.Server { RejectUpdate = true };
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        var head = server.Head;
        await workflow.SyncAsync();

        Assert.True(workflow.IsSyncMessageError);
        Assert.Equal(head, server.Head);
        Assert.False(File.Exists(SyncStatePath));
        Assert.Empty(Evidence(App));
        Assert.Equal(SyncState.Upload, workflow.Sync.State);
        Assert.True(workflow.CanSync);
    }

    [Fact]
    public async Task StaleLeftoverEvidenceNeverBlocksSync()
    {
        // An operation left behind by a crash is discarded rather than requiring review.
        var server = new CollectionUploadTests.Server();
        var operations = new SyncOperations(Connection(server), App);
        await operations.PrepareAsync(Source, default);
        Assert.Single(Evidence(App));

        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        Assert.True(workflow.CanSync);
        await workflow.SyncAsync();
        Assert.Equal(SyncState.InSync, workflow.Sync.State);
        Assert.Empty(Evidence(App));
    }

    [Fact]
    public async Task StartupAndRefreshNeverWrite()
    {
        var server = new CollectionUploadTests.Server("unborn");
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        await workflow.RefreshAsync();
        await workflow.CheckCloudAsync();
        Assert.Empty(server.Writes);
        Assert.False(File.Exists(SyncStatePath));
    }

    [Fact]
    public async Task EmptySaveFolderHasNothingToUploadIntoEmptyStorage()
    {
        Directory.Delete(Source, true);
        Directory.CreateDirectory(Source);
        var server = new CollectionUploadTests.Server("unborn");
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        Assert.Equal(SyncState.NothingToSync, workflow.Sync.State);
        Assert.Equal("Nothing to sync yet", workflow.SyncStatus);
        Assert.False(workflow.CanSync);
        Assert.Contains("empty", workflow.SyncDetail);
    }
}
