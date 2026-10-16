using System.ComponentModel;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;

namespace PD2Cloud.Desktop.ViewModels;

public enum InspectionState { Initial, Scanning, ScannedSuccessfully, NeedsAttention, Failed }

public sealed class MainWindowViewModel(ICollectionScanner scanner, IFolderPicker picker, IFolderSettingsStore? settings = null, GitHubConnectionViewModel? github = null) : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? scan;
    private long generation;
    private bool disposed;
    private bool picking;
    private bool initializing;
    private bool initialized;
    private string settingsNotice = "";
    private string? selectedPath;
    private string pickerNotice = "";
    private string status = "Select your save folder to get started.";
    private InspectionState state;
    private CollectionManifest? manifest;

    public GitHubConnectionViewModel? GitHub { get; } = github;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string? SelectedPath => selectedPath;
    public string SettingsNotice => settingsNotice;
    public bool HasSettingsNotice => settingsNotice.Length > 0;
    public string PickerNotice => pickerNotice;
    public string Status => status;
    public InspectionState State => state;
    public CollectionManifest? Manifest => manifest;
    public IReadOnlyList<FileEntry> Files => manifest?.Files ?? [];
    public IReadOnlyList<ValidationIssue> Issues => manifest?.Issues ?? [];
    public string? CollectionFingerprint => manifest?.CollectionFingerprint;
    public bool IsInitial => selectedPath is null;
    public bool HasFolder => !IsInitial;
    public bool IsBusy => state == InspectionState.Scanning;
    public bool HasPickerNotice => pickerNotice.Length > 0;
    public bool CanRefresh => HasFolder && !disposed;
    public bool CanSelect => !picking && !initializing && !disposed;
    public bool HasInspection => manifest is not null;
    public bool ShowEmpty => manifest?.IsValid == true && Files.Count == 0;
    public string CountSummary => manifest?.IsValid == true ? $"{Files.Count} {(Files.Count == 1 ? "file" : "files")}" : "File inventory incomplete";
    public string LastInspection => manifest is null ? "" : $"Last inspected {manifest.InspectedAt.ToLocalTime():g}";
    public string StateLabel => state switch
    {
        InspectionState.Scanning => "Scanning",
        InspectionState.ScannedSuccessfully => "Scanned successfully",
        InspectionState.NeedsAttention => "Needs attention",
        InspectionState.Failed => "Failed",
        _ => ""
    };

    public async Task InitializeAsync()
    {
        if (initialized || disposed) return;
        initialized = true;
        if (settings is null) return;
        initializing = true;
        Notify();
        try
        {
            var restored = await settings.LoadAsync();
            if (disposed) return;
            settingsNotice = restored.Notice;
            selectedPath = restored.SelectedFolder;
            initializing = false;
            if (selectedPath is not null) await RefreshAsync();
        }
        catch (Exception)
        {
            if (!disposed) settingsNotice = "Could not restore folder settings. Choose a folder to continue.";
        }
        finally
        {
            initializing = false;
            if (!disposed) Notify();
        }
    }

    public async Task SelectFolderAsync()
    {
        if (!CanSelect) return;
        picking = true;
        pickerNotice = "";
        Notify();
        string? path;
        try
        {
            path = await picker.PickAsync();
            if (disposed) return;
            if (path is null)
            {
                pickerNotice = "Folder selection cancelled. Current selection kept.";
                return;
            }
            if (settings is not null)
            {
                if (!settings.IsSourceAllowed(path))
                {
                    pickerNotice = "Choose a folder outside application-owned state and staging, and not a parent of that location.";
                    return;
                }
                try
                {
                    await settings.SaveFolderAsync(path);
                    if (disposed) return;
                    settingsNotice = "";
                }
                catch (Exception)
                {
                    if (disposed) return;
                    settingsNotice = "Folder could not be saved for restart. You can still inspect it; existing settings have been kept. Choose the folder again to retry.";
                }
            }
        }
        catch (Exception)
        {
            if (!disposed) pickerNotice = "Could not open the folder chooser. Please try again.";
            return;
        }
        finally
        {
            picking = false;
            Notify();
        }
        selectedPath = path;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        scan?.Cancel();
        var current = new CancellationTokenSource();
        scan = current;
        var request = ++generation;
        manifest = null;
        state = InspectionState.Scanning;
        status = "Inspecting your files…";
        Notify();
        try
        {
            var result = await scanner.ScanAsync(selectedPath!, current.Token);
            if (disposed || request != generation) return;
            manifest = result;
            state = result.IsValid ? InspectionState.ScannedSuccessfully :
                result.Issues.Any(i => i.Code is ValidationCode.FolderError or ValidationCode.AccessError or ValidationCode.ReadError)
                    ? InspectionState.Failed : InspectionState.NeedsAttention;
            status = state switch
            {
                InspectionState.ScannedSuccessfully => result.Files.Count == 0 ? "Folder is empty." : "Inspection complete.",
                InspectionState.Failed => "Could not finish inspection. Check access and Refresh.",
                _ => "Inspection needs attention. Review the details and Refresh."
            };
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!disposed && request == generation)
            {
                state = InspectionState.Failed;
                status = "Could not inspect this folder. Check access and Refresh.";
            }
        }
        finally
        {
            if (ReferenceEquals(scan, current)) scan = null;
            current.Dispose();
            if (!disposed && request == generation) Notify();
        }
    }

    public void Dispose()
    {
        disposed = true;
        GitHub?.Dispose();
        ++generation;
        scan?.Cancel();
        Notify();
    }

    // All derived screen properties change together at inspection/picker boundaries.
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
