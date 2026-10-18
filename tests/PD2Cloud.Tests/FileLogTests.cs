using PD2Cloud.Desktop.Logging;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly string root = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pd2-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void WritesFlushedSanitizedLines()
    {
        using (var log = new FileLog(root))
        {
            log.Write(LogLevel.Info, "Test", "access_token=secret-value ghu_abcdefghijklmnopqrstuvwxyz");
            log.Write(LogLevel.Error, "Test", "failure", new InvalidOperationException("Bearer abc.def"));
        }

        var text = File.ReadAllText(Path.Combine(root, "pd2cloud.log"));
        Assert.Contains("[INFO]", text);
        Assert.Contains("[ERROR]", text);
        Assert.Contains("Test:", text);
        Assert.Contains("<redacted>", text);
        Assert.DoesNotContain("secret-value", text);
        Assert.DoesNotContain("ghu_abcdefghijklmnopqrstuvwxyz", text);
        Assert.DoesNotContain("abc.def", text);
        Assert.Contains("InvalidOperationException", text);
    }

    [Fact]
    public void RotatesAtTheBoundAndKeepsTheConfiguredNumberOfArchives()
    {
        using (var log = new FileLog(root, maxBytes: 1024, keep: 2))
        {
            for (var index = 0; index < 30; index++) log.Write(LogLevel.Info, "Rotation", new string('x', 100));
        }

        Assert.True(File.Exists(Path.Combine(root, "pd2cloud.log")));
        Assert.True(File.Exists(Path.Combine(root, "pd2cloud.1.log")));
        Assert.True(File.Exists(Path.Combine(root, "pd2cloud.2.log")));
        Assert.False(File.Exists(Path.Combine(root, "pd2cloud.3.log")));
    }

    [Fact]
    public void WriteAfterDisposeIsANoOp()
    {
        var log = new FileLog(root);
        log.Dispose();
        log.Write(LogLevel.Info, "Test", "after dispose");
    }
}
