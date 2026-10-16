using System.Net;
using System.Text;
using System.Text.Json;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;
using PD2Cloud.Desktop.ViewModels;
using PD2Cloud.GitHub;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class GitHubConnectionTests
{
    private const string Device = """{"device_code":"secret-device","user_code":"ABCD-EFGH","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""";
    private const string Token = """{"access_token":"secret-access","refresh_token":"secret-refresh","token_type":"bearer","expires_in":28800,"refresh_token_expires_in":15897600}""";
    private const string Installations = """{"installations":[{"id":12,"suspended_at":null,"permissions":{"contents":"write"}}]}""";
    private static string Repo(long id = 1, bool privacy = true, bool push = true, bool archived = false) => JsonSerializer.Serialize(new
    {
        id,
        full_name = $"owner/repo{id}",
        default_branch = "main",
        @private = privacy,
        archived,
        disabled = false,
        permissions = new { push }
    });
    private static string Repos(params string[] repos) => "{\"repositories\":[" + string.Join(',', repos) + "]}";
    private static ConnectionCredentials Credentials(DateTimeOffset? expires = null) => new()
    {
        ClientId = "client",
        AccessToken = "secret-access",
        RefreshToken = "secret-refresh",
        ExpiresAt = expires,
        RefreshExpiresAt = DateTimeOffset.UtcNow.AddMonths(1),
        Repository = new(1, "owner/repo1", "main")
    };

    private sealed class Store : ICredentialStore
    {
        public string? Value;
        public bool FailRead, FailWrite, FailDelete;
        public int Writes, Deletes;
        public Task<string?> ReadAsync(CancellationToken ct) => FailRead ? Task.FromException<string?>(new IOException("secret-error")) : Task.FromResult(Value);
        public Task WriteAsync(string value, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailWrite) throw new IOException("secret-error");
            Value = value; Writes++; return Task.CompletedTask;
        }
        public Task DeleteAsync(CancellationToken ct)
        {
            if (FailDelete) throw new IOException("secret-error");
            Value = null; Deletes++; return Task.CompletedTask;
        }
    }
    private sealed class Handler(params string[] replies) : HttpMessageHandler
    {
        public readonly Queue<string> Replies = new(replies);
        public readonly List<(HttpMethod Method, string Url, string Body, string? Token)> Requests = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Func<CancellationToken, Task>? Pause;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct), request.Headers.Authorization?.Parameter));
            if (Pause is not null) await Pause(ct);
            return new(Status) { Content = new StringContent(Replies.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }
    // Advances only when a requested delay fires; no wall-clock sleeps in polling tests.
    private sealed class Clock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public List<TimeSpan> Delays { get; } = [];
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            var timer = new TimerStub();
            ThreadPool.QueueUserWorkItem(_ => { Interlocked.Add(ref ticks, dueTime.Ticks); callback(state); });
            return timer;
        }
        private sealed class TimerStub : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private static GitHubConnection Connection(Handler handler, Store store, TimeProvider? clock = null) => new(new GitHubApi(new HttpClient(handler), "client", clock), store, clock);

    [Fact]
    public async Task DevicePollingHonorsPendingAndCumulativeSlowdownAndPersistsOnlyAfterAccountValidation()
    {
        var handler = new Handler(Device, """{"error":"authorization_pending"}""", """{"error":"slow_down"}""", """{"error":"slow_down"}""", Token, """{"login":"alice"}""");
        var store = new Store(); var clock = new Clock();
        DeviceAuthorization? shown = null;
        await Connection(handler, store, clock).ConnectAsync(d => shown = d, default);
        Assert.Equal("ABCD-EFGH", shown!.UserCode);
        Assert.Equal(new[] { 5, 5, 10, 15 }, clock.Delays.Select(d => (int)d.TotalSeconds));
        Assert.Equal(1, store.Writes);
        Assert.Contains("secret-refresh", store.Value);
        Assert.Equal(HttpMethod.Get, handler.Requests.Last().Method);
        Assert.All(handler.Requests.Take(5), r => Assert.Null(r.Token));
        Assert.DoesNotContain(handler.Requests, r => r.Body.Contains("client_secret"));
        Assert.Contains("device_code=secret-device", handler.Requests[1].Body);
    }

    [Theory]
    [InlineData("access_denied", "declined")]
    [InlineData("expired_token", "expired")]
    [InlineData("device_flow_disabled", "enabled")]
    [InlineData("incorrect_client_credentials", "rejected")]
    [InlineData("bad_verification_code", "failed")]
    public async Task DeviceTerminalErrorsDoNotSaveCredentials(string error, string expected)
    {
        var store = new Store(); var handler = new Handler(Device, JsonSerializer.Serialize(new { error }));
        var exception = await Assert.ThrowsAsync<ConnectionException>(() => Connection(handler, store, new Clock()).ConnectAsync(_ => { }, default));
        Assert.Contains(expected, exception.Message);
        Assert.Null(store.Value); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task DeviceLocalExpiryStopsBeforeAnotherPoll()
    {
        var handler = new Handler(Device.Replace("900", "5")); var store = new Store();
        await Assert.ThrowsAsync<ConnectionException>(() => Connection(handler, store, new Clock()).ConnectAsync(_ => { }, default));
        Assert.Single(handler.Requests); Assert.Null(store.Value);
    }

    [Fact]
    public async Task DeviceCancellationDoesNotPersistOrPoll()
    {
        var handler = new Handler(Device); var store = new Store(); using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Connection(handler, store).ConnectAsync(_ => cancel.Cancel(), cancel.Token));
        Assert.Single(handler.Requests); Assert.Null(store.Value);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SecureStoreFailureNeverCreatesSessionFallback(bool readFails, bool writeFails)
    {
        var store = new Store { FailRead = readFails, FailWrite = writeFails };
        var handler = new Handler(Device, Token, """{"login":"alice"}""");
        await Assert.ThrowsAsync<IOException>(() => Connection(handler, store, new Clock()).ConnectAsync(_ => { }, default));
        Assert.Null(store.Value);
        Assert.Equal(readFails ? 0 : 3, handler.Requests.Count);
    }

    [Fact]
    public async Task PrivateWritableAppRepositoriesOnlyAndSelectionRevalidates()
    {
        var store = new Store { Value = JsonSerializer.Serialize(Credentials()) };
        var handler = new Handler("""{"login":"alice"}""", Installations, Repos(Repo(), Repo(2, privacy: false), Repo(3, push: false), Repo(4, archived: true)), Installations, Repos(Repo(1, privacy: false)));
        var connection = Connection(handler, store);
        var overview = await connection.LoadAsync(default);
        Assert.Equal(1, Assert.Single(overview!.Repositories).Id);
        Assert.NotNull(overview.Selected);
        await Assert.ThrowsAsync<ConnectionException>(() => connection.SelectAsync(1, default));
        Assert.Equal(0, store.Writes);
        Assert.All(handler.Requests, r => { Assert.Equal(HttpMethod.Get, r.Method); Assert.Equal("secret-access", r.Token); });
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("/contents") || r.Url.Contains("/git/"));
    }

    [Fact]
    public async Task SelectionPersistsStableIdentityAndDefaultBranchAndRestoresThroughNewService()
    {
        var store = new Store { Value = JsonSerializer.Serialize(Credentials()) };
        var first = Connection(new Handler(Installations, Repos(Repo(2))), store);
        Assert.Equal(2, (await first.SelectAsync(2, default)).Id);
        var second = Connection(new Handler("""{"login":"alice"}""", Installations, Repos(Repo(2))), store);
        var overview = await second.LoadAsync(default);
        Assert.Equal(new RepositoryChoice(2, "owner/repo2", "main"), overview!.Selected);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task StaleSelectionIsNotPresentedAsVerifiedAndNoSilentPreferenceWrite()
    {
        var store = new Store { Value = JsonSerializer.Serialize(Credentials()) };
        var overview = await Connection(new Handler("""{"login":"alice"}""", Installations, Repos(Repo(2))), store).LoadAsync(default);
        Assert.Null(overview!.Selected); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task SuspendedAndReadOnlyInstallationsNeverOfferRepositories()
    {
        var handler = new Handler("""{"login":"alice"}""", """{"installations":[{"id":1,"suspended_at":"today","permissions":{"contents":"write"}},{"id":2,"suspended_at":null,"permissions":{"contents":"read"}}]}""");
        var overview = await Connection(handler, new Store { Value = JsonSerializer.Serialize(Credentials()) }).LoadAsync(default);
        Assert.Empty(overview!.Repositories); Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PaginationDoesNotStopAtFirstFullPage()
    {
        var hundred = Enumerable.Range(1, 100).Select(i => Repo(i)).ToArray();
        var handler = new Handler("""{"login":"alice"}""", Installations, Repos(hundred), Repos(Repo(101)));
        var overview = await Connection(handler, new Store { Value = JsonSerializer.Serialize(Credentials()) }).LoadAsync(default);
        Assert.Equal(101, overview!.Repositories.Count);
        Assert.EndsWith("per_page=100&page=2", handler.Requests.Last().Url);
    }

    [Fact]
    public async Task ConcurrentLoadsSerializeRefreshAndPersistRotationBeforeUsingNewToken()
    {
        var store = new Store { Value = JsonSerializer.Serialize(Credentials(DateTimeOffset.UtcNow.AddSeconds(-1))) };
        var handler = new Handler(Token.Replace("secret-access", "rotated-access"), """{"login":"alice"}""", Installations, Repos(Repo()), """{"login":"alice"}""", Installations, Repos(Repo()));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        handler.Pause = async _ => { if (Interlocked.Increment(ref requestCount) == 1) { started.SetResult(); await release.Task; } };
        var connection = Connection(handler, store);
        var first = connection.LoadAsync(default);
        await started.Task;
        var second = connection.LoadAsync(default);
        Assert.Single(handler.Requests);
        Assert.Equal(0, store.Writes);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.All(handler.Requests.Skip(1), r => Assert.Equal("rotated-access", r.Token));
        Assert.Contains("refresh_token=secret-refresh", handler.Requests[0].Body);
        Assert.DoesNotContain("client_secret", handler.Requests[0].Body);
        Assert.Equal(1, store.Writes);
        Assert.Contains("rotated-access", store.Value);
    }

    [Fact]
    public async Task RotationStorageFailureBlocksAllMetadataRequests()
    {
        var store = new Store { Value = JsonSerializer.Serialize(Credentials(DateTimeOffset.UtcNow.AddSeconds(-1))), FailWrite = true };
        var handler = new Handler(Token);
        await Assert.ThrowsAsync<IOException>(() => Connection(handler, store).LoadAsync(default));
        Assert.Single(handler.Requests); Assert.Contains("secret-access", store.Value);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailuresDoNotExposeResponseSecretsOrMasqueradeAsEmpty(int status)
    {
        var handler = new Handler("secret-response") { Status = (HttpStatusCode)status };
        var exception = await Assert.ThrowsAsync<ConnectionException>(() => Connection(handler, new Store { Value = JsonSerializer.Serialize(Credentials()) }).LoadAsync(default));
        Assert.DoesNotContain("secret", exception.Message);
    }

    [Fact]
    public async Task DisconnectDeletesWithoutNetworkAndFailureIsVisible()
    {
        var handler = new Handler(); var store = new Store { Value = "existing", FailDelete = true };
        using var vm = new GitHubConnectionViewModel(Connection(handler, store));
        await vm.DisconnectAsync();
        Assert.Equal("existing", store.Value);
        Assert.Contains("unavailable", vm.Notice); Assert.DoesNotContain("secret-error", vm.Notice);
        store.FailDelete = false;
        await vm.DisconnectAsync();
        Assert.Null(store.Value); Assert.Contains("Disconnected", vm.Notice);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingConfigurationAndCorruptCredentialsDoNotStartAuthorization()
    {
        var handler = new Handler(); var store = new Store { Value = "{" };
        var api = new GitHubApi(new HttpClient(handler), "");
        using var vm = new GitHubConnectionViewModel(new(api, store));
        Assert.False(vm.CanConnect); Assert.False(vm.CanReload); await vm.InitializeAsync(); await vm.ConnectAsync(); await vm.ReloadAsync(); Assert.Contains("client ID", vm.Notice);
        await Assert.ThrowsAsync<ConnectionException>(() => Connection(handler, store).LoadAsync(default));
        Assert.Equal("{", store.Value); Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExpiredRefreshRequiresReconnectionWithoutSendingOldToken()
    {
        var store = new Store
        {
            Value = JsonSerializer.Serialize(new ConnectionCredentials
            {
                ClientId = "client",
                AccessToken = "old",
                RefreshToken = "expired",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
                RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
            })
        };
        var handler = new Handler();
        await Assert.ThrowsAsync<ConnectionException>(() => Connection(handler, store).LoadAsync(default));
        Assert.Empty(handler.Requests); Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("{bad-json")]
    [InlineData("{\"token_type\":\"bearer\"}")]
    [InlineData("{\"access_token\":\"synthetic\",\"token_type\":\"unknown\"}")]
    public async Task MalformedTokenResponsesCannotCreateStoredConnection(string response)
    {
        var handler = new Handler(Device, response); var store = new Store();
        await Assert.ThrowsAnyAsync<Exception>(() => Connection(handler, store, new Clock()).ConnectAsync(_ => { }, default));
        Assert.Null(store.Value); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task RepositoryViewModelShowsStoredSelectionAndClearsVerificationOnSaveFailure()
    {
        var handler = new Handler("""{"login":"alice"}""", Installations, Repos(Repo(), Repo(2)), Installations, Repos(Repo(2)), Installations, Repos(Repo(2)));
        var store = new Store { Value = JsonSerializer.Serialize(Credentials()) };
        using var vm = new GitHubConnectionViewModel(Connection(handler, store));
        await vm.InitializeAsync();
        Assert.Contains("alice", vm.Account); Assert.Contains("owner/repo1", vm.Selection);
        Assert.True(vm.CanSelect); Assert.False(vm.IsBusy);
        vm.Candidate = vm.Repositories.Single(r => r.Id == 2);
        store.FailWrite = true;
        await vm.SelectAsync();
        Assert.Equal("No verified repository selected.", vm.Selection); Assert.Contains("unavailable", vm.Notice);
        Assert.Contains("repo1", store.Value);
        store.FailWrite = false;
        await vm.SelectAsync();
        Assert.Contains("owner/repo2", vm.Selection); Assert.Contains("saved securely", vm.Notice);
        await vm.DisconnectAsync();
        Assert.Equal("", vm.Account); Assert.Empty(vm.Repositories); Assert.False(vm.CanSelect);
    }

    private sealed class Picker : IFolderPicker { public Task<string?> PickAsync() => Task.FromResult<string?>("/synthetic"); }
    private sealed class Scanner : ICollectionScanner
    {
        public int Count;
        public Task<CollectionManifest> ScanAsync(string root, CancellationToken ct)
        {
            Count++;
            return Task.FromResult(new CollectionManifest(1, root, [], CollectionInventory.Fingerprint([]), DateTimeOffset.UtcNow, true, []));
        }
    }
    [Fact]
    public async Task SlowGitHubAndDisconnectNeverBlockOrTriggerLocalRefresh()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler("""{"login":"alice"}""") { Pause = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var store = new Store { Value = JsonSerializer.Serialize(Credentials()) };
        var github = new GitHubConnectionViewModel(Connection(handler, store));
        var scanner = new Scanner();
        using var local = new MainWindowViewModel(scanner, new Picker(), github: github);
        var load = github.InitializeAsync(); await started.Task;
        await local.SelectFolderAsync(); await local.RefreshAsync();
        Assert.Equal(2, scanner.Count); Assert.True(local.Manifest!.IsValid);
        github.Cancel(); await load;
        await github.DisconnectAsync();
        Assert.Equal(2, scanner.Count); Assert.Single(handler.Requests);
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task DisposeCancelsGithubAndRejectsLateOverview()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler("""{"login":"alice"}""", Installations, Repos(Repo())) { Pause = _ => release.Task };
        using var vm = new GitHubConnectionViewModel(Connection(handler, new Store { Value = JsonSerializer.Serialize(Credentials()) }));
        var load = vm.InitializeAsync(); vm.Dispose(); release.SetResult(); await load;
        Assert.Equal("", vm.Account); Assert.False(vm.CanManage);
    }
}
