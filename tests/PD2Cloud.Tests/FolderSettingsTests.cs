using System.Text.Json.Nodes;
using PD2Cloud.Core;
using PD2Cloud.Desktop.Services;
using PD2Cloud.Desktop.ViewModels;
using PD2Cloud.Local;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class FolderSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pd2-settings-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "app");
    private string Source => Path.Combine(root, "source");
    private string Settings => Path.Combine(Data, "settings.json");
    public FolderSettingsTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Source);
    }
    public void Dispose() => Directory.Delete(root, true);
    private sealed class Picker(string path) : IFolderPicker
    {
        public Task<string?> PickAsync() => Task.FromResult<string?>(path);
    }
    private MainWindowViewModel Model(string? source = null) => new(new CollectionScanner(applicationDirectory: Data),
        new Picker(source ?? Source), new FolderSettingsStore(Data));

    [Fact]
    public async Task RestartReadsFreshBytesWithoutBaselineOrSourceWrites()
    {
        var file = Path.Combine(Source, "opaque.bin");
        await File.WriteAllBytesAsync(file, [0, 1, 255]);
        var originalTime = File.GetLastWriteTimeUtc(file);
        string? fingerprint;
        using (var first = Model())
        {
            await first.InitializeAsync();
            await first.SelectFolderAsync();
            fingerprint = first.CollectionFingerprint;
        }
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(file));
        Assert.Single(Directory.GetFiles(Source));
        await File.WriteAllBytesAsync(file, [2, 3]);
        var changedTime = File.GetLastWriteTimeUtc(file);
        using var restarted = Model();
        await restarted.InitializeAsync();
        Assert.Equal(Source, restarted.SelectedPath);
        Assert.Equal(InspectionState.ScannedSuccessfully, restarted.State);
        Assert.NotEqual(fingerprint, restarted.CollectionFingerprint);
        Assert.Equal(changedTime, File.GetLastWriteTimeUtc(file));
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(Data).Select(Path.GetFileName));
        Assert.Single(Directory.GetFiles(Source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"SchemaVersion\":0,")]
    [InlineData("\"SchemaVersion\":1,")]
    public async Task OlderSettingsAndUnknownFieldsSurviveReplacement(string version)
    {
        await File.WriteAllTextAsync(Settings, "{" + version + "\"SelectedFolder\":" + System.Text.Json.JsonSerializer.Serialize(Source)
            + ",\"ObsoleteProfile\":{\"Ignored\":true},\"Unrelated\":{\"Value\":42}}");
        var store = new FolderSettingsStore(Data);
        Assert.Equal(Source, (await store.LoadAsync()).SelectedFolder);
        await store.SaveFolderAsync(Source);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(Settings))!;
        Assert.Equal(1, document["SchemaVersion"]!.GetValue<int>());
        Assert.Equal(42, document["Unrelated"]!["Value"]!.GetValue<int>());
        Assert.True(document["ObsoleteProfile"]!["Ignored"]!.GetValue<bool>());
        Assert.Single(Directory.GetFiles(Data));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"SchemaVersion\":99}")]
    [InlineData("{\"SchemaVersion\":\"bad\"}")]
    [InlineData("{\"SelectedFolder\":12}")]
    public async Task DamagedOrFutureSettingsRemainUntouchedAndLocalInspectionWorks(string json)
    {
        await File.WriteAllTextAsync(Settings, json);
        using var model = Model();
        await model.InitializeAsync();
        Assert.True(model.IsInitial);
        Assert.True(model.HasSettingsNotice);
        await model.SelectFolderAsync();
        Assert.Equal(InspectionState.ScannedSuccessfully, model.State);
        Assert.Contains("could not be saved", model.SettingsNotice);
        Assert.Equal(json, await File.ReadAllTextAsync(Settings));
        Assert.Single(Directory.GetFiles(Data));
    }

    [Fact]
    public async Task UnavailableRestoredFolderRetainsChoiceAndCanRecover()
    {
        await new FolderSettingsStore(Data).SaveFolderAsync(Source);
        Directory.Delete(Source);
        using var model = Model();
        await model.InitializeAsync();
        Assert.Equal(Source, model.SelectedPath);
        Assert.Equal(InspectionState.Failed, model.State);
        Assert.True(model.CanRefresh);
        Assert.True(model.CanSelect);
        Directory.CreateDirectory(Source);
        await model.RefreshAsync();
        Assert.True(model.ShowEmpty);
        Assert.Equal(Source, (await new FolderSettingsStore(Data).LoadAsync()).SelectedFolder);
    }

    [Fact]
    public async Task StateAndStagingOverlapRejectedBeforeSettingsOrInspection()
    {
        var store = new FolderSettingsStore(Data);
        foreach (var path in new[] { root, Data, Path.Combine(Data, "staging") })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveFolderAsync(path));
            var scan = await new CollectionScanner(applicationDirectory: Data).ScanAsync(path, default);
            Assert.False(scan.IsValid);
            Assert.Contains(scan.Issues, issue => issue.Code == ValidationCode.UnsafePath);
            using var model = Model(path);
            await model.SelectFolderAsync();
            Assert.True(model.IsInitial);
            Assert.Contains("application-owned", model.PickerNotice);
        }
        Assert.False(File.Exists(Settings));
        Assert.True(store.IsSourceAllowed(Data + "-sibling"));
        await File.WriteAllTextAsync(Settings, "{\"SelectedFolder\":" + System.Text.Json.JsonSerializer.Serialize(root) + "}");
        Assert.Null((await store.LoadAsync()).SelectedFolder);
    }

    [Fact]
    public async Task FailedReplacementKeepsExistingDocumentAndCleansTemporaryFile()
    {
        var store = new FolderSettingsStore(Data);
        await store.SaveFolderAsync(Source);
        var before = await File.ReadAllBytesAsync(Settings);
        // A read-only directory reliably blocks creation for normal Unix users.
        if (OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(Data);
        try
        {
            File.SetUnixFileMode(Data, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => store.SaveFolderAsync(Source));
            Assert.Equal(before, await File.ReadAllBytesAsync(Settings));
            Assert.Single(Directory.GetFiles(Data));
        }
        finally { File.SetUnixFileMode(Data, mode); }
    }

    [Fact]
    public async Task InaccessibleRestoredFolderShowsFailureWithoutLosingSettings()
    {
        if (OperatingSystem.IsWindows()) return;
        await new FolderSettingsStore(Data).SaveFolderAsync(Source);
        var mode = File.GetUnixFileMode(Source);
        try
        {
            File.SetUnixFileMode(Source, UnixFileMode.None);
            using var model = Model();
            await model.InitializeAsync();
            Assert.Equal(Source, model.SelectedPath);
            Assert.Equal(InspectionState.Failed, model.State);
            Assert.True(model.HasInspection);
        }
        finally { File.SetUnixFileMode(Source, mode); }
        Assert.Equal(Source, (await new FolderSettingsStore(Data).LoadAsync()).SelectedFolder);
    }

    [Fact]
    public async Task ReplacementPublishesCompleteJsonAndPreservesLatestUnrelatedValues()
    {
        var store = new FolderSettingsStore(Data);
        await store.SaveFolderAsync(Source);
        var latest = JsonNode.Parse(await File.ReadAllTextAsync(Settings))!;
        latest["OtherPreference"] = "changed externally";
        await File.WriteAllTextAsync(Settings, latest.ToJsonString());
        var next = Path.Combine(root, "other-source");
        await store.SaveFolderAsync(next);
        Assert.Equal(next, (await new FolderSettingsStore(Data).LoadAsync()).SelectedFolder);
        Assert.Equal("changed externally", JsonNode.Parse(await File.ReadAllTextAsync(Settings))!["OtherPreference"]!.GetValue<string>());
        Assert.Single(Directory.GetFiles(Data));
    }

    [Fact]
    public async Task LinkedSettingsAreNotFollowedOrReplaced()
    {
        if (OperatingSystem.IsWindows()) return;
        var external = Path.Combine(root, "external.json");
        await File.WriteAllTextAsync(external, "{}");
        File.CreateSymbolicLink(Settings, external);
        var store = new FolderSettingsStore(Data);
        Assert.NotEmpty((await store.LoadAsync()).Notice);
        await Assert.ThrowsAsync<IOException>(() => store.SaveFolderAsync(Source));
        Assert.Equal("{}", await File.ReadAllTextAsync(external));
        Assert.NotNull(new FileInfo(Settings).LinkTarget);
    }
}
