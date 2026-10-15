using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;
using PD2Cloud.Desktop.ViewModels;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class MainWindowViewModelTests
{
    private sealed class Picker : IFolderPicker
    {
        public string? Path { get; set; }
        public bool Fail { get; set; }
        public Task<string?> PickAsync() => Fail ? Task.FromException<string?>(new IOException()) : Task.FromResult(Path);
    }
    private sealed class Scanner : ICollectionScanner
    {
        public List<(string Path, CancellationToken Token, TaskCompletionSource<CollectionManifest> Result)> Requests { get; } = [];
        public Task<CollectionManifest> ScanAsync(string path, CancellationToken token)
        {
            var result = new TaskCompletionSource<CollectionManifest>();
            Requests.Add((path, token, result));
            return result.Task; // Deliberately ignores cancellation to exercise stale results.
        }
    }
    private static CollectionManifest Manifest(string? name = null, ValidationIssue? issue = null) =>
        new(1, "/folder", name is null ? [] : [new(name, 42, new string('a', 64))], issue is null ? "hash" : null,
            DateTimeOffset.UtcNow, issue is null, issue is null ? [] : [issue]);

    [Fact]
    public async Task InitialLoadingSuccessEmptyWarningErrorAndRecovery()
    {
        var scanner = new Scanner();
        using var model = new MainWindowViewModel(scanner, new Picker { Path = "/folder" });
        Assert.True(model.IsInitial);
        Assert.False(model.CanRefresh);
        Assert.Null(model.Manifest);
        var select = model.SelectFolderAsync();
        Assert.True(model.IsBusy);
        Assert.Empty(model.Files);
        scanner.Requests[0].Result.SetResult(Manifest("nested/unknown"));
        await select;
        Assert.Equal(InspectionState.ScannedSuccessfully, model.State);
        Assert.Equal("Scanned successfully", model.StateLabel);
        Assert.Equal("1 file", model.CountSummary);
        Assert.Contains("Last inspected", model.LastInspection);
        var refresh = model.RefreshAsync();
        Assert.Null(model.Manifest);
        scanner.Requests[1].Result.SetResult(Manifest());
        await refresh;
        Assert.True(model.ShowEmpty);
        Assert.Equal("Folder is empty.", model.Status);
        refresh = model.RefreshAsync();
        scanner.Requests[2].Result.SetResult(Manifest(issue: new(ValidationCode.UnsupportedEntry, "Link skipped", "link")));
        await refresh;
        Assert.Equal(InspectionState.NeedsAttention, model.State);
        Assert.Single(model.Issues);
        Assert.False(model.ShowEmpty);
        Assert.Equal("File inventory incomplete", model.CountSummary);
        refresh = model.RefreshAsync();
        scanner.Requests[3].Result.SetResult(Manifest(issue: new(ValidationCode.AccessError, "Cannot access")));
        await refresh;
        Assert.Equal(InspectionState.Failed, model.State);
        refresh = model.RefreshAsync();
        scanner.Requests[4].Result.SetResult(Manifest("recovered"));
        await refresh;
        Assert.Equal(InspectionState.ScannedSuccessfully, model.State);
        Assert.Equal("recovered", Assert.Single(model.Files).RelativePath);
    }

    [Fact]
    public async Task ChangingFolderAndOverlappingRefreshRejectLateSuccessAndFailure()
    {
        var picker = new Picker { Path = "/first" };
        var scanner = new Scanner();
        using var model = new MainWindowViewModel(scanner, picker);
        var first = model.SelectFolderAsync();
        picker.Path = "/second";
        var second = model.SelectFolderAsync();
        Assert.True(scanner.Requests[0].Token.IsCancellationRequested);
        scanner.Requests[1].Result.SetResult(Manifest("second"));
        await second;
        scanner.Requests[0].Result.SetResult(Manifest("first"));
        await first;
        Assert.Equal("/second", model.SelectedPath);
        Assert.Equal("second", Assert.Single(model.Files).RelativePath);
        var refresh1 = model.RefreshAsync();
        var refresh2 = model.RefreshAsync();
        scanner.Requests[3].Result.SetResult(Manifest("new"));
        await refresh2;
        scanner.Requests[2].Result.SetException(new UnauthorizedAccessException());
        await refresh1;
        Assert.Equal("new", Assert.Single(model.Files).RelativePath);
        Assert.Equal(InspectionState.ScannedSuccessfully, model.State);
    }

    [Fact]
    public async Task PickerCancellationAndFailureKeepSelectionAndResults()
    {
        var picker = new Picker();
        var scanner = new Scanner();
        using var model = new MainWindowViewModel(scanner, picker);
        await model.SelectFolderAsync();
        Assert.True(model.IsInitial);
        Assert.Contains("cancelled", model.PickerNotice);
        picker.Path = "/folder";
        var select = model.SelectFolderAsync();
        scanner.Requests[0].Result.SetResult(Manifest("kept"));
        await select;
        picker.Path = null;
        await model.SelectFolderAsync();
        picker.Fail = true;
        await model.SelectFolderAsync();
        Assert.Equal("/folder", model.SelectedPath);
        Assert.Equal("kept", Assert.Single(model.Files).RelativePath);
        Assert.Single(scanner.Requests);
        Assert.Contains("Could not open", model.PickerNotice);
    }

    [Fact]
    public async Task ClosingCancelsScanAndRejectsResult()
    {
        var scanner = new Scanner();
        var model = new MainWindowViewModel(scanner, new Picker { Path = "/folder" });
        var select = model.SelectFolderAsync();
        model.Dispose();
        Assert.True(scanner.Requests[0].Token.IsCancellationRequested);
        scanner.Requests[0].Result.SetResult(Manifest("late"));
        await select;
        Assert.Empty(model.Files);
        Assert.False(model.CanSelect);
        Assert.False(model.CanRefresh);
    }
}
