using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PD2Cloud.Local;

public sealed record FileFingerprint(long SizeBytes, string Sha256);

public interface IFileFingerprintReader
{
    FileFingerprint Read(string path, CancellationToken token);
}

/// <summary>Read-only handles; Unix nonblocking/no-follow open avoids hanging on pipes.</summary>
public sealed class FileFingerprintReader : IFileFingerprintReader
{
    public FileFingerprint Read(string path, CancellationToken token)
        => ReadOrCopy(path, null, token);

    internal FileFingerprint Copy(string path, Stream destination, CancellationToken token, Action? copiedChunk = null)
        => ReadOrCopy(path, destination, token, copiedChunk);

    internal static byte[] ReadBounded(string path, int limit, CancellationToken token)
    {
        using var handle = OpenRegularFile(path);
        var length = RandomAccess.GetLength(handle);
        if (length > limit) throw new IOException("Recovery record exceeds the size limit.");
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = RandomAccess.Read(handle, bytes.AsSpan(offset), offset);
            if (count == 0) throw new IOException("Recovery record changed while reading.");
            offset += count;
        }
        if (RandomAccess.GetLength(handle) != length) throw new IOException("Recovery record changed while reading.");
        return bytes;
    }

    private static FileFingerprint ReadOrCopy(string path, Stream? destination, CancellationToken token, Action? copiedChunk = null)
    {
        token.ThrowIfCancellationRequested();
        using var handle = OpenRegularFile(path);
        var length = RandomAccess.GetLength(handle);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            token.ThrowIfCancellationRequested();
            var count = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
            if (count == 0) throw new IOException("File shrank during hashing.");
            hash.AppendData(buffer, 0, count);
            destination?.Write(buffer, 0, count);
            copiedChunk?.Invoke();
            offset += count;
        }
        token.ThrowIfCancellationRequested();
        if (RandomAccess.GetLength(handle) != length) throw new IOException("File changed during hashing.");
        return new(length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static SafeFileHandle OpenRegularFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Directory)) != 0)
                throw new IOException("Not a regular source file.");
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        }
        var mac = OperatingSystem.IsMacOS();
        if (!mac && (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)))
            throw new IOException("Regular-file verification is unavailable on this platform.");
        var fd = Open(path, mac ? 0x4 | 0x100 | 0x1000000 : 0x800 | 0x20000 | 0x80000); // O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC
        if (fd < 0) throw new IOException("Cannot open a regular source file.");
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            // Oversized native stat storage. st_mode offsets follow Darwin and Linux x64/arm64 ABIs.
            var stat = new byte[512];
            if (FStatForPlatform(fd, stat) != 0) throw new IOException("Cannot inspect source handle.");
            var mode = mac ? BitConverter.ToUInt16(stat, 4) :
                BitConverter.ToUInt32(stat, RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 24 : 16);
            if ((mode & 0xf000) != 0x8000) throw new IOException("Source is not a regular file.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fd, [Out] byte[] stat);

    // On Darwin x86_64, plain "fstat" is the legacy 32-bit-inode ABI where st_mode is at byte 8, so
    // the st_mode read at byte 4 sees inode bytes and rejects every regular file. The $INODE64 entry
    // point selects the 64-bit-inode layout (st_mode at byte 4) that arm64 and Linux already use; it
    // is only exported on x86_64, where the plain symbol is legacy.
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int FStatInode64(int fd, [Out] byte[] stat);

    private static int FStatForPlatform(int fd, byte[] stat) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? FStatInode64(fd, stat)
            : FStat(fd, stat);
}
