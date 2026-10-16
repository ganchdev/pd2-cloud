using System.ComponentModel;
using PD2Cloud.GitHub;

namespace PD2Cloud.Desktop.ViewModels;

public sealed class GitHubConnectionViewModel(GitHubConnection connection) : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? operation;
    private bool disposed;
    private bool initialized;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsBusy => operation is not null;
    public bool CanConnect => !disposed && !IsBusy && connection.IsConfigured;
    public bool CanManage => !disposed && !IsBusy;
    public bool CanReload => CanManage && connection.IsConfigured;
    public bool CanSelect => CanManage && Candidate is not null;
    public string Notice { get; private set; } = connection.IsConfigured ? "Connect to choose a private GitHub repository." : "GitHub App client ID is not configured. Set PD2CLOUD_GITHUB_CLIENT_ID and restart. Local inspection is available.";
    public string Account { get; private set; } = "";
    public string Selection { get; private set; } = "No repository selected.";
    public string UserCode { get; private set; } = "";
    public string VerificationAddress { get; private set; } = "";
    public bool HasCode => UserCode.Length > 0;
    public IReadOnlyList<RepositoryChoice> Repositories { get; private set; } = [];
    private RepositoryChoice? candidate;
    public RepositoryChoice? Candidate
    {
        get => candidate;
        set { candidate = value; Notify(); }
    }

    public Task InitializeAsync()
    {
        if (initialized || !connection.IsConfigured) return Task.CompletedTask;
        initialized = true;
        return ReloadAsync();
    }

    public Task ConnectAsync() => !CanConnect ? Task.CompletedTask : RunAsync(async ct =>
    {
        Account = ""; Selection = "No repository selected."; Repositories = []; Candidate = null;
        var progress = new Progress<DeviceAuthorization>(device =>
        {
            if (disposed || ct.IsCancellationRequested || operation?.Token != ct) return;
            UserCode = device.UserCode;
            VerificationAddress = device.VerificationUri.AbsoluteUri;
            Notice = $"Open the GitHub address below and enter this code. Expires {device.ExpiresAt.ToLocalTime():t}.";
            Notify();
        });
        await connection.ConnectAsync(device => ((IProgress<DeviceAuthorization>)progress).Report(device), ct);
        await LoadAsync(ct);
    });

    public Task ReloadAsync() => !CanReload ? Task.CompletedTask : RunAsync(LoadAsync);
    private async Task LoadAsync(CancellationToken ct)
    {
        Account = ""; Repositories = []; Candidate = null; Selection = "No verified repository selected.";
        var overview = await connection.LoadAsync(ct);
        if (disposed || ct.IsCancellationRequested) return;
        Account = overview is null ? "" : $"Connected as {overview.Account}";
        Repositories = overview?.Repositories ?? [];
        Selection = overview?.Selected is { } selected ? $"Selected: {selected.FullName} • {selected.DefaultBranch}" : "No repository selected.";
        Candidate = overview?.Selected;
        Notice = overview is null ? "Connect to GitHub to continue." : Repositories.Count == 0
            ? "No eligible private repositories. Install the GitHub App on a private repository with Contents read/write access, then Reload Repositories."
            : "Choose a private repository. Connection does not synchronize files.";
    }

    public Task SelectAsync()
    {
        var selected = Candidate;
        if (selected is null) return Task.CompletedTask;
        return RunAsync(async ct =>
        {
            Selection = "No verified repository selected.";
            var result = await connection.SelectAsync(selected.Id, ct);
            if (disposed || ct.IsCancellationRequested) return;
            Selection = $"Selected: {result.FullName} • {result.DefaultBranch}";
            Notice = "Repository selection saved securely. No repository contents were written.";
        });
    }

    public Task DisconnectAsync() => RunAsync(async ct =>
    {
        await connection.DisconnectAsync(ct);
        if (disposed) return;
        Account = ""; Selection = "No repository selected."; Repositories = []; Candidate = null;
        Notice = "Disconnected. Local credentials removed. Revoke app authorization separately in GitHub settings if desired.";
    });

    public void Cancel() => operation?.Cancel();
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (disposed || IsBusy) return;
        using var current = new CancellationTokenSource();
        operation = current; UserCode = ""; VerificationAddress = ""; Notice = "Contacting GitHub…"; Notify();
        try { await action(current.Token); }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            if (!disposed) Notice = "GitHub operation cancelled. Reload to verify saved connection state.";
        }
        catch (ConnectionException ex) { if (!disposed) Notice = ex.Message; }
        catch (Exception)
        {
            if (!disposed)
            {
                Notice = "GitHub connection or native secure storage is unavailable. Check connectivity and the native credential facility, then retry. No plaintext fallback is used.";
            }
        }
        finally
        {
            operation = null; UserCode = ""; VerificationAddress = "";
            if (!disposed) Notify();
        }
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() { disposed = true; operation?.Cancel(); }
}
