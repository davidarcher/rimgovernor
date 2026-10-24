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

        // The save round-trips the log; an old save (no node) and a malformed list load empty.
        var loaded = new SightingLog();
        loaded.Import(log.Export());
        Require(loaded.Pawns == 1 && loaded.Covered(1, now) == log.Covered(1, now), "a loaded log keeps each pawn's coverage");
        var before = log.Passes(1, now);
        var after = loaded.Passes(1, now);
        Require(after.Count == before.Count && after[0] == before[0] && after[after.Count - 1] == before[before.Count - 1], "a loaded log keeps the passes in order");
        loaded.Record(1, now + 600, 9);
        var recorded = loaded.Passes(1, now + 600);
        Require(recorded[recorded.Count - 1] == (now + 600, 9), "a loaded ring records on");
        loaded.Import(null);
        Require(loaded.Pawns == 0, "a save without the log loads empty");
        loaded.Import(new System.Collections.Generic.List<int> { 1, 0, 5, 3 });
        Require(loaded.Pawns == 0, "a malformed list loads empty");

        log.Retain(pawn => pawn == 2);
        Require(log.Pawns == 0 && log.Covered(1, now) == 0, "retain drops a refused pawn's ring");
        Console.WriteLine("native-sighting-log: window, ring order, cadence bound, coverage, save round trip and retain passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
