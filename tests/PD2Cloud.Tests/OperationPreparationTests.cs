using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PD2Cloud.Core;
using PD2Cloud.Local;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class OperationPreparationTests : IDisposable
{
    private readonly string root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pd2-recovery-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "app");
    private string Source => Path.Combine(root, "source");
    private string Recovery => Path.Combine(Data, "recovery");

    public OperationPreparationTests() => Directory.CreateDirectory(Source);
    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class Observer(Action<string, string, string?> action) : IPreparationObserver
    {
        public void Reached(string boundary, string id, string? path) => action(boundary, id, path);
    }
    private sealed class Interruption : Exception;
    private OperationPreparationStore Store(Action<string, string, string?>? observe = null) =>
        new(Data, observe is null ? null : new Observer(observe));

    private void Populate()
    {
        Directory.CreateDirectory(Path.Combine(Source, "nested", "empty"));
        File.WriteAllBytes(Path.Combine(Source, "nested", "unknown.data"), [0, 255, 10, 128]);
        File.WriteAllBytes(Path.Combine(Source, "hidden.pd2capture"), [9, 8]);
        File.WriteAllText(Path.Combine(Source, ".hidden"), "opaque");
        File.WriteAllBytes(Path.Combine(Source, "extensionless"), []);
    }

    private static Dictionary<string, string> Bytes(string path) => Directory.Exists(path)
        ? Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(path, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))
        : [];

    // Evidence only lives for one sync: a later preparation replaces anything left behind instead of requiring review.
    private async Task AssertNextPreparationDiscardsLeftovers(OperationPreparationStore store)
    {
        var next = await store.PrepareAsync(Source);
        var remaining = Assert.Single(await store.AssessRecoveryAsync());
        Assert.Equal(next.OperationId, remaining.OperationId);
        Assert.Equal(RecoveryStatus.Prepared, remaining.Status);
    }

    [Fact]
    public async Task CompleteCaptureAndBackupSurviveRestartWithoutChangingAnySourceOrBaseline()
    {
        Populate();
        Directory.CreateDirectory(Data);
        await File.WriteAllTextAsync(Path.Combine(Data, "settings.json"), "unrelated settings");
        var before = Bytes(Source);
        var times = Directory.GetFiles(Source, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.GetLastWriteTimeUtc);
        var operation = await Store().PrepareAsync(Source);
        Assert.Equal(before.OrderBy(p => p.Key), Bytes(operation.CaptureDirectory).OrderBy(p => p.Key));
        Assert.Equal(before.OrderBy(p => p.Key), Bytes(operation.BackupDirectory).OrderBy(p => p.Key));
        Assert.True(Directory.Exists(Path.Combine(operation.BackupDirectory, "nested", "empty")));
        Assert.Equal(before.OrderBy(p => p.Key), Bytes(Source).OrderBy(p => p.Key));
        Assert.All(times, pair => Assert.Equal(pair.Value, File.GetLastWriteTimeUtc(pair.Key)));
        Assert.Equal("unrelated settings", await File.ReadAllTextAsync(Path.Combine(Data, "settings.json")));
        Assert.False(File.Exists(Path.Combine(Data, "local-sync-state.json")));
        Assert.False(operation.Files is FileEntry[]);
        var evidence = Bytes(Recovery);
        var recovered = Assert.Single(await Store().AssessRecoveryAsync());
        Assert.Equal(RecoveryStatus.Prepared, recovered.Status);
        Assert.Equal(operation, recovered.Operation! with { Files = operation.Files });
        Assert.Equal(RecoveryStatus.Prepared, Assert.Single(await Store().AssessRecoveryAsync()).Status);
        Assert.Equal(evidence.OrderBy(p => p.Key), Bytes(Recovery).OrderBy(p => p.Key));
        await AssertNextPreparationDiscardsLeftovers(Store());
    }

    [Fact]
    public async Task EmptyCollectionIsExplicitlyPreparedWithTwoVerifiedEmptySnapshots()
    {
        var operation = await Store().PrepareAsync(Source);
        Assert.Empty(operation.Files);
        Assert.Equal(CollectionInventory.Fingerprint([]), operation.CollectionFingerprint);
        Assert.True(Directory.Exists(operation.CaptureDirectory));
        Assert.True(Directory.Exists(operation.BackupDirectory));
        Assert.Equal(RecoveryStatus.Prepared, Assert.Single(await Store().AssessRecoveryAsync()).Status);
    }

    [Fact]
    public async Task CaptureRetainsIndependentBytesAndRecoveryPreservesLaterExternalEdits()
    {
        Populate();
        var operation = await Store().PrepareAsync(Source);
        var captured = Bytes(operation.CaptureDirectory);
        await File.WriteAllTextAsync(Path.Combine(Source, ".hidden"), "external edit after interruption");
        var edited = Bytes(Source);
        Assert.Equal(RecoveryStatus.SourceChanged, Assert.Single(await Store().AssessRecoveryAsync()).Status);
        Assert.Equal(RecoveryStatus.SourceChanged, Assert.Single(await Store().AssessRecoveryAsync()).Status);
        Assert.Equal(edited.OrderBy(p => p.Key), Bytes(Source).OrderBy(p => p.Key));
        Assert.Equal(captured.OrderBy(p => p.Key), Bytes(operation.CaptureDirectory).OrderBy(p => p.Key));
        Assert.Equal(captured.OrderBy(p => p.Key), Bytes(operation.BackupDirectory).OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData("capture", "change")]
    [InlineData("backup", "change")]
    [InlineData("capture", "missing")]
    [InlineData("backup", "missing")]
    [InlineData("capture", "extra")]
    [InlineData("backup", "extra")]
    public async Task DamagedOrIncompleteSnapshotsBlockWithoutSpeculativeRollback(string snapshot, string damage)
    {
        Populate();
        var operation = await Store().PrepareAsync(Source);
        var path = snapshot == "capture" ? operation.CaptureDirectory : operation.BackupDirectory;
        if (damage == "missing") File.Delete(Path.Combine(path, ".hidden"));
        else await File.WriteAllTextAsync(Path.Combine(path, damage == "extra" ? "extra" : ".hidden"), "changed");
        var source = Bytes(Source);
        var evidence = Bytes(Recovery);
        Assert.Equal(RecoveryStatus.Blocked, Assert.Single(await Store().AssessRecoveryAsync()).Status);
        Assert.Equal(source.OrderBy(p => p.Key), Bytes(Source).OrderBy(p => p.Key));
        Assert.Equal(evidence.OrderBy(p => p.Key), Bytes(Recovery).OrderBy(p => p.Key));
    }

    [Fact]
    public async Task EveryJournalFlushRenameAndCopyBoundaryCanBeInterruptedAndAssessedIdempotently()
    {
        // First discover every real persistence boundary, then interrupt each occurrence independently.
        await File.WriteAllBytesAsync(Path.Combine(Source, "opaque"), [0, 255, 1]);
        var trace = new List<string>();
        var traceData = Path.Combine(root, "trace-app");
        await new OperationPreparationStore(traceData, new Observer((boundary, _, _) => trace.Add(boundary))).PrepareAsync(Source);
        Assert.Contains("JournalTemporaryFlushed", trace);
        Assert.Contains("Renamed", trace);
        Assert.Contains("DirectoryFlushed", trace);
        Assert.Contains("FileFlushed", trace);
        Assert.Contains("FileChunkWritten", trace);
        Assert.Contains("FileRenamed", trace);
        Assert.Contains("FileDirectoryFlushed", trace);
        for (var stop = 0; stop < trace.Count; stop++)
        {
            var app = Path.Combine(root, "interrupted-" + stop);
            var count = 0;
            var store = new OperationPreparationStore(app, new Observer((_, _, _) =>
            {
                if (count++ == stop) throw new Interruption();
            }));
            await Assert.ThrowsAsync<Interruption>(() => store.PrepareAsync(Source));
            var before = Bytes(app);
            var restarted = new OperationPreparationStore(app);
            var result = Assert.Single(await restarted.AssessRecoveryAsync());
            Assert.Equal(result.Status, Assert.Single(await restarted.AssessRecoveryAsync()).Status);
            // Only publication of the final Prepared record can yield Prepared after an interruption.
            Assert.Equal(stop >= trace.FindLastIndex(e => e == "BeforeJournal") + 2,
                result.Status == RecoveryStatus.Prepared);
            Assert.Equal(before.OrderBy(p => p.Key), Bytes(app).OrderBy(p => p.Key));
            Assert.Equal(new byte[] { 0, 255, 1 }, await File.ReadAllBytesAsync(Path.Combine(Source, "opaque")));
            await AssertNextPreparationDiscardsLeftovers(restarted);
        }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("malformed")]
    [InlineData("chain")]
    [InlineData("sequence")]
    [InlineData("transition")]
    [InlineData("identity")]
    [InlineData("path")]
    [InlineData("files")]
    [InlineData("missing")]
    [InlineData("temporary")]
    public async Task UnsupportedOrCorruptJournalsAreRetainedAndBlock(string damage)
    {
        Populate();
        var operation = await Store().PrepareAsync(Source);
        var directory = Path.GetDirectoryName(operation.CaptureDirectory)!;
        var first = Path.Combine(directory, "journal-000000.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(first))!.AsObject();
        switch (damage)
        {
            case "version": document["SchemaVersion"] = 2; break;
            case "unknown": document["Unknown"] = true; break;
            case "chain": document["PreviousDigest"] = new string('0', 64); break;
            case "sequence": document["Sequence"] = 8; break;
            case "transition": document["Phase"] = (int)PreparationPhase.Prepared; break;
            case "identity": document["OperationId"] = Guid.NewGuid().ToString("N"); break;
            case "path": document["SourceRoot"] = Data; break;
            case "files": document["Files"] = null; break;
        }
        if (damage == "missing") File.Delete(first);
        else if (damage == "temporary") File.Move(first, first + ".tmp");
        else if (damage == "malformed") await File.WriteAllTextAsync(first, "{");
        else if (damage == "duplicate") await File.WriteAllTextAsync(first, "{\"SchemaVersion\":1," + document.ToJsonString()[1..]);
        else await File.WriteAllTextAsync(first, document.ToJsonString());
        var before = Bytes(Recovery);
        Assert.Equal(RecoveryStatus.Blocked, Assert.Single(await Store().AssessRecoveryAsync()).Status);
        Assert.Equal(before.OrderBy(p => p.Key), Bytes(Recovery).OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("path")]
    [InlineData("files")]
    [InlineData("fingerprint")]
    [InlineData("inventory-binding")]
    [InlineData("size")]
    public async Task InventoryMetadataMustBeStrictValidAndBoundToTheJournal(string damage)
    {
        Populate();
        var operation = await Store().PrepareAsync(Source);
        var path = Path.Combine(Path.GetDirectoryName(operation.CaptureDirectory)!, "inventory.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        switch (damage)
        {
            case "version": document["SchemaVersion"] = 2; break;
            case "unknown": document["Other"] = true; break;
            case "path": document["SourceRoot"] = Data; break;
            case "files": document["Files"] = null; break;
            case "fingerprint": document["CollectionFingerprint"] = new string('0', 64); break;
            case "inventory-binding": document["SourceRoot"] = Path.Combine(root, "other-safe-source"); break;
        }
        if (damage == "duplicate") await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1," + document.ToJsonString()[1..]);
        else if (damage == "size") await File.WriteAllTextAsync(path, new string(' ', 8 * 1024 * 1024 + 1));
        else await File.WriteAllTextAsync(path, document.ToJsonString());
        Assert.Equal(RecoveryStatus.Blocked, Assert.Single(await Store().AssessRecoveryAsync()).Status);
    }

    [Theory]
    [InlineData("unknown-operation-entry")]
    [InlineData("pending-bytes")]
    [InlineData("missing-source")]
    public async Task UnexpectedEvidenceIsBlockedForAssessmentAndDiscardedByTheNextPreparation(string condition)
    {
        Populate();
        var operation = await Store().PrepareAsync(Source);
        var directory = Path.GetDirectoryName(operation.CaptureDirectory)!;
        if (condition == "unknown-operation-entry") await File.WriteAllTextAsync(Path.Combine(directory, "unknown"), "retain");
        else if (condition == "pending-bytes") await File.WriteAllTextAsync(Path.Combine(directory, "pending", "orphan"), "retain");
        else Directory.Move(Source, Source + "-moved");
        Assert.Equal(condition == "missing-source" ? RecoveryStatus.SourceChanged : RecoveryStatus.Blocked,
            Assert.Single(await Store().AssessRecoveryAsync()).Status);
        if (condition == "missing-source") await Assert.ThrowsAnyAsync<Exception>(() => Store().PrepareAsync(Source));
        else await AssertNextPreparationDiscardsLeftovers(Store());
    }

    [Fact]
    public async Task OversizeInventoryBlocksAsAWholeWithoutExcludingEligibleFiles()
    {
        for (var i = 0; i < 1001; i++) File.WriteAllBytes(Path.Combine(Source, "file-" + i), []);
        await Assert.ThrowsAsync<RecoveryBlockedException>(() => Store().PrepareAsync(Source));
        Assert.Equal(1001, Directory.GetFiles(Source).Length);
        Assert.Empty(Directory.GetDirectories(Recovery));
    }

    [Theory]
    [InlineData("FileFlushed")]
    [InlineData("Journal:Captured:False")]
    [InlineData("Journal:BackingUp:False")]
    public async Task SourceChangesDuringPreparationNeverProducePreparedEvidence(string boundary)
    {
        await File.WriteAllTextAsync(Path.Combine(Source, "opaque"), "original");
        var changed = false;
        var store = Store((current, _, _) =>
        {
            if (current != boundary || changed) return;
            changed = true;
            File.WriteAllText(Path.Combine(Source, "opaque"), "external edit");
        });
        await Assert.ThrowsAnyAsync<IOException>(() => store.PrepareAsync(Source));
        Assert.True(changed);
        Assert.NotEqual(RecoveryStatus.Prepared, Assert.Single(await Store().AssessRecoveryAsync()).Status);
        Assert.Equal("external edit", await File.ReadAllTextAsync(Path.Combine(Source, "opaque")));
    }

    [Fact]
    public async Task TamperingWithCaptureWhileBackingUpCannotProducePreparedEvidence()
    {
        Populate();
        await Assert.ThrowsAsync<RecoveryBlockedException>(() => Store((boundary, id, _) =>
        {
            if (boundary == "Journal:Captured:False") File.WriteAllText(Path.Combine(Recovery, id, "capture", ".hidden"), "tampered");
        }).PrepareAsync(Source));
        Assert.Equal(RecoveryStatus.Blocked, Assert.Single(await Store().AssessRecoveryAsync()).Status);
    }

    [Fact]
    public async Task CancellationRetainsEvidenceAndReleasesTheSerializationGate()
    {
        Populate();
        using var cancellation = new CancellationTokenSource();
        var store = Store((boundary, _, _) =>
        {
            if (boundary == "FileFlushed") cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PrepareAsync(Source, cancellation.Token));
        Assert.Equal(RecoveryStatus.Interrupted, Assert.Single(await Store().AssessRecoveryAsync()).Status);
        await AssertNextPreparationDiscardsLeftovers(Store());
    }

    [Fact]
    public async Task StoresSharingStorageSerializeAndCancelledWaitersDoNotReleaseAnotherLease()
    {
        Populate();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Store((boundary, _, _) =>
        {
            if (boundary != "BeforeJournal") return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        }).PrepareAsync(Source);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var waiting = Store().PrepareAsync(Source, cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            var assessment = Store().AssessRecoveryAsync();
            Assert.False(assessment.IsCompleted);
            release.Set();
            await first;
            Assert.Equal(RecoveryStatus.Prepared, Assert.Single(await assessment).Status);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task AnExistingExclusiveLeaseBlocksPreparationWithoutCreatingAnOperation()
    {
        Directory.CreateDirectory(Recovery);
        using var lease = new FileStream(Path.Combine(Recovery, "operations.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<RecoveryBlockedException>(() => Store().PrepareAsync(Source));
        Assert.Empty(Directory.GetDirectories(Recovery));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("storage")]
    [InlineData("journal")]
    [InlineData("backup")]
    public async Task LinkedSourcesStorageAndEvidenceAreRejected(string location)
    {
        if (OperatingSystem.IsWindows()) return; // Native reparse/ACL acceptance remains separate.
        Populate();
        if (location == "source")
        {
            File.CreateSymbolicLink(Path.Combine(Source, "link"), Path.Combine(Source, ".hidden"));
            await Assert.ThrowsAsync<RecoveryBlockedException>(() => Store().PrepareAsync(Source));
            return;
        }
        if (location == "storage")
        {
            Directory.CreateDirectory(Data);
            Directory.CreateSymbolicLink(Recovery, Source);
            await Assert.ThrowsAnyAsync<IOException>(() => Store().PrepareAsync(Source));
            return;
        }
        var operation = await Store().PrepareAsync(Source);
        var path = location == "journal" ? Path.Combine(Path.GetDirectoryName(operation.BackupDirectory)!, "journal-000000.json")
            : Path.Combine(operation.BackupDirectory, ".hidden");
        File.Delete(path);
        File.CreateSymbolicLink(path, Path.Combine(Source, ".hidden"));
        Assert.Equal(RecoveryStatus.Blocked, Assert.Single(await Store().AssessRecoveryAsync()).Status);
    }

    [Fact]
    public async Task ApplicationStorageOverlapsAreRejectedBeforeAnySourceChanges()
    {
        await Assert.ThrowsAsync<RecoveryBlockedException>(() => Store().PrepareAsync(root));
        await Assert.ThrowsAsync<RecoveryBlockedException>(() => Store().PrepareAsync(Data));
        await Assert.ThrowsAsync<RecoveryBlockedException>(() => Store().PrepareAsync(Path.Combine(Data, "recovery", "source")));
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public async Task CanonicalUnicodeSnapshotPathsPreservePhysicalSourceSpelling()
    {
        var physical = "e\u0301.opaque";
        await File.WriteAllBytesAsync(Path.Combine(Source, physical), [1, 0, 255]);
        var operation = await Store().PrepareAsync(Source);
        Assert.Equal("é.opaque", Assert.Single(operation.Files).RelativePath);
        Assert.True(File.Exists(Path.Combine(Source, physical)));
        Assert.Equal(new byte[] { 1, 0, 255 }, await File.ReadAllBytesAsync(Path.Combine(operation.BackupDirectory, "é.opaque")));
        Assert.Equal(RecoveryStatus.Prepared, Assert.Single(await Store().AssessRecoveryAsync()).Status);
    }

    private Process StartProbe(string mode, string boundary)
    {
        var assembly = typeof(RecoveryProbe.Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(assembly, ".runtimeconfig.json"));
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(Data);
        start.ArgumentList.Add(Source);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(boundary);
        return Process.Start(start)!;
    }

    [Theory]
    [InlineData("JournalTemporaryFlushed", RecoveryStatus.Blocked)]
    [InlineData("FileChunkWritten", RecoveryStatus.Interrupted)]
    [InlineData("FileRenamed", RecoveryStatus.Interrupted)]
    [InlineData("Journal:Prepared:False", RecoveryStatus.Prepared)]
    public async Task AbruptSubprocessExitLeavesAssessableEvidenceWithoutManagedCleanup(string boundary, RecoveryStatus expected)
    {
        Populate();
        var before = Bytes(Source);
        using var child = StartProbe("exit", boundary);
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("boundary reached", (await child.StandardOutput.ReadToEndAsync()).Trim());
            Assert.Equal(73, child.ExitCode);
            Assert.Equal(expected, Assert.Single(await Store().AssessRecoveryAsync()).Status);
            Assert.Equal(expected, Assert.Single(await Store().AssessRecoveryAsync()).Status);
            Assert.Equal(before.OrderBy(p => p.Key), Bytes(Source).OrderBy(p => p.Key));
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }

    [Fact]
    public async Task SeparateProcessLeaseBlocksConcurrentOperationsAndIsReleasedByProcessDeath()
    {
        Populate();
        using var child = StartProbe("hold", "BeforeJournal");
        try
        {
            Assert.Equal("boundary reached", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            await Assert.ThrowsAsync<RecoveryBlockedException>(() => Store().PrepareAsync(Source));
            await Assert.ThrowsAsync<RecoveryBlockedException>(() => Store().AssessRecoveryAsync());
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(RecoveryStatus.Blocked, Assert.Single(await Store().AssessRecoveryAsync()).Status);
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }
}
