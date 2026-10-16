using PD2Cloud.Core;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class CollectionComparisonTests
{
    // Opaque identities deliberately need no real folders, GitHub client or repository.
    private static readonly CollectionPairing Pairing = new("repo-1", "main", "local-root");
    private static readonly FileEntry A = new("nested/a", 1, new string('a', 64));
    private static readonly FileEntry B = new("b", 2, new string('b', 64));
    private static FileEntry[] Files(string state) => state switch
    {
        "empty" => [],
        "base" => [A, B],
        "local-edit" => [A with { Sha256 = new string('c', 64) }, B],
        "remote-edit" => [A, B with { Sha256 = new string('d', 64) }],
        "added" => [A, B, new("extra", 0, new string('e', 64))],
        "missing-a" => [B],
        "missing-b" => [A],
        "renamed" => [A with { RelativePath = "renamed" }, B],
        _ => throw new ArgumentException(state)
    };

    private static CollectionManifest Manifest(string state, string root = "local-root") =>
        Manifest(Files(state), root);
    private static CollectionManifest Manifest(FileEntry[] files, string root = "local-root") =>
        new(1, root, files, CollectionInventory.Fingerprint(files), DateTimeOffset.UnixEpoch, true, []);
    private static LocalSyncState Baseline(string state = "base") =>
        new(1, Pairing.RepositoryIdentity, Pairing.Branch, "accepted-commit", Pairing.LocalRootPath, Files(state), DateTimeOffset.UnixEpoch, CollectionInventory.Fingerprint(Files(state)));
    private static RemoteCollection Remote(string state) =>
        new(Pairing.RepositoryIdentity, Pairing.Branch, "new-commit", Manifest(state, "remote-root"), RemoteVerification.Verified, BaselineHistory.ContainsBaseline);

    [Theory]
    [InlineData(null, "empty", "empty", ComparisonDecision.NoTransfer)]
    [InlineData(null, "base", "base", ComparisonDecision.NoTransfer)]
    [InlineData(null, "base", "empty", ComparisonDecision.InitialUpload)]
    [InlineData(null, "empty", "base", ComparisonDecision.InitialDownload)]
    [InlineData(null, "local-edit", "remote-edit", ComparisonDecision.Conflict)]
    [InlineData(null, "base", "added", ComparisonDecision.Conflict)]
    [InlineData("base", "base", "base", ComparisonDecision.NoTransfer)]
    [InlineData("base", "local-edit", "local-edit", ComparisonDecision.NoTransfer)]
    [InlineData("base", "added", "added", ComparisonDecision.NoTransfer)]
    [InlineData("base", "local-edit", "base", ComparisonDecision.Upload)]
    [InlineData("base", "added", "base", ComparisonDecision.Upload)]
    [InlineData("base", "base", "remote-edit", ComparisonDecision.Download)]
    [InlineData("base", "base", "added", ComparisonDecision.Download)]
    [InlineData("base", "local-edit", "remote-edit", ComparisonDecision.Conflict)]
    [InlineData("base", "local-edit", "added", ComparisonDecision.Conflict)]
    [InlineData("empty", "empty", "empty", ComparisonDecision.NoTransfer)]
    [InlineData("empty", "base", "empty", ComparisonDecision.Upload)]
    [InlineData("empty", "empty", "base", ComparisonDecision.Download)]
    [InlineData("empty", "local-edit", "remote-edit", ComparisonDecision.Conflict)]
    public void CompleteCollectionDecisionTable(string? baseline, string local, string remote, ComparisonDecision expected)
    {
        var result = CollectionComparer.Compare(Pairing, Manifest(local), Remote(remote), baseline is null ? null : Baseline(baseline));
        Assert.Equal(expected, result.Decision);
        Assert.Equal(ComparisonBlock.None, result.Block);
        Assert.Empty(result.MissingLocally);
        Assert.Empty(result.MissingRemotely);
    }

    [Theory]
    [InlineData("missing-a", "base", "nested/a", "")]
    [InlineData("base", "missing-b", "", "b")]
    [InlineData("missing-a", "missing-b", "nested/a", "b")]
    [InlineData("missing-a", "missing-a", "nested/a", "nested/a")]
    [InlineData("empty", "empty", "b,nested/a", "b,nested/a")]
    [InlineData("empty", "remote-edit", "b,nested/a", "")]
    [InlineData("local-edit", "empty", "", "b,nested/a")]
    [InlineData("renamed", "base", "nested/a", "")]
    public void MissingBaselineFilesRequireReviewBeforeEqualityOrDirection(string local, string remote, string missingLocal, string missingRemote)
    {
        var result = CollectionComparer.Compare(Pairing, Manifest(local), Remote(remote), Baseline());
        Assert.Equal(ComparisonDecision.MissingFileReview, result.Decision);
        Assert.Equal(missingLocal, string.Join(',', result.MissingLocally));
        Assert.Equal(missingRemote, string.Join(',', result.MissingRemotely));
    }

    [Fact]
    public void EqualityIgnoresOrderingRootAndInspectionTimeButDetectsPathHashAndSizeChanges()
    {
        var local = Manifest("base") with { Files = Files("base").Reverse().ToArray(), InspectedAt = DateTimeOffset.UtcNow };
        Assert.Equal(ComparisonDecision.NoTransfer, CollectionComparer.Compare(Pairing, local, Remote("base")).Decision);
        foreach (var changed in new[] { A with { RelativePath = "new-name" }, A with { Sha256 = new string('f', 64) }, A with { SizeBytes = 3 } })
            Assert.Equal(ComparisonDecision.Conflict, CollectionComparer.Compare(Pairing, Manifest([changed, B]), Remote("base")).Decision);
    }

    [Theory]
    [InlineData(RemoteVerification.Unavailable, ComparisonBlock.RemoteUnavailable)]
    [InlineData(RemoteVerification.Unverified, ComparisonBlock.RemoteUnverified)]
    public void UnknownRemoteNeverMeansEmptyEvenWithCachedInventory(RemoteVerification verification, ComparisonBlock expected)
    {
        foreach (var manifest in new CollectionManifest?[] { null, Manifest("empty"), Manifest("base") })
            AssertBlocked(CollectionComparer.Compare(Pairing, Manifest("base"), Remote("base") with { Verification = verification, Manifest = manifest }), expected);
    }

    public static IEnumerable<object?[]> InvalidManifests()
    {
        var valid = Manifest("base");
        yield return [null];
        yield return [valid with { SchemaVersion = 2 }];
        yield return [valid with { IsStable = false }];
        yield return [valid with { Issues = [new(ValidationCode.ReadError, "unreadable")] }];
        yield return [valid with { CollectionFingerprint = null }];
        yield return [valid with { CollectionFingerprint = Manifest("empty").CollectionFingerprint }];
        yield return [valid with { Files = null! }];
        foreach (var files in InvalidFiles()) yield return [valid with { Files = files }];
    }

    private static IEnumerable<FileEntry[]> InvalidFiles()
    {
        yield return [null!];
        yield return [A, A];
        yield return [A, A with { RelativePath = "Nested/a" }];
        yield return [A, B with { RelativePath = "NESTED/other" }];
        yield return [A, B with { RelativePath = "nested" }];
        yield return [A with { RelativePath = "../escape" }];
        yield return [A with { RelativePath = "e\u0301" }];
        yield return [A with { SizeBytes = -1 }];
        yield return [A with { Sha256 = "invalid" }];
        yield return [A with { Sha256 = new string('A', 64) }];
        yield return [A with { Sha256 = null! }];
    }

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public void InvalidInspectionsBlockBeforeEqualityOrMissingFileReview(CollectionManifest? invalid)
    {
        AssertBlocked(CollectionComparer.Compare(Pairing, invalid, Remote("base"), Baseline()), ComparisonBlock.InvalidLocalInspection);
        AssertBlocked(CollectionComparer.Compare(Pairing, Manifest("empty"), Remote("base") with { Manifest = invalid }, Baseline()), ComparisonBlock.InvalidRemoteInspection);
    }

    [Fact]
    public void PairingAndBaselineIdentityAreCheckedEvenWhenInventoriesMatch()
    {
        foreach (var baseline in new[] { Baseline() with { RepositoryIdentity = "other" }, Baseline() with { Branch = "Main" }, Baseline() with { LocalRootPath = "other-root" } })
            AssertBlocked(CollectionComparer.Compare(Pairing, Manifest("base"), Remote("base"), baseline), ComparisonBlock.BaselineIdentityMismatch);
        foreach (var remote in new[] { Remote("base") with { RepositoryIdentity = "other" }, Remote("base") with { Branch = "Main" } })
            AssertBlocked(CollectionComparer.Compare(Pairing, Manifest("base"), remote), ComparisonBlock.RemoteIdentityMismatch);
        AssertBlocked(CollectionComparer.Compare(Pairing with { Branch = "" }, Manifest("base"), Remote("base")), ComparisonBlock.InvalidPairing);
        AssertBlocked(CollectionComparer.Compare(Pairing, Manifest("base", "other-root"), Remote("base")), ComparisonBlock.InvalidLocalInspection);
    }

    [Fact]
    public void InvalidBaselineCannotFallBackToInitialSync()
    {
        foreach (var baseline in new[] { Baseline() with { SchemaVersion = 2 }, Baseline() with { LastSynchronizedCommit = "" }, Baseline() with { Files = null! } }
            .Concat(InvalidFiles().Select(files => Baseline() with { Files = files })))
            AssertBlocked(CollectionComparer.Compare(Pairing, Manifest("base"), Remote("base"), baseline), ComparisonBlock.InvalidBaseline);
    }

    [Fact]
    public void PinnedRevisionAndBaselineHistoryAreRequiredBeforeComparison()
    {
        AssertBlocked(CollectionComparer.Compare(Pairing, Manifest("base"), Remote("base") with { Commit = null }), ComparisonBlock.RemoteUnverified);
        AssertBlocked(CollectionComparer.Compare(Pairing, Manifest("base"), Remote("base") with { History = BaselineHistory.Unknown }, Baseline()), ComparisonBlock.UnverifiedHistory);
        AssertBlocked(CollectionComparer.Compare(Pairing, Manifest("base"), Remote("base") with { History = BaselineHistory.Rewritten }, Baseline()), ComparisonBlock.RewrittenHistory);
        var sameCommit = Remote("base") with { Commit = "accepted-commit", History = BaselineHistory.Unknown };
        Assert.Equal(ComparisonDecision.NoTransfer, CollectionComparer.Compare(Pairing, Manifest("base"), sameCommit, Baseline()).Decision);
    }

    [Fact]
    public void ComparisonIsRepeatableAndDoesNotMutateInputsOrCreateBaseline()
    {
        var baseline = Baseline();
        var files = baseline.Files.ToArray();
        var local = Manifest("local-edit");
        var remote = Remote("base");
        var first = CollectionComparer.Compare(Pairing, local, remote, baseline);
        var second = CollectionComparer.Compare(Pairing, local, remote, baseline);
        Assert.Equal(first.Decision, second.Decision);
        Assert.Equal(files, baseline.Files);
        Assert.Equal("accepted-commit", baseline.LastSynchronizedCommit);
        Assert.Equal(DateTimeOffset.UnixEpoch, baseline.SynchronizedAt);
        Assert.Equal(ComparisonDecision.InitialUpload, CollectionComparer.Compare(Pairing, local, Remote("empty")).Decision);
        Assert.Equal(ComparisonDecision.InitialUpload, CollectionComparer.Compare(Pairing, local, Remote("empty")).Decision);
    }

    private static void AssertBlocked(CollectionComparison result, ComparisonBlock expected)
    {
        Assert.Equal(ComparisonDecision.Blocked, result.Decision);
        Assert.Equal(expected, result.Block);
        Assert.Empty(result.MissingLocally);
        Assert.Empty(result.MissingRemotely);
    }
}
