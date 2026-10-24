using System;
using HomeBridge.BridgeTools;

internal static class NativeSightingLogProbe
{
    internal static void Invoke()
    {
        var log = new SightingLog();
        Require(log.Covered(1, 1000) == 0 && log.Passes(1, 1000).Count == 0, "a pawn with no pass has no record");

        // A pass every 600 ticks for three days: the ring keeps the window's passes only, oldest first.
        for (int tick = 1000; tick <= 1000 + 3 * 60000; tick += 600) log.Record(1, tick, tick / 600);
        int now = 1000 + 3 * 60000;
        var passes = log.Passes(1, now);
        Require(passes.Count == SightingLog.WindowTicks / 600, "the window's passes are kept");
        Require(passes[0].Tick == now - SightingLog.WindowTicks + 600 && passes[passes.Count - 1].Tick == now, "oldest first, the newest last");
        Require(passes[passes.Count - 1].Cell == now / 600, "each pass keeps its cell");
        Require(log.Covered(1, now) == SightingLog.WindowTicks, "a long record covers the window");

        // At the fastest cadence a full window fits the ring.
        var fast = new SightingLog();
        for (int tick = 0; tick <= SightingLog.WindowTicks; tick += SightingLog.FastestPassTicks) fast.Record(2, tick, 7);
        Require(fast.Passes(2, SightingLog.WindowTicks).Count == SightingLog.Capacity - 1, "the fastest cadence's window fits the ring");
        Require(fast.Covered(2, SightingLog.WindowTicks / 2) == SightingLog.WindowTicks / 2, "a young record covers since its first pass");

        log.Retain(pawn => pawn == 2);
        Require(log.Pawns == 0 && log.Covered(1, now) == 0, "retain drops a refused pawn's ring");
        Console.WriteLine("native-sighting-log: window, ring order, cadence bound, coverage and retain passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
