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

    private sealed class Inspector : IFolderInspector
    {
        public List<(string Path, CancellationToken Token, TaskCompletionSource<FolderInspection> Result)> Requests { get; } = [];
        public Task<FolderInspection> InspectAsync(string path, CancellationToken cancellationToken)
        {
            var result = new TaskCompletionSource<FolderInspection>();
            Requests.Add((path, cancellationToken, result));
            return result.Task; // Deliberately ignores cancellation to exercise stale results.
        }
    }

    private static FolderInspection Listing(string name) => new([new(name, 42, FolderEntryKind.File)]);

    [Fact]
    public async Task ChangingFolderRejectsLateSuccessAndRefreshRejectsLateFailure()
    {
        var picker = new Picker { Path = "/first" };
        var inspector = new Inspector();
        using var model = new MainWindowViewModel(inspector, picker);
        var first = model.SelectFolderAsync();
        Assert.True(model.CanSelect);
        picker.Path = "/second";
        var second = model.SelectFolderAsync();
        Assert.True(inspector.Requests[0].Token.IsCancellationRequested);
        inspector.Requests[1].Result.SetResult(Listing("second"));
        await second;
        inspector.Requests[0].Result.SetResult(Listing("first"));
        await first;
        Assert.Equal("/second", model.SelectedPath);
        Assert.Equal("second", Assert.Single(model.Entries).Name);
        var refresh1 = model.RefreshAsync();
        var refresh2 = model.RefreshAsync();
        inspector.Requests[3].Result.SetResult(Listing("new"));
        await refresh2;
        inspector.Requests[2].Result.SetException(new UnauthorizedAccessException());
        await refresh1;
        Assert.Equal("new", Assert.Single(model.Entries).Name);
        Assert.DoesNotContain("Could not", model.Status);
    }

    [Fact]
    public async Task PickerCancellationAndFailureKeepCurrentSelectionAndListing()
    {
        var picker = new Picker();
        var inspector = new Inspector();
        using var model = new MainWindowViewModel(inspector, picker);
        await model.SelectFolderAsync();
        Assert.False(model.CanRefresh);
        Assert.Contains("cancelled", model.PickerNotice);
        picker.Path = "/folder";
        var select = model.SelectFolderAsync();
        inspector.Requests[0].Result.SetResult(Listing("kept"));
        await select;
        picker.Path = null;
        await model.SelectFolderAsync();
        picker.Fail = true;
        await model.SelectFolderAsync();
        Assert.Equal("/folder", model.SelectedPath);
        Assert.Equal("kept", Assert.Single(model.Entries).Name);
        Assert.Single(inspector.Requests);
        Assert.Contains("Could not select", model.PickerNotice);
    }

    [Fact]
    public async Task EmptyErrorAndRecoveryClearOldListing()
    {
        var inspector = new Inspector();
        using var model = new MainWindowViewModel(inspector, new Picker { Path = "/folder" });
        var select = model.SelectFolderAsync();
        inspector.Requests[0].Result.SetResult(Listing("old"));
        await select;
        var refresh = model.RefreshAsync();
        Assert.Empty(model.Entries);
        inspector.Requests[1].Result.SetResult(new([], "Cannot access this folder."));
        await refresh;
        Assert.Contains("Cannot access", model.Status);
        refresh = model.RefreshAsync();
        inspector.Requests[2].Result.SetResult(new([]));
        await refresh;
        Assert.Equal("Folder is empty.", model.Status);
    }

    [Fact]
    public async Task ClosingCancelsScanAndRejectsItsResult()
    {
        var inspector = new Inspector();
        var model = new MainWindowViewModel(inspector, new Picker { Path = "/folder" });
        var select = model.SelectFolderAsync();
        model.Dispose();
        Assert.True(inspector.Requests[0].Token.IsCancellationRequested);
        inspector.Requests[0].Result.SetResult(Listing("late"));
        await select;
        Assert.Empty(model.Entries);
    }
}
