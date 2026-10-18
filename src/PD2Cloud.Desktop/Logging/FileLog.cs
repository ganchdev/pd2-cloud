using System.Text;
using System.Text.RegularExpressions;

namespace PD2Cloud.Desktop.Logging;

internal enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Append-only, bounded diagnostic log. Never records credential values or file contents; callers pass
/// identifiers, paths, counts and already-sanitized messages.
/// </summary>
internal sealed class FileLog : IDisposable
{
    internal const long DefaultMaxBytes = 2 * 1024 * 1024;
    internal const int DefaultKeep = 5;
    private static readonly Regex SecretPair = new(
        @"(?i)\b(access[_-]?token|refresh[_-]?token|authorization|password|secret)\b(\s*[:=]\s*)([""']?)[^\s""',;]+",
        RegexOptions.Compiled);
    private static readonly Regex JsonSecret = new(
        @"(?i)(""?(?:access_token|refresh_token|token|secret|password)""?\s*:\s*"")[^""]+",
        RegexOptions.Compiled);
    private static readonly Regex BearerToken = new(
        @"(?i)\b(Bearer)\s+[A-Za-z0-9._~+/-]+=*",
        RegexOptions.Compiled);
    private static readonly Regex GitHubToken = new(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b",
        RegexOptions.Compiled);

    private readonly object gate = new();
    private readonly string directory;
    private readonly long maxBytes;
    private readonly int keep;
    private StreamWriter? writer;

    internal FileLog(string directory, long maxBytes = DefaultMaxBytes, int keep = DefaultKeep)
    {
        if (maxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (keep < 0) throw new ArgumentOutOfRangeException(nameof(keep));
        this.directory = Path.GetFullPath(directory);
        this.maxBytes = maxBytes;
        this.keep = keep;
        Directory.CreateDirectory(this.directory);
        LogPath = Path.Combine(this.directory, "pd2cloud.log");
        Open();
    }

    internal string LogPath { get; }

    internal void Write(LogLevel level, string category, string message, Exception? error = null)
    {
        lock (gate)
        {
            if (writer is null) return;
            try
            {
                if (writer.BaseStream.Length >= maxBytes) Rotate();
                writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level.ToString().ToUpperInvariant()}] [T{Environment.CurrentManagedThreadId}] {Sanitize(category)}: {Sanitize(message)}");
                if (error is not null) writer.WriteLine(Sanitize(error.ToString()));
            }
            catch (Exception)
            {
                // Diagnostics must never turn a working operation into a failed one.
                Close();
            }
        }
    }

    internal static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = SecretPair.Replace(text, "$1$2$3<redacted>");
        text = JsonSecret.Replace(text, "$1<redacted>");
        text = BearerToken.Replace(text, "$1 <redacted>");
        return GitHubToken.Replace(text, "<redacted-github-token>");
    }

    private void Rotate()
    {
        Close();
        if (keep > 0)
        {
            for (var index = keep - 1; index >= 1; index--)
            {
                var source = Archive(index);
                if (File.Exists(source)) File.Move(source, Archive(index + 1), overwrite: true);
            }
            if (File.Exists(LogPath)) File.Move(LogPath, Archive(1), overwrite: true);
        }
        else if (File.Exists(LogPath)) File.Delete(LogPath);
        Open();
    }

    private string Archive(int index) => Path.Combine(directory, $"pd2cloud.{index}.log");

    private void Open()
    {
        var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    private void Close()
    {
        try { writer?.Dispose(); }
        catch (Exception) { }
        finally { writer = null; }
    }

    public void Dispose()
    {
        lock (gate) Close();
    }
}
