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

// Exercises the desktop reviewed-upload and recovery journey against the real coordinated publication APIs
// and the shared fake GitHub server. No live account or real save data is used.
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

    private async Task<SetupWorkflowViewModel> CreateAsync(CollectionUploadTests.Server server)
    {
        var settings = new FolderSettingsStore(Prefs);
        await settings.SaveFolderAsync(Source);
        await settings.SaveSetupCompletedAsync();
        var connection = Connection(server);
        var github = new GitHubConnectionViewModel(connection);
        var local = new MainWindowViewModel(new CollectionScanner(), new Picker(Source), settings, github);
        return new SetupWorkflowViewModel(local, new SyncOperations(connection, App));
    }

    private string SyncStatePath => Path.Combine(App, "local-sync-state.json");

    [Fact]
    public async Task ReviewedUploadPublishesExactlyWhatWasReviewedAndNeverClaimsSynchronization()
    {
        Assert.False(File.Exists(SyncStatePath));
        var server = new CollectionUploadTests.Server();
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();

        Assert.True(workflow.IsDashboard);
        Assert.True(workflow.ShowSyncAction);
        Assert.True(workflow.CanSyncAll);
        Assert.DoesNotContain("Synced", workflow.SyncNotice, StringComparison.OrdinalIgnoreCase);

        workflow.BeginUpload();
        Assert.True(workflow.IsUploadReview);
        Assert.Equal(Repository.FullName, workflow.Sync.ReviewRepository);
        Assert.Equal(server.Head, workflow.Sync.ReviewCommit);
        Assert.Equal(2, workflow.Sync.ReviewFiles.Count);
        Assert.True(workflow.Sync.CanConfirm);

        await workflow.ConfirmUploadAsync();
        Assert.Equal(SyncOutcome.Uploaded, workflow.Sync.Outcome);
        Assert.Contains("Published revision", workflow.Sync.ResultMessage);
        Assert.DoesNotContain("Synced", workflow.Sync.ResultMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a verified two-device synchronization", workflow.Sync.ResultMessage);
        Assert.Equal(workflow.Sync.ResultCommit, server.Head);
        Assert.NotEqual(ParentCommit, server.Head);

        // Source bytes are preserved and a baseline is written only after successful publication.
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(Path.Combine(Source, "nested", "save.any")));
        Assert.True(File.Exists(SyncStatePath));
        var (state, notice) = await new LocalSyncStateStore(App).LoadAsync();
        Assert.NotNull(state);
        Assert.Null(notice);
        Assert.Equal(server.Head, state!.LastSynchronizedCommit);

        // Publication evidence is retained for review and no further upload is offered.
        Assert.True(workflow.Sync.HasRecoveryEvidence);
        Assert.False(workflow.CanSyncAll);
        Assert.Single(server.Writes, p => p.StartsWith("PATCH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaleReviewIsRejectedWithoutUploadingAnything()
    {
        var server = new CollectionUploadTests.Server();
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        workflow.BeginUpload();
        Assert.True(workflow.Sync.CanConfirm);

        // The reviewed local snapshot changes underneath the open review.
        await File.WriteAllTextAsync(Path.Combine(Source, "new.bin"), "edited");
        await workflow.Local.RefreshAsync();
        Assert.False(workflow.Sync.CanConfirm);

        await workflow.ConfirmUploadAsync();
        Assert.True(workflow.Sync.HasReviewNotice);
        Assert.Empty(server.Writes);
        Assert.False(File.Exists(SyncStatePath));
    }

    [Fact]
    public async Task UnverifiedBaselineAncestryBlocksTheUploadOffer()
    {
        // A saved baseline whose commit is not part of the remote history must never be trusted.
        var file = new FileEntry(".hidden", 0, Convert.ToHexStringLower(SHA256.HashData([])));
        await new LocalSyncStateStore(App).SaveSyncStateAsync(new LocalSyncState(1, Repository.FullName, Repository.DefaultBranch,
            new string('f', 40), Source, [file], DateTimeOffset.UtcNow, "fp"));
        var server = new CollectionUploadTests.Server();
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();

        Assert.True(workflow.Sync.HasBaseline);
        Assert.False(workflow.Sync.BaselineAncestryValid);
        Assert.False(workflow.CanSyncAll);
        Assert.Contains("no longer part of the remote history", workflow.SyncNotice);
        workflow.BeginUpload();
        Assert.False(workflow.IsUploadReview);
        Assert.Empty(server.Writes);
    }

    [Fact]
    public async Task ValidAncestorBaselineDoesNotOfferASecondUpload()
    {
        var server = new CollectionUploadTests.Server();
        var operations = new SyncOperations(Connection(server), App);
        var prepared = await operations.PrepareAsync(Source, default);
        var published = await operations.UploadAsync(new UploadRequest(prepared.OperationId, prepared.SourceRoot, prepared.CollectionFingerprint, Repository, server.Head!), default);
        Assert.Equal(UploadStatus.Published, published.Status);
        server.Head = published.CandidateCommit;

        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        Assert.True(workflow.Sync.HasBaseline);
        Assert.True(workflow.Sync.BaselineAncestryValid);
        Assert.False(workflow.CanSyncAll);
        Assert.Contains("Additional uploads are not available", workflow.Sync.OfferBlockedReason);
        Assert.DoesNotContain("Synced", workflow.SyncNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoveryScreenAssessesLostResponseWithoutReplayingOrWritingABaseline()
    {
        var server = new CollectionUploadTests.Server { LostResponse = true };
        var operations = new SyncOperations(Connection(server), App);
        var prepared = await operations.PrepareAsync(Source, default);
        await Assert.ThrowsAnyAsync<Exception>(() => operations.UploadAsync(new UploadRequest(prepared.OperationId, prepared.SourceRoot, prepared.CollectionFingerprint, Repository, server.Head!), default));
        Assert.False(File.Exists(SyncStatePath));

        var writes = server.Writes.Count;
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        Assert.True(workflow.Sync.HasRecoveryEvidence);
        Assert.False(workflow.CanSyncAll);

        await workflow.OpenRecoveryAsync();
        Assert.True(workflow.IsRecovery);
        Assert.Equal(UploadStatus.Published, workflow.Sync.RecoveryStatus);
        Assert.Contains("remote publication evidence only", workflow.Sync.RecoveryMessage);
        Assert.False(File.Exists(SyncStatePath));

        // Re-checking is read-only and idempotent.
        await workflow.AssessRecoveryAsync();
        Assert.Equal(UploadStatus.Published, workflow.Sync.RecoveryStatus);
        Assert.Equal(writes, server.Writes.Count);

        // The reviewed source is never replaced.
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(Path.Combine(Source, "nested", "save.any")));
    }

    [Fact]
    public async Task RecoveryCannotBeAssessedWithoutTheTargetRepositorySelected()
    {
        var server = new CollectionUploadTests.Server { LostResponse = true };
        var operations = new SyncOperations(Connection(server), App);
        var prepared = await operations.PrepareAsync(Source, default);
        await Assert.ThrowsAnyAsync<Exception>(() => operations.UploadAsync(new UploadRequest(prepared.OperationId, prepared.SourceRoot, prepared.CollectionFingerprint, Repository, server.Head!), default));

        // The account can no longer see the repository the evidence belongs to.
        var settings = new FolderSettingsStore(Prefs);
        var connection = Connection(server, new Credentials(new RepositoryChoice(2, "player/other", "main")));
        var github = new GitHubConnectionViewModel(connection);
        var local = new MainWindowViewModel(new CollectionScanner(), new Picker(Source), settings, github);
        using var workflow = new SetupWorkflowViewModel(local, new SyncOperations(connection, App));
        await workflow.InitializeAsync();
        Assert.True(workflow.Sync.HasRecoveryEvidence);
        Assert.Null(workflow.GitHub.SelectedRepository);

        await workflow.OpenRecoveryAsync();
        Assert.Null(workflow.Sync.RecoveryStatus);
        Assert.True(workflow.Sync.HasRecoveryNotice);
    }

    [Fact]
    public async Task UploadFailurePreservesFilesAndDoesNotFabricateABaseline()
    {
        var server = new CollectionUploadTests.Server { LostResponse = true };
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        workflow.BeginUpload();
        await workflow.ConfirmUploadAsync();

        Assert.Equal(SyncOutcome.Interrupted, workflow.Sync.Outcome);
        Assert.DoesNotContain("Synced", workflow.Sync.ResultMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(SyncStatePath));
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(Path.Combine(Source, "nested", "save.any")));
    }

    [Fact]
    public async Task UnbornRepositoryOffersReviewedInitializationAndPublishesTheFirstCollection()
    {
        var server = new CollectionUploadTests.Server("unborn");
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();

        Assert.True(workflow.IsDashboard);
        Assert.True(workflow.CanSyncAll);
        Assert.Equal("Initialize Repository", workflow.SyncActionLabel);
        Assert.Contains("empty", workflow.SyncNotice, StringComparison.OrdinalIgnoreCase);

        workflow.BeginUpload();
        Assert.True(workflow.IsUploadReview);
        Assert.True(workflow.Sync.IsBootstrapCase);
        Assert.Contains("Initialize", workflow.Sync.ReviewBootstrapTitle);
        Assert.True(workflow.Sync.CanConfirm);

        await workflow.ConfirmUploadAsync();
        Assert.Equal(SyncOutcome.Uploaded, workflow.Sync.Outcome);
        Assert.Equal("Repository initialized", workflow.Sync.ResultTitle);
        Assert.DoesNotContain("Synced", workflow.Sync.ResultMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a verified two-device synchronization", workflow.Sync.ResultMessage);
        Assert.NotNull(server.Head);
        // The first commit (no parent) is created by the Contents API; the collection is published as its child.
        Assert.NotNull(server.Seed);
        Assert.Equal(server.Seed, server.Commits[server.Head!].Parent);
        Assert.True(File.Exists(SyncStatePath));
        Assert.False(workflow.CanSyncAll);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(Path.Combine(Source, "nested", "save.any")));
    }

    [Fact]
    public async Task UnrelatedRepositoryRequiresExplicitAdoptionAcknowledgement()
    {
        var server = new CollectionUploadTests.Server("unrelated");
        var before = server.Expand(server.Commits[ParentCommit].Tree).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();

        Assert.True(workflow.CanSyncAll);
        Assert.Equal("Review Adoption", workflow.SyncActionLabel);
        workflow.BeginUpload();
        Assert.True(workflow.IsUploadReview);
        Assert.True(workflow.Sync.IsBootstrapCase);
        Assert.True(workflow.Sync.RequiresAdoptionAcknowledgement);
        Assert.False(workflow.Sync.CanConfirm);

        workflow.Sync.AdoptionAcknowledged = true;
        Assert.True(workflow.Sync.CanConfirm);
        await workflow.ConfirmUploadAsync();
        Assert.Equal(SyncOutcome.Uploaded, workflow.Sync.Outcome);
        Assert.Equal("Repository adopted", workflow.Sync.ResultTitle);
        Assert.DoesNotContain("Synced", workflow.Sync.ResultMessage, StringComparison.OrdinalIgnoreCase);
        var after = server.Expand(server.Commits[server.Head!].Tree)
            .Where(e => !e.Path.StartsWith(CloudStorage.Root, StringComparison.Ordinal)).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        Assert.Equal(before, after);
        Assert.True(File.Exists(SyncStatePath));
    }

    [Fact]
    public async Task EmptyTreeRepositoryIsBootstrappedOnlyAfterReview()
    {
        var server = new CollectionUploadTests.Server("empty-tree");
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        Assert.True(workflow.CanSyncAll);
        Assert.Equal("Create First Collection", workflow.SyncActionLabel);

        workflow.BeginUpload();
        Assert.True(workflow.Sync.IsBootstrapCase);
        Assert.Contains("first collection", workflow.Sync.ReviewBootstrapTitle, StringComparison.OrdinalIgnoreCase);
        await workflow.ConfirmUploadAsync();
        Assert.Equal(SyncOutcome.Uploaded, workflow.Sync.Outcome);
        Assert.Equal(ParentCommit, server.Commits[server.Head!].Parent);
    }

    [Fact]
    public async Task BootstrapIsNeverOfferedOrWrittenFromStartupSelectionOrRefresh()
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
    public async Task IncompatibleRepositoryNeverOffersBootstrap()
    {
        var server = new CollectionUploadTests.Server("incompatible");
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        Assert.False(workflow.CanSyncAll);
        workflow.BeginUpload();
        Assert.False(workflow.IsUploadReview);
        Assert.Empty(server.Writes);
    }

    [Fact]
    public async Task BootstrapReviewIsDroppedWhenTheClassificationChangesUnderneath()
    {
        var server = new CollectionUploadTests.Server("unborn");
        using var workflow = await CreateAsync(server);
        await workflow.InitializeAsync();
        workflow.BeginUpload();
        Assert.True(workflow.Sync.CanConfirm);

        // The repository is no longer unborn once a branch appears; the reviewed case no longer matches.
        server.Head = ParentCommit;
        await workflow.CheckCloudAsync();
        Assert.False(workflow.Sync.CanConfirm);
        await workflow.ConfirmUploadAsync();
        Assert.True(workflow.Sync.HasReviewNotice);
        Assert.Empty(server.Writes);
        Assert.False(File.Exists(SyncStatePath));
    }
}
