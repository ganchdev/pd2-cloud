using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PD2Cloud.Core;

public static class CollectionInventory
{
    /// <summary>SHA-256 over domain prefix then length-prefixed UTF-8 NFC paths and raw content hashes, in ordinal order.</summary>
    public static string Fingerprint(IReadOnlyList<FileEntry> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("PD2Cloud.Collection.v1\0"));
        Span<byte> length = stackalloc byte[4];
        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(file.RelativePath);
            BinaryPrimitives.WriteInt32BigEndian(length, name.Length);
            hash.AppendData(length);
            hash.AppendData(name);
            hash.AppendData(Convert.FromHexString(file.Sha256));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool IsValidManifest(CollectionManifest? manifest) => manifest is { SchemaVersion: 1, IsStable: true, Issues.Count: 0 }
        && IsValidFiles(manifest.Files) && manifest.CollectionFingerprint == CollectionInventory.Fingerprint(manifest.Files);

    public static bool IsValidFiles(IReadOnlyList<FileEntry>? files)
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
