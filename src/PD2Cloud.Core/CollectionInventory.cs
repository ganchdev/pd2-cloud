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

}
