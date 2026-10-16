using System.Net;
using System.Text.Json;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;
using PD2Cloud.Desktop.ViewModels;
using PD2Cloud.GitHub;
using PD2Cloud.Local;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class SetupWorkflowTests : IDisposable
{
    private readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pd2-setup-" + Guid.NewGuid().ToString("N"));
    private readonly Store store = new();
    private readonly Handler handler = new();
    private FolderSettingsStore Settings => new(Path.Combine(directory, "preferences"));
    private string Source => Path.Combine(directory, "saves");
    public SetupWorkflowTests() => Directory.CreateDirectory(Source);
    private SetupWorkflowViewModel Create(TimeProvider? clock = null) => new(new MainWindowViewModel(new CollectionScanner(), new Picker(Source), Settings,
        new GitHubConnectionViewModel(new GitHubConnection(new GitHubApi(new HttpClient(handler), "client", clock), store, clock))));
    private void Connected(bool selected = true) => store.Value = JsonSerializer.Serialize(new ConnectionCredentials
    {
        ClientId = "client",
        AccessToken = "synthetic",
        Repository = selected ? new(1, "player/saves", "main") : null
    });

    [Fact]
    public async Task NewUserStartsAtWelcomeAndCanGoBackFromConnect()
    {
        using var workflow = Create();
        await workflow.InitializeAsync();
        Assert.True(workflow.IsWelcome);
        await workflow.GetStartedAsync(); Assert.True(workflow.IsConnect);
        await workflow.BackAsync(); Assert.True(workflow.IsWelcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InterruptedConnectionAndRepositorySelectionResumeAtIncompleteStep()
    {
        Connected(selected: false);
        using (var first = Create())
        {
            await first.InitializeAsync(); Assert.True(first.IsRepository);
            first.GitHub.Candidate = first.GitHub.Repositories.First();
            await first.AcceptRepositoryAsync(); Assert.True(first.IsFolder);
        }
        using var resumed = Create();
        await resumed.InitializeAsync(); Assert.True(resumed.IsFolder);
        Assert.False(resumed.Local.SetupCompleted);
    }

    [Fact]
    public async Task CompletionPersistsAndRestartOpensDashboardEvenWhenOffline()
    {
        Connected();
        await File.WriteAllTextAsync(Path.Combine(Source, "hero.any"), "unchanged");
        using (var first = Create())
        {
            await first.InitializeAsync();
            Assert.False(first.CanContinueFolder);
            await first.Local.SelectFolderAsync();
            Assert.True(first.CanContinueFolder);
            await first.CompleteAsync();
            Assert.True(first.IsDashboard); Assert.Equal("Not yet synchronized", first.SyncStatus);
        }
        handler.Offline = true;
        using var restarted = Create();
        await restarted.InitializeAsync();
        Assert.True(restarted.IsDashboard); Assert.Equal("Needs attention", restarted.SyncStatus);
        Assert.Single(restarted.Local.Files);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(Path.Combine(Source, "hero.any")));
        Assert.All(handler.Requests, r => Assert.DoesNotContain("/contents", r));
    }

    [Fact]
    public async Task SettingsChangeRepositoryReturnsToSettingsAndLeavesFilesUntouched()
    {
        Connected();
        await File.WriteAllBytesAsync(Path.Combine(Source, "save"), [1, 2, 3]);
        using var workflow = Create();
        await workflow.InitializeAsync(); await workflow.Local.SelectFolderAsync();
        workflow.OpenSettings(); await workflow.ChangeRepositoryAsync();
        workflow.GitHub.Candidate = workflow.GitHub.Repositories.Single(r => r.Id == 2);
        await workflow.AcceptRepositoryAsync();
        Assert.True(workflow.IsGitHubSettings);
        Assert.Equal("player/other", workflow.RepositoryName);
        Assert.Equal("Not yet synchronized", workflow.SyncStatus);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(Source, "save")));
    }

    [Fact]
    public async Task DisconnectRequiresConfirmationAndPreservesFolderAndRepository()
    {
        Connected(); using var workflow = Create();
        await workflow.InitializeAsync(); await workflow.Local.SelectFolderAsync();
        await workflow.DisconnectAsync(); Assert.NotNull(store.Value);
        workflow.RequestDisconnect(); workflow.CancelDisconnect();
        await workflow.DisconnectAsync(); Assert.NotNull(store.Value);
        workflow.RequestDisconnect(); await workflow.DisconnectAsync();
        Assert.Null(store.Value); Assert.True(workflow.ShowDisconnected);
        Assert.Equal(Source, (await Settings.LoadAsync()).SelectedFolder);
        Assert.Equal(0, handler.Writes);
    }

    [Fact]
    public async Task FailedSelectionStaysInRepositoryFlowAndDoesNotClaimSuccess()
    {
        Connected(); using var workflow = Create();
        await workflow.InitializeAsync(); workflow.OpenSettings(); await workflow.ChangeRepositoryAsync();
        workflow.GitHub.Candidate = workflow.GitHub.Repositories.Single(r => r.Id == 2); store.FailWrite = true;
        await workflow.AcceptRepositoryAsync();
        Assert.True(workflow.IsRepository); Assert.Null(workflow.GitHub.SelectedRepository);
        Assert.Contains("unavailable", workflow.GitHub.Notice);
    }

    [Fact]
    public async Task EmptyListDoesNotClaimUserHasNoRepositoriesAndCanRefreshAfterGrantingAccess()
    {
        Connected(false); handler.Empty = true; using var workflow = Create();
        await workflow.InitializeAsync(); Assert.True(workflow.IsRepository);
        Assert.True(workflow.GitHub.ShowEmptyRepositories);
        Assert.Contains("manage GitHub access", workflow.GitHub.Notice);
        handler.Empty = false; await workflow.GitHub.ReloadAsync();
        Assert.True(workflow.GitHub.HasRepositories);
    }

    [Fact]
    public async Task CancelledAuthorizationCanReturnAndRetryWithoutLosingLocalFolder()
    {
        using var workflow = Create(); await workflow.InitializeAsync();
        await workflow.Local.SelectFolderAsync(); await workflow.GetStartedAsync();
        var connect = workflow.AuthorizeAsync();
        Assert.True(workflow.IsAuthorize); workflow.GitHub.Cancel(); await connect;
        Assert.True(workflow.ShowAuthorizationRetry);
        await workflow.BackAsync(); Assert.True(workflow.IsConnect);
        Assert.Equal(Source, workflow.Local.SelectedPath); Assert.Null(store.Value);
    }

    [Fact]
    public async Task SetupMarkerPreservesUnknownPreferencesAndRejectsDamagedDocuments()
    {
        await Settings.SaveFolderAsync(Source);
        var path = Path.Combine(directory, "preferences", "settings.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["OtherPreference"] = 42;
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await Settings.SaveSetupCompletedAsync();
        Assert.True((await Settings.LoadAsync()).SetupCompleted);
        Assert.Contains("OtherPreference", await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, "{broken");
        await Assert.ThrowsAnyAsync<JsonException>(() => Settings.SaveSetupCompletedAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task MissingFolderAfterCompletionStaysOnDashboardWithRecovery()
    {
        Connected(); await Settings.SaveFolderAsync(Source); await Settings.SaveSetupCompletedAsync();
        Directory.Delete(Source); using var workflow = Create();
        await workflow.InitializeAsync();
        Assert.True(workflow.IsDashboard); Assert.Equal("Needs attention", workflow.SyncStatus);
        Assert.True(workflow.Local.CanSelect); Assert.True(workflow.Local.CanRefresh);
    }

    [Fact]
    public async Task StartingSetupResumesAtConnectAfterRestart()
    {
        using (var first = Create()) { await first.InitializeAsync(); await first.GetStartedAsync(); }
        using var second = Create(); await second.InitializeAsync();
        Assert.True(second.IsConnect); Assert.False(second.Local.SetupCompleted);
    }

    [Fact]
    public async Task SuccessfulOnboardingAuthorizationGoesToRepositoryThenFolder()
    {
        handler.AuthorizeComplete = true;
        using var workflow = Create(new Clock());
        await workflow.InitializeAsync(); await workflow.GetStartedAsync();
        await workflow.AuthorizeAsync();
        Assert.True(workflow.IsRepository); Assert.True(workflow.GitHub.IsConnected);
        Assert.Equal("player", workflow.GitHub.Account);
        workflow.GitHub.Candidate = workflow.GitHub.Repositories.First();
        await workflow.AcceptRepositoryAsync(); Assert.True(workflow.IsFolder);
    }

    [Fact]
    public async Task SettingsConnectionUsesSameAuthorizationAndReturnsToSettings()
    {
        handler.AuthorizeComplete = true;
        using var workflow = Create(new Clock());
        await workflow.InitializeAsync(); workflow.OpenSettings(); workflow.BeginConnection();
        await workflow.AuthorizeAsync(); Assert.True(workflow.IsRepository);
        workflow.GitHub.Candidate = workflow.GitHub.Repositories.First();
        await workflow.AcceptRepositoryAsync(); Assert.True(workflow.IsGitHubSettings);
    }

    [Fact]
    public async Task ReconnectRestoresStillAccessibleRepositoryAndReturnsToSettings()
    {
        Connected(); handler.AuthorizeComplete = true;
        using var workflow = Create(new Clock());
        await workflow.InitializeAsync(); workflow.OpenSettings(); workflow.BeginConnection(reconnect: true);
        await workflow.AuthorizeAsync();
        Assert.True(workflow.IsGitHubSettings); Assert.Equal("player/saves", workflow.RepositoryName);
    }

    [Fact]
    public async Task FailedSetupPreferenceSaveCannotClaimCompletion()
    {
        Connected(); using var workflow = Create();
        await workflow.InitializeAsync(); await workflow.Local.SelectFolderAsync();
        var path = Path.Combine(directory, "preferences", "settings.json");
        await File.WriteAllTextAsync(path, "{broken");
        await workflow.CompleteAsync();
        Assert.True(workflow.IsFolder); Assert.False(workflow.Local.SetupCompleted);
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReturningDashboardDoesNotWaitForSlowGitHubOrOverrideSettingsNavigation()
    {
        Connected(); await Settings.SaveFolderAsync(Source); await Settings.SaveSetupCompletedAsync();
        handler.Stall = true; using var workflow = Create();
        var dashboard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.PropertyChanged += (_, _) => { if (workflow.IsDashboard) dashboard.TrySetResult(); };
        var startup = workflow.InitializeAsync();
        await dashboard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(workflow.GitHub.IsBusy); Assert.False(startup.IsCompleted);
        workflow.OpenSettings(SettingsSection.Saves);
        workflow.GitHub.Cancel(); await startup;
        Assert.True(workflow.IsSavesSettings); Assert.True(workflow.Local.Manifest!.IsValid);
    }

    [Fact]
    public async Task SlowCloudReadLeavesLocalRefreshUsableAndAccountChecksInvalidateIt()
    {
        Connected(); await Settings.SaveFolderAsync(Source); await Settings.SaveSetupCompletedAsync();
        handler.StallCloud = true;
        using var workflow = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.Cloud.PropertyChanged += (_, _) => { if (workflow.Cloud.IsBusy) started.TrySetResult(); };
        var startup = workflow.InitializeAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(workflow.IsDashboard); Assert.False(workflow.GitHub.IsBusy);
        await File.WriteAllBytesAsync(Path.Combine(Source, "save.bin"), [0, 255, 1]);
        await workflow.Local.RefreshAsync();
        Assert.Single(workflow.Local.Files); Assert.True(workflow.Local.Manifest!.IsValid);
        Assert.Null(workflow.Comparison);
        await workflow.GitHub.ReloadAsync(); await startup;
        Assert.Null(workflow.Cloud.Result); Assert.False(workflow.Cloud.IsBusy);
        Assert.Equal(new byte[] { 0, 255, 1 }, await File.ReadAllBytesAsync(Path.Combine(Source, "save.bin")));
        Assert.Equal(0, handler.Writes);
    }

    [Fact]
    public async Task CloudRetryInFolderAndSettingsDoesNotRescanOrChangePreferences()
    {
        Connected(); using var workflow = Create();
        await workflow.InitializeAsync(); await workflow.Local.SelectFolderAsync();
        var inspection = workflow.Local.Manifest;
        Assert.True(workflow.IsFolder); Assert.True(workflow.CanCheckCloud);
        handler.Requests.Clear(); await workflow.CheckCloudAsync();
        Assert.Same(inspection, workflow.Local.Manifest);
        Assert.DoesNotContain("/user", handler.Requests);
        Assert.Equal("Not yet synchronized", workflow.SyncStatus);
        workflow.OpenSettings(); await workflow.GitHub.ReloadAsync();
        Assert.Null(workflow.Cloud.Result); Assert.Null(workflow.Comparison);
        await workflow.CheckCloudAsync(); Assert.True(workflow.IsGitHubSettings);
        Assert.NotNull(workflow.Cloud.Result); Assert.Same(inspection, workflow.Local.Manifest);
        Assert.Equal(Source, (await Settings.LoadAsync()).SelectedFolder);
        Assert.Equal(0, handler.Writes);
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ThreadPool.QueueUserWorkItem(_ => { Interlocked.Add(ref ticks, dueTime.Ticks); callback(state); });
            return new TimerStub();
        }
        private sealed class TimerStub : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class Picker(string path) : IFolderPicker { public Task<string?> PickAsync() => Task.FromResult<string?>(path); }
    private sealed class Store : ICredentialStore
    {
        public string? Value;
        public bool FailWrite;
        public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task WriteAsync(string value, CancellationToken ct) { if (FailWrite) throw new IOException(); Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct) { Value = null; return Task.CompletedTask; }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool Offline, Empty, AuthorizeComplete, Stall, StallCloud;
        public List<string> Requests { get; } = [];
        public int Writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Requests.Add(path);
            if (request.Method != HttpMethod.Get && !path.StartsWith("/login/", StringComparison.Ordinal)) Writes++;
            if (Offline) throw new HttpRequestException();
            if (Stall) return Task.Delay(Timeout.Infinite, ct).ContinueWith<HttpResponseMessage>(_ => throw new OperationCanceledException(ct), TaskScheduler.Default);
            if (StallCloud && path == "/repos/player/saves/commits") return Task.Delay(Timeout.Infinite, ct).ContinueWith<HttpResponseMessage>(_ => throw new OperationCanceledException(ct), TaskScheduler.Default);
            if (path == "/login/device/code" && !AuthorizeComplete) return Task.Delay(Timeout.Infinite, ct).ContinueWith<HttpResponseMessage>(_ => throw new OperationCanceledException(ct), TaskScheduler.Default);
            var json = path switch
            {
                "/repos/player/saves" => """{"id":1,"full_name":"player/saves","default_branch":"main","private":true,"archived":false,"disabled":false,"permissions":{"push":true}}""",
                "/repos/player/other" => """{"id":2,"full_name":"player/other","default_branch":"main","private":true,"archived":false,"disabled":false,"permissions":{"push":true}}""",
                "/repos/player/saves/commits" or "/repos/player/other/commits" => "[]",
                "/login/device/code" => """{"device_code":"synthetic","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""",
                "/login/oauth/access_token" => """{"access_token":"synthetic","token_type":"bearer"}""",
                "/user" => """{"login":"player"}""",
                "/user/installations" => """{"installations":[{"id":12,"suspended_at":null,"permissions":{"contents":"write"}}]}""",
                _ => Empty ? """{"repositories":[]}""" : """{"repositories":[{"id":1,"full_name":"player/saves","default_branch":"main","private":true,"archived":false,"disabled":false,"permissions":{"push":true}},{"id":2,"full_name":"player/other","default_branch":"main","private":true,"archived":false,"disabled":false,"permissions":{"push":true}}]}"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
