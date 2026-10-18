using System.Runtime.InteropServices;
using PD2Cloud.Local;

namespace PD2Cloud.Desktop.Logging;

/// <summary>
/// Process-wide diagnostic logger. Initialized by the desktop entry point; a no-op in tests and tools
/// until initialized. Logs are local files under application data and are never uploaded.
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static FileLog? log;
    private static string session = "";

    internal static string? LogPath
    {
        get { lock (Gate) return log?.LogPath; }
    }

    internal static bool IsEnabled
    {
        get { lock (Gate) return log is not null; }
    }

    internal static void Initialize(string? directory = null)
    {
        lock (Gate)
        {
            if (log is not null) return;
            try
            {
                var target = directory ?? Path.Combine(ApplicationDataPaths.DirectoryPath, "logs");
                log = new FileLog(target);
                session = Guid.NewGuid().ToString("N")[..8];
                var version = typeof(AppLog).Assembly.GetName().Version?.ToString() ?? "unknown";
                log.Write(LogLevel.Info, "App",
                    $"[{session}] pid={Environment.ProcessId} PD2 Cloud {version} started; os={RuntimeInformation.OSDescription}; runtime={RuntimeInformation.FrameworkDescription}; data={ApplicationDataPaths.DirectoryPath}; log={log.LogPath}");
            }
            catch (Exception error)
            {
                log = null;
                Console.Error.WriteLine("PD2 Cloud logging is unavailable: " + error.Message);
            }
        }
    }

    internal static void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);
    internal static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);
    internal static void Warn(string category, string message) => Write(LogLevel.Warn, category, message, null);
    internal static void Warn(string category, string message, Exception error) => Write(LogLevel.Warn, category, message, error);
    internal static void Error(string category, string message, Exception? error = null) => Write(LogLevel.Error, category, message, error);

    private static void Write(LogLevel level, string category, string message, Exception? error)
    {
        FileLog? current;
        string currentSession;
        lock (Gate)
        {
            current = log;
            currentSession = session;
        }
        current?.Write(level, category, $"[{currentSession}] {message}", error);
    }

    internal static void Shutdown()
    {
        lock (Gate)
        {
            if (log is null) return;
            try { log.Write(LogLevel.Info, "App", $"[{session}] PD2 Cloud exited"); }
            finally
            {
                log.Dispose();
                log = null;
            }
        }
    }
}
