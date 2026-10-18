using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PD2Cloud.Core;
using PD2Cloud.GitHub;
using PD2Cloud.Local;

namespace PD2Cloud.Remote;

/// <summary>Uploads one prepared local snapshot on top of the pinned repository revision it was compared with.</summary>
public sealed record UploadRequest(string OperationId, string SourceRoot, string CollectionFingerprint,
    RepositoryChoice Repository, string PinnedCommit, RepositoryBootstrapCase Bootstrap = RepositoryBootstrapCase.None);

/// <summary>
/// The pinned repository classification. None replaces an existing managed collection (empty or not). Repair
/// replaces a collection that can no longer be verified, only at the user's explicit request. The others set up
/// cloud storage in a repository that has none yet.
/// </summary>
public enum RepositoryBootstrapCase { None, UnbornRepository, EmptyTree, Unrelated, Repair }

public enum UploadStatus { Published, PublishedSourceChanged, NotPublished, ConcurrentUpdate }
public sealed record UploadResult(UploadStatus Status, string? CandidateCommit);

/// <summary>Publishes a prepared snapshot with one non-forced branch update. Never replaces source files.</summary>
public sealed class CollectionUploader(OperationPreparationStore preparations, GitHubConnection connection, LocalSyncStateStore syncStateStore)
{
    private const string UploadDirection = "Upload";
    private const string InitializeDirection = "Initialize";
    private const string BootstrapDirection = "Bootstrap";
    private const string AdoptDirection = "Adopt";
    private const string RepairDirection = "Repair";
    // Local-only placeholder used to round-trip verify a generated tree for an unborn repository before any object is posted.
    private const string UnbornVerificationCommit = "0000000000000000000000000000000000000000";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };
    private sealed record Intent(int SchemaVersion, string Direction, string OperationId, string SourceRoot,
        string Fingerprint, string PreparationDigest, RepositoryChoice Repository, string? Parent, string? OriginalTree);
    private sealed record Candidate(int SchemaVersion, string Direction, string IntentDigest, string Commit, string Tree);
    private sealed record Receipt(int SchemaVersion, string CandidateDigest, string Commit);

    public Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken ct = default) =>
        preparations.CoordinateUploadAsync(request.OperationId, evidence =>
            connection.CoordinateRepositoryAsync(request.Repository, async writer =>
            {
                var operation = evidence.Operation;
                Require(request.SourceRoot == operation.SourceRoot && request.CollectionFingerprint == operation.CollectionFingerprint
                    && operation.Files.Count > 0
                    && (request.Bootstrap == RepositoryBootstrapCase.UnbornRepository ? request.PinnedCommit.Length == 0 : IsId(request.PinnedCommit)),
                    "Reviewed local evidence changed.");
                var original = await writer.ReadAsync().ConfigureAwait(false);
                var verified = CloudCollectionReader.Verify(original, ct);
                ValidateReviewedState(request, original, verified);
                ValidateLimits(operation.Files);
                var manifest = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Format = "PD2Cloud.Collection",
                    SchemaVersion = 1,
                    operation.Files,
                    operation.CollectionFingerprint
                });
                Require(manifest.Length <= CloudStorage.MaxManifestBytes && operation.Files.Sum(f => f.SizeBytes) + manifest.Length <= CloudStorage.MaxCollectionBytes,
                    "Upload exceeds managed collection limits.");
                // Build and verify every payload before any remote object creation. Retained arrays are privately owned.
                var payloads = operation.Files.ToDictionary(f => CloudStorage.FilesPath + "/" + f.RelativePath, evidence.ReadFile, StringComparer.Ordinal);
                payloads.Add(CloudStorage.ManifestPath, manifest);
                var blobEntries = payloads.Select(p => new GitTreeEntry(p.Key, "100644", "blob", CloudStorage.BlobId(p.Value), p.Value.Length)).ToList();
                var entries = BuildEntries(original, blobEntries);
                var tree = CloudStorage.TreeId(entries.Where(e => !e.Path.Contains('/')));
                var candidateSnapshot = new RepositorySnapshot(request.Repository, request.Bootstrap == RepositoryBootstrapCase.UnbornRepository
                    ? UnbornVerificationCommit : original.Commit, tree, entries,
                    payloads.Values.GroupBy(CloudStorage.BlobId).ToDictionary(g => g.Key, g => g.First()));
                var candidateVerification = CloudCollectionReader.Verify(candidateSnapshot, ct);
                Require(candidateVerification.Content == RepositoryContent.ManagedCollection
                    && candidateVerification.Collection!.Manifest!.Files.SequenceEqual(operation.Files), "Generated upload failed round-trip verification.");
                await evidence.RevalidateAsync().ConfigureAwait(false);
                var direction = DirectionOf(request);
                var unborn = request.Bootstrap == RepositoryBootstrapCase.UnbornRepository;
                var intent = new Intent(1, direction, operation.OperationId, operation.SourceRoot, operation.CollectionFingerprint,
                    evidence.PreparationDigest, request.Repository, original.Commit, original.Tree);
                var intentBytes = Encode(intent);
                evidence.WriteRecord("intent.json", intentBytes);
                // GitHub's Git Data API rejects writes to an empty repository, so an unborn repository is first seeded
                // with its manifest (creating the initial commit and default branch) through the Contents API.
                (string Commit, string Tree) seed = (string.Empty, string.Empty);
                if (unborn)
                {
                    seed = await writer.CreateFileAsync(CloudStorage.ManifestPath, manifest, operation.OperationId).ConfigureAwait(false);
                    Require(IsId(seed.Commit) && IsId(seed.Tree), "The initial repository commit could not be verified.");
                }
                foreach (var bytes in payloads.Values.GroupBy(CloudStorage.BlobId).Select(g => g.First()))
                    _ = await writer.BlobAsync(bytes).ConfigureAwait(false);
                // Explicit direct-child trees avoid base_tree replacement/deletion semantics and preserve unrelated root IDs verbatim.
                foreach (var directory in entries.Where(e => e.Type == "tree" && (e.Path == CloudStorage.Root || e.Path.StartsWith(CloudStorage.Root + "/", StringComparison.Ordinal)))
                    .OrderByDescending(e => e.Path.Count(c => c == '/')))
                    _ = await writer.TreeAsync(Children(entries, directory.Path)).ConfigureAwait(false);
                _ = await writer.TreeAsync(Children(entries, "")).ConfigureAwait(false);
                var parent = unborn ? seed.Commit : original.Commit!;
                var commit = await writer.CommitAsync(tree, parent, operation.OperationId).ConfigureAwait(false);
                var candidateBytes = Encode(new Candidate(1, direction, Digest(intentBytes), commit, tree));
                evidence.WriteRecord("candidate.json", candidateBytes);
                await evidence.RevalidateAsync().ConfigureAwait(false);
                var current = await writer.ReadAsync().ConfigureAwait(false);
                if (unborn)
                    Require(current.Commit == seed.Commit && current.Tree == seed.Tree, "Remote repository changed. Retain candidate and refresh; never reparent automatically.");
                else
                    Require(current.Commit == original.Commit && current.Tree == original.Tree, "Remote head changed. Retain candidate and refresh; never reparent automatically.");
                await evidence.RevalidateAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                // Once attempted, any error/cancellation is an uncertain result. Only recovery may inspect it; never replay.
                await writer.PublishAsync(commit).ConfigureAwait(false);
                var result = await ConfirmAsync(evidence, writer, intent, new Candidate(1, direction, Digest(intentBytes), commit, tree), candidateBytes, ct).ConfigureAwait(false);
                if (result.Status == UploadStatus.Published)
                {
                    var syncState = new LocalSyncState(1, request.Repository.FullName, request.Repository.DefaultBranch,
                        result.CandidateCommit!, request.SourceRoot, operation.Files, DateTimeOffset.UtcNow,
                        request.CollectionFingerprint);
                    await syncStateStore.SaveSyncStateAsync(syncState).ConfigureAwait(false);
                }
                return result;
            }, ct), ct);

    private static void ValidateReviewedState(UploadRequest request, RepositorySnapshot original, CloudReadResult verified)
    {
        switch (request.Bootstrap)
        {
            case RepositoryBootstrapCase.None:
                Require(original.Commit == request.PinnedCommit && verified.Content == RepositoryContent.ManagedCollection,
                    "The cloud saves changed. Refresh and sync again; nothing was written.");
                break;
            case RepositoryBootstrapCase.UnbornRepository:
                Require(original.Commit is null && original.Tree is null && verified.Content == RepositoryContent.EmptyRepository,
                    "The repository changed. Refresh and sync again; nothing was written.");
                break;
            case RepositoryBootstrapCase.EmptyTree:
                Require(original.Commit == request.PinnedCommit && verified.Content == RepositoryContent.EmptyRepository,
                    "The repository changed. Refresh and sync again; nothing was written.");
                break;
            case RepositoryBootstrapCase.Repair:
                Require(original.Commit == request.PinnedCommit && verified.Content == RepositoryContent.Incompatible,
                    "The repository changed. Refresh and try again; nothing was written.");
                break;
            case RepositoryBootstrapCase.Unrelated:
                Require(original.Commit == request.PinnedCommit && verified.Content == RepositoryContent.Unrelated,
                    "The repository changed. Refresh and sync again; nothing was written.");
                break;
            default:
                throw new RecoveryBlockedException("Unsupported bootstrap direction.");
        }
    }

    private static string DirectionOf(UploadRequest request) => request.Bootstrap switch
    {
        RepositoryBootstrapCase.None => UploadDirection,
        RepositoryBootstrapCase.UnbornRepository => InitializeDirection,
        RepositoryBootstrapCase.EmptyTree => BootstrapDirection,
        RepositoryBootstrapCase.Unrelated => AdoptDirection,
        RepositoryBootstrapCase.Repair => RepairDirection,
        _ => throw new RecoveryBlockedException("Unsupported bootstrap direction.")
    };

    private static async Task<UploadResult> ConfirmAsync(UploadEvidence evidence, RepositoryWriter writer, Intent intent, Candidate candidate, byte[] candidateBytes, CancellationToken ct)
    {
        var current = await writer.ReadAsync().ConfigureAwait(false);
        if (current.Commit == intent.Parent)
            return new(evidence.ReadRecords().ContainsKey("published.json") ? UploadStatus.ConcurrentUpdate : UploadStatus.NotPublished, candidate.Commit);
        if (current.Commit != candidate.Commit) return new(UploadStatus.ConcurrentUpdate, candidate.Commit);
        var verified = CloudCollectionReader.Verify(current, ct);
        Require(current.Tree == candidate.Tree && verified.Content == RepositoryContent.ManagedCollection
            && verified.Collection!.Manifest!.Files.SequenceEqual(evidence.Operation.Files), "Published candidate failed verification.");
        await evidence.RevalidateAsync(false).ConfigureAwait(false);
        if (!evidence.ReadRecords().ContainsKey("published.json"))
            evidence.WriteRecord("published.json", Encode(new Receipt(1, Digest(candidateBytes), candidate.Commit)));
        try { await evidence.RevalidateAsync().ConfigureAwait(false); }
        catch (RecoveryBlockedException) { return new(UploadStatus.PublishedSourceChanged, candidate.Commit); }
        return new(UploadStatus.Published, candidate.Commit);
    }

    private static List<GitTreeEntry> BuildEntries(RepositorySnapshot original, List<GitTreeEntry> blobs)
    {
        var entries = original.Entries.Where(e => e.Path != CloudStorage.Root && !e.Path.StartsWith(CloudStorage.Root + "/", StringComparison.Ordinal)).Concat(blobs).ToList();
        var directories = new HashSet<string>(StringComparer.Ordinal) { CloudStorage.Root, CloudStorage.FilesPath };
        foreach (var file in blobs)
        {
            var path = file.Path;
            while (path.LastIndexOf('/') is var slash && slash >= 0) { path = path[..slash]; directories.Add(path); }
        }
        foreach (var path in directories.OrderByDescending(p => p.Count(c => c == '/')))
            entries.Add(new(path, "040000", "tree", CloudStorage.TreeId(Children(entries, path)), 0));
        Require(entries.Count <= CloudStorage.MaxEntries, "Generated repository tree exceeds read limits.");
        return entries;
    }
    private static GitTreeEntry[] Children(IEnumerable<GitTreeEntry> entries, string path) => entries
        .Where(e => (e.Path.Contains('/') ? e.Path[..e.Path.LastIndexOf('/')] : "") == path)
        .Select(e => e with { Path = e.Path[(e.Path.LastIndexOf('/') + 1)..] }).ToArray();
    private static void ValidateLimits(IReadOnlyList<FileEntry> files) => Require(files.Count <= CloudStorage.MaxFiles && files.All(f => f.SizeBytes <= CloudStorage.MaxFileBytes), "Upload exceeds file limits.");
    private static byte[] Encode<T>(T record) => JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
    private static bool IsId(string? id) => id is { Length: 40 } && id.All(char.IsAsciiHexDigitLower);
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message) { if (!condition) throw new RecoveryBlockedException(message); }
}
