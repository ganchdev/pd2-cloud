using System.ComponentModel;
using System.Runtime.CompilerServices;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;

namespace PD2Cloud.Desktop.ViewModels;

public sealed class MainWindowViewModel(IFolderInspector inspector, IFolderPicker picker) : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? scan;
    private long generation;
    private bool disposed;
    private bool picking;
    private string? selectedPath;
    private string status = "Select a folder to inspect its filenames and sizes.";
    private string pickerNotice = "";
    private IReadOnlyList<FolderEntry> entries = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public string? SelectedPath { get => selectedPath; private set => Set(ref selectedPath, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string PickerNotice { get => pickerNotice; private set => Set(ref pickerNotice, value); }
    public IReadOnlyList<FolderEntry> Entries { get => entries; private set => Set(ref entries, value); }
    public bool CanRefresh => SelectedPath is not null;
    public bool CanSelect => !picking;

    public async Task SelectFolderAsync()
    {
        if (picking || disposed) return;
        picking = true;
        OnPropertyChanged(nameof(CanSelect));
        PickerNotice = "";
        string? path;
        try
        {
            path = await picker.PickAsync();
            if (disposed) return;
            if (path is null)
            {
                PickerNotice = SelectedPath is null ? "Folder selection cancelled." :
                    "Folder selection cancelled. Current selection kept.";
                return;
            }
        }
        catch (Exception)
        {
            if (!disposed)
                PickerNotice = "Could not select a local folder. Check desktop picker availability and try again.";
            return;
        }
        finally
        {
            picking = false;
            OnPropertyChanged(nameof(CanSelect));
        }
        SelectedPath = path;
        OnPropertyChanged(nameof(CanRefresh));
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (SelectedPath is null || disposed) return;
        scan?.Cancel();
        var current = new CancellationTokenSource();
        scan = current;
        var request = ++generation;
        Entries = [];
        Status = "Inspecting folder…";
        try
        {
            var result = await inspector.InspectAsync(SelectedPath, current.Token);
            if (disposed || request != generation) return;
            Entries = result.Entries;
            Status = result.Error ?? (Entries.Count == 0 ? "Folder is empty." :
                $"{Entries.Count} entries. Links are skipped; subfolders are not inspected.");
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!disposed && request == generation)
                Status = "Could not inspect this folder. Check access and Refresh to retry.";
        }
        finally
        {
            if (ReferenceEquals(scan, current)) scan = null;
            current.Dispose();
        }
    }

    public void Dispose()
    {
        disposed = true;
        ++generation;
        scan?.Cancel();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
