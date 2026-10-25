using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RimGovernor.Native;

// Quit callbacks run on the game thread. Final brain holds still need that thread;
// waiting without draining its existing queue deadlocks cleanup until a timeout.
internal static class ShutdownDrain
{
    internal static void Run(Task stop, Action pump, Action disposeGame, TimeSpan budget)
    {
        var started = Stopwatch.GetTimestamp();
        while (!stop.IsCompleted)
        {
            if (Stopwatch.GetElapsedTime(started) >= budget)
                throw new TimeoutException("The brain did not stop before game shutdown; game disposal was not safe.");
            pump();
            Thread.Sleep(1);
        }
        stop.GetAwaiter().GetResult();
        disposeGame();
    }
}
