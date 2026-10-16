using PD2Cloud.Local;

namespace PD2Cloud.RecoveryProbe;

/// <summary>Test-only subprocess: abrupt exit skips disposal; hold mode tests OS lease release on kill.</summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length != 4) Environment.Exit(2);
        var store = new OperationPreparationStore(args[0], new Observer(args[2], args[3]));
        await store.PrepareAsync(args[1]);
        Environment.Exit(3); // A requested boundary must actually have been reached.
    }

    private sealed class Observer(string mode, string stop) : IPreparationObserver
    {
        public void Reached(string boundary, string operationId, string? path)
        {
            if (boundary != stop) return;
            Console.WriteLine("boundary reached");
            Console.Out.Flush();
            if (mode == "exit") Environment.Exit(73);
            if (mode == "hold") Thread.Sleep(Timeout.Infinite);
            Environment.Exit(4);
        }
    }
}
