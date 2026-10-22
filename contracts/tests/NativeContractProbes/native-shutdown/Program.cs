using System;
using System.Threading.Tasks;
using HomeBridge.BridgeTools;

internal static class NativeShutdownProbe
{
    internal static void Invoke()
    {
        // The stop cannot finish until its final game-thread request runs.
        // A blocking Wait on the main thread would time out here.
        var finalHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var disposed = false;
        var stop = Task.Run(async () => { await finalHold.Task; stopped = true; });
        ShutdownDrain.Run(stop, () => finalHold.TrySetResult(), () =>
        {
            if (!stopped) throw new Exception("Game disposed before the brain stopped");
            disposed = true;
        }, TimeSpan.FromSeconds(5));
        if (!disposed) throw new Exception("Game was not disposed");

        // No brain (or an already-stopped brain) still releases the loaded game.
        var disposals = 0;
        ShutdownDrain.Run(Task.CompletedTask, () => throw new Exception("Unneeded pump"),
            () => disposals++, TimeSpan.FromSeconds(5));
        if (disposals != 1) throw new Exception("Expected one disposal");

        // Neither a failed nor a hung stop may dispose resources under live work.
        var failure = new InvalidOperationException("stop failed");
        try
        {
            ShutdownDrain.Run(Task.FromException(failure), () => { }, () => disposals++, TimeSpan.FromSeconds(5));
            throw new Exception("Stop failure was hidden");
        }
        catch (InvalidOperationException e) when (ReferenceEquals(e, failure)) { }
        try
        {
            ShutdownDrain.Run(new TaskCompletionSource().Task, () => { }, () => disposals++, TimeSpan.Zero);
            throw new Exception("Hung stop was not bounded");
        }
        catch (TimeoutException) { }
        if (disposals != 1) throw new Exception("Disposed resources under unfinished work");
        Console.WriteLine("native-shutdown: final game-thread request drains before disposal; no-brain, failure and timeout passed");
    }
}
