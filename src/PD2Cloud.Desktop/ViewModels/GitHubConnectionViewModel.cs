using System.ComponentModel;
using PD2Cloud.GitHub;
using PD2Cloud.Remote;

namespace PD2Cloud.Desktop.ViewModels;

public sealed class GitHubConnectionViewModel(GitHubConnection connection) : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? operation;
    private bool disposed;
    private bool initialized;
    public event PropertyChangedEventHandler? PropertyChanged;
    public Task<CloudReadResult> ReadCloudAsync(RepositoryChoice repository, CancellationToken ct) => new CloudCollectionReader(connection).ReadAsync(repository, ct);
    public bool IsConfigured => connection.IsConfigured;
    public bool IsConnected { get; private set; }
    public bool LastOperationSucceeded { get; private set; }
    public RepositoryChoice? SelectedRepository { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    public string ConnectionStatus => IsConnected ? "Connected" : "GitHub Not Connected";
    public string LastCheck => LastChecked is { } time ? $"Last successful check {time.ToLocalTime():g}" : "No successful connection check yet.";
    public bool HasRepositories => Repositories.Count > 0;
    public bool ShowEmptyRepositories => !IsBusy && IsConnected && !HasRepositories;
    public bool IsBusy => operation is not null;
    public bool CanConnect => !disposed && !IsBusy && connection.IsConfigured;
    public bool CanManage => !disposed && !IsBusy;
    public bool CanReload => CanManage && connection.IsConfigured;
    public bool CanSelect => CanManage && Candidate is not null;
    public string Notice { get; private set; } = connection.IsConfigured ? "Connect to choose a private GitHub repository." : "GitHub connection is not configured in this build. Follow the project setup guide and restart. You can still inspect local saves.";
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
        IsConnected = false; SelectedRepository = null;
        Account = ""; Selection = "No repository selected."; Repositories = []; Candidate = null;
        var progress = new Progress<DeviceAuthorization>(device =>
        {
            if (disposed || ct.IsCancellationRequested || operation?.Token != ct) return;
            UserCode = device.UserCode;
            VerificationAddress = device.VerificationUri.AbsoluteUri;
            Notice = $"Waiting for GitHub authorization… Code expires at {device.ExpiresAt.ToLocalTime():t}.";
            Notify();
        });
        await connection.ConnectAsync(device => ((IProgress<DeviceAuthorization>)progress).Report(device), ct);
        await LoadAsync(ct);
    });

    public Task ReloadAsync() => !CanReload ? Task.CompletedTask : RunAsync(LoadAsync);
    private async Task LoadAsync(CancellationToken ct)
    {
        IsConnected = false; SelectedRepository = null;
        Account = ""; Repositories = []; Candidate = null; Selection = "No verified repository selected.";
        var overview = await connection.LoadAsync(ct);
        if (disposed || ct.IsCancellationRequested) return;
        IsConnected = overview is not null;
        LastChecked = overview is null ? LastChecked : DateTimeOffset.Now;
        Account = overview?.Account ?? "";
        SelectedRepository = overview?.Selected;
        Repositories = overview?.Repositories ?? [];
        Selection = overview?.Selected is { } selected ? $"{selected.FullName}" : "No repository selected.";
        Candidate = overview?.Selected;
        Notice = overview is null ? "Connect to GitHub to continue." : overview.SelectionUnavailable
            ? "Your previous repository is no longer available with its saved settings. Check GitHub access, then choose a repository again." : Repositories.Count == 0
            ? "No available private repositories. Create a private repository or manage GitHub access to allow PD2 Cloud to use an existing one, then refresh."
            : "Choose a private repository. Connection does not synchronize files.";
    }

    public Task SelectAsync()
    {
        var selected = Candidate;
        if (selected is null) return Task.CompletedTask;
        return RunAsync(async ct =>
        {
            SelectedRepository = null;
            Selection = "No verified repository selected.";
            var result = await connection.SelectAsync(selected.Id, ct);
            if (disposed || ct.IsCancellationRequested) return;
            SelectedRepository = result;
            Selection = $"{result.FullName}";
            Notice = "Repository selection saved securely. No repository contents were written.";
        });
    }

    public Task DisconnectAsync() => RunAsync(async ct =>
    {
        await connection.DisconnectAsync(ct);
        if (disposed) return;
        IsConnected = false; SelectedRepository = null;
        Account = ""; Selection = "No repository selected."; Repositories = []; Candidate = null;
        Notice = "Disconnected. Local credentials removed. Revoke app authorization separately in GitHub settings if desired.";
    });

    public void Cancel() => operation?.Cancel();
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (disposed || IsBusy) return;
        using var current = new CancellationTokenSource();
        LastOperationSucceeded = false;
        operation = current; UserCode = ""; VerificationAddress = ""; Notice = "Contacting GitHub…"; Notify();
        try { await action(current.Token); LastOperationSucceeded = !current.IsCancellationRequested; }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            if (!disposed) Notice = "GitHub operation cancelled. Try again when you’re ready.";
        }
        catch (ConnectionException ex) { if (!disposed) Notice = ex.Message; }
        catch (Exception)
        {
            if (!disposed)
            {
                Notice = "GitHub or secure account storage is unavailable. Check your internet connection and unlock your account’s secure storage, then retry.";
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
