using System.ComponentModel;
using PD2Cloud.Desktop.Logging;
using PD2Cloud.GitHub;
using PD2Cloud.Remote;

namespace PD2Cloud.Desktop.ViewModels;

public sealed class CloudInspectionViewModel(Func<RepositoryChoice, CancellationToken, Task<CloudReadResult>> read) : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? operation;
    private long generation;
    private bool disposed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public CloudReadResult? Result { get; private set; }
    public bool IsBusy => operation is not null;
    public string Notice { get; private set; } = "Cloud files have not been checked. Select a repository, then refresh.";
    public string Revision => Result?.Commit is { } commit ? "Checked revision " + commit : "";

    public void Invalidate()
    {
        ++generation;
        operation?.Cancel();
        operation = null;
        Result = null;
        Notice = "Cloud files need a fresh check. Refresh after choosing a repository or changing account settings.";
        AppLog.Debug("Cloud", "Cloud result invalidated.");
        Notify();
    }
    public void Cancel()
    {
        Invalidate();
        Notice = "Cloud check cancelled. Refresh to try again.";
        Notify();
    }
    public async Task RefreshAsync(RepositoryChoice repository)
    {
        if (disposed) return;
        Invalidate();
        var request = generation;
        using var current = new CancellationTokenSource();
        operation = current;
        Notice = "Checking cloud saves…";
        AppLog.Info("Cloud", $"Check started; repository={repository.FullName}; branch={repository.DefaultBranch}");
        Notify();
        try
        {
            var result = await read(repository, current.Token);
            if (disposed || request != generation || current.IsCancellationRequested) return;
            if (result.Repository != repository) throw new InvalidDataException();
            Result = result;
            Notice = result.Message;
            AppLog.Info("Cloud", $"Check finished; content={result.Content}; commit={result.Commit ?? "<none>"}; files={result.Collection?.Manifest?.Files.Count}; message={result.Message}");
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            AppLog.Info("Cloud", "Check cancelled.");
        }
        catch (ConnectionException ex)
        {
            AppLog.Warn("Cloud", "Check failed with a connection error.", ex);
            if (!disposed && request == generation) Notice = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error("Cloud", "Check failed unexpectedly.", ex);
            if (!disposed && request == generation) Notice = "Cloud saves could not be checked. Check your connection and GitHub access, then refresh.";
        }
        finally
        {
            if (ReferenceEquals(operation, current)) operation = null;
            if (!disposed && request == generation) Notify();
        }
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() { disposed = true; ++generation; operation?.Cancel(); operation = null; Result = null; }
}
