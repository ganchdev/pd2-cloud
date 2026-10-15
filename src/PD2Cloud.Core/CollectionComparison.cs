namespace PD2Cloud.Core;

/// <summary>Canonical pairing identifiers supplied by the caller; compared ordinally without filesystem access.</summary>
public sealed record CollectionPairing(string RepositoryIdentity, string Branch, string LocalRootPath);

public enum RemoteVerification { Unavailable, Unverified, Verified }
public enum BaselineHistory { Unknown, ContainsBaseline, Rewritten }

/// <summary>
/// Verified means a complete inventory from one pinned revision, never an offline cache or missing branch.
/// History evidence must refer to the supplied baseline's accepted commit. The comparer performs no remote verification.
/// </summary>
public sealed record RemoteCollection(string RepositoryIdentity, string Branch, string? Commit,
    CollectionManifest? Manifest, RemoteVerification Verification, BaselineHistory History = BaselineHistory.Unknown);

public enum ComparisonDecision { NoTransfer, InitialUpload, InitialDownload, Upload, Download, Conflict, MissingFileReview, Blocked }
public enum ComparisonBlock
{
    None, InvalidPairing, InvalidLocalInspection, RemoteUnavailable, RemoteUnverified,
    RemoteIdentityMismatch, InvalidRemoteInspection, BaselineIdentityMismatch, InvalidBaseline, UnverifiedHistory, RewrittenHistory
}

public sealed record CollectionComparison(ComparisonDecision Decision, ComparisonBlock Block,
    IReadOnlyList<string> MissingLocally, IReadOnlyList<string> MissingRemotely);

/// <summary>Pure whole-collection planning only. Results never authorize writes or establish a successful-sync baseline.</summary>
public static class CollectionComparer
{
    public static CollectionComparison Compare(CollectionPairing pairing, CollectionManifest? local,
        RemoteCollection remote, LocalSyncState? baseline = null)
    {
        static CollectionComparison Blocked(ComparisonBlock reason) => new(ComparisonDecision.Blocked, reason, [], []);
        static CollectionComparison Result(ComparisonDecision decision) => new(decision, ComparisonBlock.None, [], []);

        if (string.IsNullOrWhiteSpace(pairing.RepositoryIdentity) || string.IsNullOrWhiteSpace(pairing.Branch)
            || string.IsNullOrWhiteSpace(pairing.LocalRootPath)) return Blocked(ComparisonBlock.InvalidPairing);
        if (!ValidManifest(local) || local!.RootPath != pairing.LocalRootPath) return Blocked(ComparisonBlock.InvalidLocalInspection);
        if (remote.Verification == RemoteVerification.Unavailable) return Blocked(ComparisonBlock.RemoteUnavailable);
        if (remote.Verification != RemoteVerification.Verified || string.IsNullOrWhiteSpace(remote.Commit))
            return Blocked(ComparisonBlock.RemoteUnverified);
        if (remote.RepositoryIdentity != pairing.RepositoryIdentity || remote.Branch != pairing.Branch)
            return Blocked(ComparisonBlock.RemoteIdentityMismatch);
        if (!ValidManifest(remote.Manifest)) return Blocked(ComparisonBlock.InvalidRemoteInspection);
        var remoteFiles = remote.Manifest!.Files;

        if (baseline is not null)
        {
            if (baseline.RepositoryIdentity != pairing.RepositoryIdentity || baseline.Branch != pairing.Branch
                || baseline.LocalRootPath != pairing.LocalRootPath) return Blocked(ComparisonBlock.BaselineIdentityMismatch);
            if (baseline.SchemaVersion != 1 || string.IsNullOrWhiteSpace(baseline.LastSynchronizedCommit)
                || !ValidFiles(baseline.Files)) return Blocked(ComparisonBlock.InvalidBaseline);
            if (remote.History == BaselineHistory.Rewritten) return Blocked(ComparisonBlock.RewrittenHistory);
            if (remote.Commit != baseline.LastSynchronizedCommit && remote.History != BaselineHistory.ContainsBaseline)
                return Blocked(ComparisonBlock.UnverifiedHistory);

            // Missing files take precedence over equality and one-sided changes, including matching removals.
            var localPaths = local.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
            var remotePaths = remoteFiles.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
            var missingLocal = baseline.Files.Select(f => f.RelativePath).Where(p => !localPaths.Contains(p)).Order(StringComparer.Ordinal).ToArray();
            var missingRemote = baseline.Files.Select(f => f.RelativePath).Where(p => !remotePaths.Contains(p)).Order(StringComparer.Ordinal).ToArray();
            if (missingLocal.Length > 0 || missingRemote.Length > 0)
                return new(ComparisonDecision.MissingFileReview, ComparisonBlock.None, missingLocal, missingRemote);
        }

        if (Equal(local.Files, remoteFiles)) return Result(ComparisonDecision.NoTransfer);
        if (baseline is null)
            return Result(local.Files.Count == 0 ? ComparisonDecision.InitialDownload :
                remoteFiles.Count == 0 ? ComparisonDecision.InitialUpload : ComparisonDecision.Conflict);
        if (Equal(baseline.Files, remoteFiles)) return Result(ComparisonDecision.Upload);
        if (Equal(baseline.Files, local.Files)) return Result(ComparisonDecision.Download);
        return Result(ComparisonDecision.Conflict);
    }

    private static bool Equal(IReadOnlyList<FileEntry> left, IReadOnlyList<FileEntry> right) =>
        left.OrderBy(f => f.RelativePath, StringComparer.Ordinal).SequenceEqual(right.OrderBy(f => f.RelativePath, StringComparer.Ordinal));

    private static bool ValidManifest(CollectionManifest? manifest) => manifest is { SchemaVersion: 1, IsStable: true, Issues.Count: 0 }
        && ValidFiles(manifest.Files) && manifest.CollectionFingerprint == CollectionInventory.Fingerprint(manifest.Files);

    private static bool ValidFiles(IReadOnlyList<FileEntry>? files)
    {
        if (files is null) return false;
        var entries = new Dictionary<string, (string Path, bool File)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file is null || !RelativePaths.IsSafe(file.RelativePath) || file.RelativePath != RelativePaths.Canonical(file.RelativePath)
                || file.SizeBytes < 0 || file.Sha256 is not { Length: 64 } || file.Sha256.Any(c => !char.IsAsciiHexDigitLower(c))) return false;
            // Include implied directories so A/x + a/y and file + file/child cannot slip through.
            var segments = file.RelativePath.Split('/');
            var path = "";
            for (var i = 0; i < segments.Length; i++)
            {
                path += (i == 0 ? "" : "/") + segments[i];
                var isFile = i == segments.Length - 1;
                var key = RelativePaths.CollisionKey(path);
                if (entries.TryGetValue(key, out var previous))
                {
                    if (previous.Path != path || previous.File || isFile) return false;
                }
                else entries.Add(key, (path, isFile));
            }
        }
        return true;
    }
}
