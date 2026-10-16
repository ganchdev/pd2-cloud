using System.Runtime.InteropServices;

namespace PD2Cloud.Local;

/// <summary>Fail-closed physical paths and flushed, append-only recovery records.</summary>
internal static class RecoveryStorage
{
    internal static void CheckPath(string path)
    {
        for (FileSystemInfo? item = new DirectoryInfo(path); item is not null; item = (item as DirectoryInfo)?.Parent)
            if (item.LinkTarget is not null || (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Linked recovery paths are unsupported.");
    }

    internal static void CreateDirectory(string path)
    {
        CheckPath(path);
        if (Directory.Exists(path)) return;
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("Invalid storage path.");
        CreateDirectory(parent);
        Directory.CreateDirectory(path);
        CheckPath(path);
        FlushDirectory(path);
        FlushDirectory(parent);
    }

    internal static void WriteNew(string path, byte[] bytes, Action<string>? boundary = null)
    {
        CheckPath(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        // Orphans are deliberately retained. Recovery never interprets temporary files as committed records.
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        boundary?.Invoke("JournalTemporaryFlushed");
        Publish(temporary, path, boundary);
    }

    internal static void Publish(string temporary, string path, Action<string>? boundary = null)
    {
        CheckPath(Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(temporary, path, 0x8)) // MOVEFILE_WRITE_THROUGH; never replace an existing record.
                throw new IOException("Cannot publish a durable recovery file.");
        }
        else File.Move(temporary, path);
        boundary?.Invoke("Renamed");
        FlushDirectory(Path.GetDirectoryName(path)!);
        boundary?.Invoke("DirectoryFlushed");
    }

    internal static void FlushDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return; // Publication uses write-through rename on Windows.
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Recovery durability is unavailable on this platform.");
        var fd = Open(path, 0);
        if (fd < 0) throw new IOException("Cannot open recovery directory for flushing.");
        try
        {
            if (FSync(fd) != 0) throw new IOException("Cannot flush recovery directory.");
        }
        finally { Close(fd); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int FSync(int fd);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string destination, uint flags);
}
