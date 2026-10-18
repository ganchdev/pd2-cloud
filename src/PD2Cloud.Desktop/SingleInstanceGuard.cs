namespace PD2Cloud.Desktop;

internal enum SingleInstanceState { Acquired, AlreadyRunning, Unavailable }

/// <summary>
/// Holds an application-lifetime lease so a second desktop launch cannot race recovery storage.
/// The OS releases the lease when the process exits, so a crashed instance never needs manual cleanup.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream? handle;

    private SingleInstanceGuard(FileStream? handle) => this.handle = handle;

    public static SingleInstanceState TryAcquire(string directory, out SingleInstanceGuard? guard)
    {
        guard = null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "instance.lock");
            guard = new SingleInstanceGuard(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            return SingleInstanceState.Acquired;
        }
        catch (IOException error) when (IsHeldByAnotherProcess(error))
        {
            return SingleInstanceState.AlreadyRunning;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The guard is best effort: if it cannot be stored, let the app start and report
            // the underlying storage problem when it is actually used.
            return SingleInstanceState.Unavailable;
        }
    }

    public void Dispose() => handle?.Dispose();

    private static bool IsHeldByAnotherProcess(IOException error) => OperatingSystem.IsWindows()
        ? (error.HResult & 0xFFFF) is 32 or 33 // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
        : error.HResult is 11 or 35; // EAGAIN / EWOULDBLOCK
}
