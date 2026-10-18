using PD2Cloud.Desktop;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class SingleInstanceGuardTests : IDisposable
{
    private readonly string directory = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "pd2-instance-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

    [Fact]
    public void ASecondAcquisitionWhileTheFirstIsHeldReportsAlreadyRunning()
    {
        Assert.Equal(SingleInstanceState.Acquired, SingleInstanceGuard.TryAcquire(directory, out var first));
        using (first)
        {
            Assert.Equal(SingleInstanceState.AlreadyRunning, SingleInstanceGuard.TryAcquire(directory, out var second));
            Assert.Null(second);
        }
    }

    [Fact]
    public void ReleasingTheFirstInstanceDoesNotBlockTheNextLaunch()
    {
        Assert.Equal(SingleInstanceState.Acquired, SingleInstanceGuard.TryAcquire(directory, out var first));
        first!.Dispose();

        Assert.Equal(SingleInstanceState.Acquired, SingleInstanceGuard.TryAcquire(directory, out var second));
        second!.Dispose();
    }
}
