using System.Text.Json;
using System.Text.Json.Nodes;
using PD2Cloud.Core;
using PD2Cloud.Local;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class LocalSyncStateStoreTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pd2-syncstate-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "app");
    private string StatePath => Path.Combine(Data, "local-sync-state.json");
    public LocalSyncStateStoreTests() => Directory.CreateDirectory(Data);
    public void Dispose() => Directory.Delete(root, true);
    private LocalSyncStateStore Store() => new(Data);
    private static LocalSyncState ValidState() => new(1, "player/saves", "main",
        new string('a', 40), "/saves",
        [new FileEntry("save.dat", 100, new string('b', 64))],
        DateTimeOffset.UnixEpoch,
        CollectionInventory.Fingerprint([new FileEntry("save.dat", 100, new string('b', 64))]));

    [Fact]
    public async Task LoadReturnsNullWhenFileMissing()
    {
        var (state, notice) = await Store().LoadAsync();
        Assert.Null(state);
        Assert.Null(notice);
    }

    [Fact]
    public async Task SaveAndLoadRoundTripPreservesAllFields()
    {
        var state = ValidState();
        await Store().SaveSyncStateAsync(state);
        var (loaded, notice) = await Store().LoadAsync();
        Assert.NotNull(loaded);
        Assert.Null(notice);
        Assert.Equal(state.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(state.RepositoryIdentity, loaded.RepositoryIdentity);
        Assert.Equal(state.Branch, loaded.Branch);
        Assert.Equal(state.LastSynchronizedCommit, loaded.LastSynchronizedCommit);
        Assert.Equal(state.LocalRootPath, loaded.LocalRootPath);
        Assert.Equal(state.Files.Count, loaded.Files.Count);
        Assert.Equal(state.SynchronizedAt, loaded.SynchronizedAt);
        Assert.Equal(state.CollectionFingerprint, loaded.CollectionFingerprint);
    }

    [Fact]
    public async Task SavePublishesCompleteJsonWithSchemaVersion()
    {
        await Store().SaveSyncStateAsync(ValidState());
        var document = JsonNode.Parse(await File.ReadAllTextAsync(StatePath))!.AsObject();
        Assert.Equal(1, document["SchemaVersion"]!.GetValue<int>());
        Assert.Equal("player/saves", document["RepositoryIdentity"]!.GetValue<string>());
        Assert.Equal("main", document["Branch"]!.GetValue<string>());
        Assert.Equal(new string('a', 40), document["LastSynchronizedCommit"]!.GetValue<string>());
        Assert.Equal("/saves", document["LocalRootPath"]!.GetValue<string>());
        Assert.NotNull(document["Files"]);
        Assert.NotNull(document["SynchronizedAt"]);
        Assert.NotNull(document["CollectionFingerprint"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"SchemaVersion\":0,")]
    [InlineData("\"SchemaVersion\":2,")]
    public async Task DamagedOrFutureVersionsPreserveExistingFileAndReturnNotice(string versionPrefix)
    {
        await Store().SaveSyncStateAsync(ValidState());
        await File.WriteAllTextAsync(StatePath, "{" + versionPrefix + "\"RepositoryIdentity\":\"repo\"}");
        var damaged = await File.ReadAllBytesAsync(StatePath);
        var (state, notice) = await Store().LoadAsync();
        Assert.Null(state);
        Assert.NotNull(notice);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(StatePath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"SchemaVersion\":\"bad\"}")]
    [InlineData("{\"SchemaVersion\":1,\"RepositoryIdentity\":123}")]
    public async Task MalformedSyncStatePreservesFileAndReturnsNotice(string json)
    {
        await File.WriteAllTextAsync(StatePath, json);
        var (state, notice) = await Store().LoadAsync();
        Assert.Null(state);
        Assert.NotNull(notice);
        Assert.Equal(json, await File.ReadAllTextAsync(StatePath));
    }

    [Fact]
    public async Task UnknownFieldsOnLoadAreIgnoredAndStateIsReturned()
    {
        var state = ValidState();
        await Store().SaveSyncStateAsync(state);
        var json = await File.ReadAllTextAsync(StatePath);
        var modified = json.Replace("\"CollectionFingerprint\":\"", "\"CollectionFingerprint\":\"");
        await File.WriteAllTextAsync(StatePath, modified);
        var (loaded, notice) = await Store().LoadAsync();
        Assert.NotNull(loaded);
        Assert.Null(notice);
        Assert.Equal(state.CollectionFingerprint, loaded!.CollectionFingerprint);
    }

    [Fact]
    public async Task LinkedSyncStateFileIsNotFollowedOrReplaced()
    {
        if (OperatingSystem.IsWindows()) return;
        var external = Path.Combine(root, "external.json");
        await File.WriteAllTextAsync(external, "{}");
        File.CreateSymbolicLink(StatePath, external);
        var store = Store();
        var (state, notice) = await store.LoadAsync();
        Assert.Null(state);
        Assert.NotNull(notice);
        await Assert.ThrowsAsync<IOException>(() => store.SaveSyncStateAsync(ValidState()));
        Assert.Equal("{}", await File.ReadAllTextAsync(external));
        Assert.NotNull(new FileInfo(StatePath).LinkTarget);
    }

    [Fact]
    public async Task SourceOverlapIsRejectedBeforeSave()
    {
        var store = Store();
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSyncStateAsync(ValidState() with { LocalRootPath = root }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSyncStateAsync(ValidState() with { LocalRootPath = Data }));
    }

    [Fact]
    public async Task FailedReplacementKeepsExistingDocumentAndCleansTemporaryFile()
    {
        var store = Store();
        await store.SaveSyncStateAsync(ValidState());
        var before = await File.ReadAllBytesAsync(StatePath);
        if (OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(Data);
        try
        {
            File.SetUnixFileMode(Data, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => store.SaveSyncStateAsync(ValidState()));
            Assert.Equal(before, await File.ReadAllBytesAsync(StatePath));
            Assert.Single(Directory.GetFiles(Data));
        }
        finally { File.SetUnixFileMode(Data, mode); }
    }

    [Fact]
    public async Task SaveRejectsInvalidState()
    {
        var store = Store();
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSyncStateAsync(new LocalSyncState(0, "repo", "main", "commit", "/saves", [], DateTimeOffset.UnixEpoch, "")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSyncStateAsync(new LocalSyncState(2, "repo", "main", "commit", "/saves", [], DateTimeOffset.UnixEpoch, "fp")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSyncStateAsync(new LocalSyncState(1, "", "main", "commit", "/saves", [], DateTimeOffset.UnixEpoch, "fp")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSyncStateAsync(new LocalSyncState(1, "repo", "", "commit", "/saves", [], DateTimeOffset.UnixEpoch, "fp")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSyncStateAsync(new LocalSyncState(1, "repo", "main", "", "/saves", [], DateTimeOffset.UnixEpoch, "fp")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSyncStateAsync(new LocalSyncState(1, "repo", "main", "commit", "", [], DateTimeOffset.UnixEpoch, "fp")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSyncStateAsync(new LocalSyncState(1, "repo", "main", "commit", "/saves", null!, DateTimeOffset.UnixEpoch, "fp")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSyncStateAsync(new LocalSyncState(1, "repo", "main", "commit", "/saves", [], default, "fp")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSyncStateAsync(new LocalSyncState(1, "repo", "main", "commit", "/saves", [], DateTimeOffset.UnixEpoch, "")));
    }

    [Fact]
    public async Task IncompleteSyncStateMissingFingerprintReturnsNotice()
    {
        await File.WriteAllTextAsync(StatePath, """{"SchemaVersion":1,"RepositoryIdentity":"repo","Branch":"main","LastSynchronizedCommit":"commit","LocalRootPath":"/saves","Files":[],"SynchronizedAt":"2026-10-15T10:00:00Z"}""");
        var (state, notice) = await Store().LoadAsync();
        Assert.Null(state);
        Assert.NotNull(notice);
    }

    [Fact]
    public async Task VerifyAncestryReturnsStateWhenVerificationSucceeds()
    {
        var state = ValidState();
        await Store().SaveSyncStateAsync(state);
        var (loaded, notice) = await Store().VerifyAncestryAsync(_ => Task.FromResult(true));
        Assert.NotNull(loaded);
        Assert.Null(notice);
        Assert.Equal(state.LastSynchronizedCommit, loaded!.LastSynchronizedCommit);
    }

    [Fact]
    public async Task VerifyAncestryReturnsNoticeWhenVerificationFails()
    {
        var state = ValidState();
        await Store().SaveSyncStateAsync(state);
        var (loaded, notice) = await Store().VerifyAncestryAsync(_ => Task.FromResult(false));
        Assert.Null(loaded);
        Assert.NotNull(notice);
        Assert.Contains("ancestor", notice);
    }

    [Fact]
    public async Task VerifyAncestryReturnsNullStateAndNoticeWhenFileMissing()
    {
        var (loaded, notice) = await Store().VerifyAncestryAsync(_ => Task.FromResult(true));
        Assert.Null(loaded);
        Assert.Null(notice);
    }

    [Fact]
    public async Task VerifyAncestryReturnsNoticeWhenStateIsCorrupt()
    {
        await File.WriteAllTextAsync(StatePath, "{invalid");
        var (loaded, notice) = await Store().VerifyAncestryAsync(_ => Task.FromResult(true));
        Assert.Null(loaded);
        Assert.NotNull(notice);
    }
}
