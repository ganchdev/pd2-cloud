using System.ComponentModel;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Logging;
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

    public bool SetupStarted { get; private set; }
    public async Task StartSetupAsync()
    {
        SetupStarted = true;
        try { if (settings is not null) await settings.SaveSetupStartedAsync(); }
        catch (Exception error)
        {
            AppLog.Warn("Settings", "Could not save setup-started progress", error);
            settingsNotice = "Could not save setup progress. You can continue for this session.";
        }
        Notify();
    }
    public bool SetupCompleted { get; private set; }
    public async Task CompleteSetupAsync()
    {
        if (manifest?.IsValid != true) return;
        try
        {
            if (settings is not null)
            {
                await settings.SaveFolderAsync(selectedPath!);
                await settings.SaveSetupCompletedAsync();
            }
            settingsNotice = "";
            SetupCompleted = true;
            AppLog.Info("Settings", $"Setup completed; folder={selectedPath}");
        }
        catch (Exception error)
        {
            AppLog.Warn("Settings", "Could not save completed setup", error);
            settingsNotice = "Could not save setup progress. Please retry Continue.";
        }
        Notify();
    }

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
            SetupStarted = restored.SetupStarted;
            SetupCompleted = restored.SetupCompleted;
            settingsNotice = restored.Notice;
            selectedPath = restored.SelectedFolder;
            initializing = false;
            AppLog.Info("Settings", $"Restored setup; started={SetupStarted}; completed={SetupCompleted}; folder={selectedPath ?? "<none>"}; notice={restored.Notice}");
            if (selectedPath is not null) await RefreshAsync();
        }
        catch (Exception error)
        {
            AppLog.Warn("Settings", "Could not restore folder settings", error);
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
                AppLog.Info("Folder", "Folder selection cancelled.");
                pickerNotice = "Folder selection cancelled. Current selection kept.";
                return;
            }
            AppLog.Info("Folder", $"Folder selected: {path}");
            if (settings is not null)
            {
                if (!settings.IsSourceAllowed(path))
                {
                    AppLog.Warn("Folder", $"Rejected source folder (overlaps application data): {path}");
                    pickerNotice = "Choose a folder outside application-owned state and staging, and not a parent of that location.";
                    return;
                }
                try
                {
                    await settings.SaveFolderAsync(path);
                    if (disposed) return;
                    settingsNotice = "";
                }
                catch (Exception error)
                {
                    AppLog.Warn("Settings", "Could not save selected folder", error);
                    if (disposed) return;
                    settingsNotice = "Folder could not be saved for restart. You can still inspect it; existing settings have been kept. Choose the folder again to retry.";
                }
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Folder", "Could not open the folder chooser", error);
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
            AppLog.Info("Inspection", $"Started; folder={selectedPath}");
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
            AppLog.Info("Inspection", $"Finished; state={state}; valid={result.IsValid}; files={result.Files.Count}; fingerprint={result.CollectionFingerprint ?? "<null>"}");
            foreach (var issue in result.Issues)
                AppLog.Warn("Inspection", $"Issue; code={issue.Code}; path={issue.RelativePath ?? "<root>"}; message={issue.Message}");
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            AppLog.Info("Inspection", "Cancelled.");
        }
        catch (Exception error)
        {
            AppLog.Error("Inspection", $"Unhandled failure; folder={selectedPath}", error);
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
