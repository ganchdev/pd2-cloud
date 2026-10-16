using System.Net.Http.Headers;
using System.Text.Json;

namespace PD2Cloud.GitHub;

public sealed partial class GitHubApi(HttpClient http, string clientId, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public string ClientId { get; } = clientId;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 4 * 1024 * 1024
    };

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, string? token, Dictionary<string, string>? form, CancellationToken ct, object? json = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("PD2Cloud/0.1");
        request.Headers.Accept.ParseAdd("application/json");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        if (form is not null) request.Content = new FormUrlEncodedContent(form);
        if (json is not null) request.Content = System.Net.Http.Json.JsonContent.Create(json);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // Enforce the limit while streaming, including with injected HttpClient instances.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict && url.EndsWith("/commits?per_page=1", StringComparison.Ordinal))
            {
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                if (error.RootElement.TryGetProperty("message", out var message) && message.GetString() == "Git Repository is empty.")
                    return JsonSerializer.SerializeToElement(Array.Empty<object>());
            }
            throw new ConnectionException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "GitHub authorization is no longer valid. Disconnect and connect again.",
                System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests => "GitHub denied access or rate limited the request. Check app access and retry later.",
                _ => "GitHub could not complete the request. Check connectivity and retry."
            });
        }
        // Do not include response bodies, request URIs containing secrets, or underlying exceptions in UI/logs.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        return document.RootElement.Clone();
    }

    public async Task<ConnectionCredentials> AuthorizeAsync(Action<DeviceAuthorization> showCode, CancellationToken ct)
    {
        if (!IsConfigured) throw new ConnectionException("GitHub App client ID is not configured. Set PD2CLOUD_GITHUB_CLIENT_ID and restart.");
        var device = await SendAsync(HttpMethod.Post, "https://github.com/login/device/code", null,
            new() { ["client_id"] = ClientId }, ct).ConfigureAwait(false);
        CheckError(device);
        var code = Required(device, "device_code");
        var uri = new Uri(Required(device, "verification_uri"));
        if (uri.AbsoluteUri != "https://github.com/login/device") throw new ConnectionException("GitHub returned an unexpected verification address.");
        var expires = clock.GetUtcNow().AddSeconds(Positive(device, "expires_in"));
        var interval = Positive(device, "interval");
        showCode(new(Required(device, "user_code"), uri, expires));
        while (clock.GetUtcNow() < expires)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), clock, ct).ConfigureAwait(false);
            if (clock.GetUtcNow() >= expires) break;
            var result = await SendAsync(HttpMethod.Post, "https://github.com/login/oauth/access_token", null,
                new() { ["client_id"] = ClientId, ["device_code"] = code, ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code" }, ct).ConfigureAwait(false);
            if (result.TryGetProperty("error", out var error))
            {
                if (error.GetString() == "authorization_pending") continue;
                if (error.GetString() == "slow_down") { interval = checked(interval + 5); continue; }
                CheckError(result);
            }
            ct.ThrowIfCancellationRequested();
            if (clock.GetUtcNow() >= expires) break;
            return DecodeToken(result);
        }
        throw new ConnectionException("The GitHub code expired. Connect again for a new code.");
    }

    public async Task<ConnectionCredentials> RefreshAsync(ConnectionCredentials old, CancellationToken ct)
    {
        if (old.RefreshToken is null || old.RefreshExpiresAt <= clock.GetUtcNow())
            throw new ConnectionException("GitHub authorization expired. Disconnect and connect again.");
        var result = await SendAsync(HttpMethod.Post, "https://github.com/login/oauth/access_token", null,
            new() { ["client_id"] = ClientId, ["refresh_token"] = old.RefreshToken, ["grant_type"] = "refresh_token" }, ct).ConfigureAwait(false);
        CheckError(result);
        var fresh = DecodeToken(result);
        fresh.Repository = old.Repository;
        return fresh;
    }

    private ConnectionCredentials DecodeToken(JsonElement result)
    {
        if (!string.Equals(Required(result, "token_type"), "bearer", StringComparison.OrdinalIgnoreCase))
            throw new ConnectionException("GitHub returned an unsupported authorization type.");
        var now = clock.GetUtcNow();
        return new()
        {
            ClientId = ClientId,
            AccessToken = Required(result, "access_token"),
            RefreshToken = result.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            ExpiresAt = result.TryGetProperty("expires_in", out _) ? now.AddSeconds(Positive(result, "expires_in")) : null,
            RefreshExpiresAt = result.TryGetProperty("refresh_token_expires_in", out _) ? now.AddSeconds(Positive(result, "refresh_token_expires_in")) : null
        };
    }

    public async Task<string> AccountAsync(string token, CancellationToken ct) =>
        Required(await SendAsync(HttpMethod.Get, "https://api.github.com/user", token, null, ct).ConfigureAwait(false), "login");

    public async Task<IReadOnlyList<RepositoryChoice>> RepositoriesAsync(string token, CancellationToken ct)
    {
        var choices = new Dictionary<long, RepositoryChoice>();
        var examined = 0;
        foreach (var installation in await PagesAsync("https://api.github.com/user/installations", "installations", token, ct).ConfigureAwait(false))
        {
            if (!installation.TryGetProperty("suspended_at", out var suspended) || suspended.ValueKind != JsonValueKind.Null) continue;
            if (!installation.GetProperty("permissions").TryGetProperty("contents", out var permission) || permission.GetString() != "write") continue;
            var id = installation.GetProperty("id").GetInt64();
            foreach (var repo in await PagesAsync($"https://api.github.com/user/installations/{id}/repositories", "repositories", token, ct).ConfigureAwait(false))
            {
                if (++examined > 10000) throw new ConnectionException("GitHub repository listing exceeded the supported limit. Narrow the app installation access.");
                if (!repo.GetProperty("private").GetBoolean() || repo.GetProperty("archived").GetBoolean() || repo.GetProperty("disabled").GetBoolean()) continue;
                if (!repo.TryGetProperty("permissions", out var permissions) || !permissions.TryGetProperty("push", out var push) || !push.GetBoolean()) continue;
                var choice = new RepositoryChoice(repo.GetProperty("id").GetInt64(), Required(repo, "full_name"), Required(repo, "default_branch"));
                choices[choice.Id] = choice;
            }
        }
        return choices.Values.OrderBy(r => r.FullName, StringComparer.Ordinal).ToArray();
    }

    private async Task<List<JsonElement>> PagesAsync(string url, string field, string token, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        for (var page = 1; page <= 100; page++)
        {
            var result = await SendAsync(HttpMethod.Get, $"{url}?per_page=100&page={page}", token, null, ct).ConfigureAwait(false);
            var entries = result.GetProperty(field).EnumerateArray().ToArray();
            items.AddRange(entries);
            if (entries.Length < 100) return items;
        }
        throw new ConnectionException("GitHub repository listing exceeded the supported limit. Narrow the app installation access.");
    }

    private static string Required(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
            ? field.GetString()! : throw new ConnectionException("GitHub returned an incomplete response.");
    private static int Positive(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.TryGetInt32(out var seconds) && seconds is > 0 and <= 31536000
        ? seconds : throw new ConnectionException("GitHub returned invalid authorization timing.");
    private static void CheckError(JsonElement value)
    {
        if (!value.TryGetProperty("error", out var field)) return;
        throw new ConnectionException(field.GetString() switch
        {
            "access_denied" => "GitHub authorization was declined. Connect again when ready.",
            "expired_token" => "The GitHub code expired. Connect again for a new code.",
            "device_flow_disabled" => "Device flow must be enabled on the registered GitHub App.",
            "incorrect_client_credentials" => "The configured GitHub App client ID was rejected.",
            _ => "GitHub authorization failed. Disconnect and connect again."
        });
    }
}
