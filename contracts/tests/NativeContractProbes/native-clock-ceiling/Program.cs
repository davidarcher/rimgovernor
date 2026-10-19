using System;
using HomeBridge.BridgeTools;

// The one ceiling (ClockCeiling.cs, #3171) against a fake tick source: wall time advances in fixed
// frames, each frame ticks at min(requested, ceiling) and its tick work costs a fixed time per tick.
internal static class NativeClockCeilingProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    private const int FrameWallMs = 16;
    private const int Requested = HyperfastCap.TicksPerSecond; // Hyperfast, the fastest rate an epoch asks for

    private sealed class Rig
    {
        public long Now = 1_000_000;
        public readonly ClockCeiling Ceiling;
        public readonly double MsPerTick;
        public int OverBudget, Frames;
        public Rig(double msPerTick) { MsPerTick = msPerTick; Ceiling = new ClockCeiling(Now); }
        public int Rate => ClockCeiling.Apply(Ceiling.TicksPerSecond, Requested);
        public double Step()
        {
            var rate = Rate;
            var ticks = (int)Math.Round(rate * FrameWallMs / 1000.0);
            var frameMs = ticks * MsPerTick;
            Now += FrameWallMs;
            Ceiling.Frame(Now, frameMs, ticks, rate);
            Frames++; if (frameMs > ClockCeiling.DefaultFrameBudgetMs) OverBudget++;
            return frameMs;
        }
        public void Run(long ms, long beatEveryMs = 0)
        {
            var end = Now + ms; var nextBeat = Now;
            while (Now < end)
            {
                if (beatEveryMs > 0 && Now >= nextBeat) { Ceiling.Beat(Now); nextBeat = Now + beatEveryMs; }
                Step();
            }
        }
    }

    internal static void Invoke()
    {
        // Heartbeat: beating holds no ceiling; silence past the tolerance drops to Normal within one
        // frame of it; beats ramp back up and release.
        var rig = new Rig(0.001);
        rig.Run(10_000, beatEveryMs: 1000);
        Check(rig.Ceiling.TicksPerSecond == 0 && rig.Rate == Requested, "a beating brain is never clamped");
        rig.Ceiling.Beat(rig.Now); var lastBeat = rig.Now;
        while (rig.Now - lastBeat < ClockCeiling.HeartbeatToleranceMs) { rig.Step(); if (rig.Now - lastBeat <= ClockCeiling.HeartbeatToleranceMs) Check(rig.Ceiling.TicksPerSecond == 0, "no clamp inside the tolerance"); }
        if (rig.Ceiling.TicksPerSecond == 0) rig.Step();
        Check(rig.Ceiling.TicksPerSecond == ClockCeiling.NormalTicksPerSecond && rig.Rate == 60, "silence past the tolerance drops to Normal");
        Check(rig.Now - lastBeat <= ClockCeiling.HeartbeatToleranceMs + 2 * FrameWallMs, "the drop lands within a frame of the tolerance");
        Check(rig.Ceiling.Binding == ClockCeiling.Input.Heartbeat, "status names the heartbeat as the binding input");
        rig.Run(3000);
        Check(rig.Rate == 60, "the ceiling stays at Normal while the brain is silent");
        rig.Ceiling.Beat(rig.Now);
        var resumed = rig.Now; var previous = rig.Rate;
        while (rig.Ceiling.TicksPerSecond != 0 && rig.Now - resumed < 5000)
        {
            rig.Ceiling.Beat(rig.Now); rig.Step();
            Check(rig.Rate >= previous, "recovery never steps down"); previous = rig.Rate;
        }
        Check(rig.Ceiling.TicksPerSecond == 0 && rig.Now - resumed <= 1500, "a resumed heartbeat ramps back to the requested rate within 1.5 s");

        // Frame budget: a frame over budget lowers the ceiling at once, recovery is gradual, and the
        // rate settles without oscillating across the budget.
        var heavy = new Rig(2); // 30x asks for 29 ticks per frame, 58 ms of tick work
        heavy.Ceiling.Beat(heavy.Now);
        var first = heavy.Step();
        Check(first > ClockCeiling.DefaultFrameBudgetMs, "the unclamped frame is over budget");
        Check(heavy.Ceiling.TicksPerSecond > 0 && heavy.Ceiling.TicksPerSecond < Requested && heavy.Ceiling.Binding == ClockCeiling.Input.FrameBudget,
            "one over-budget frame lowers the ceiling at once, bound by the frame budget");
        var after = heavy.Ceiling.TicksPerSecond;
        Check(after == (int)(Requested * ClockCeiling.FrameLower), "the drop is the lowering factor of the rate played");
        long end = heavy.Now + 30_000; var raises = 0; var drops = 0; var last = after; var settledAt = 0L;
        while (heavy.Now < end)
        {
            heavy.Ceiling.Beat(heavy.Now);
            heavy.Step();
            var c = heavy.Ceiling.TicksPerSecond;
            if (c > last) raises++;
            if (c != 0 && c < last) { drops++; settledAt = heavy.Now; }
            if (c == 0) throw new Exception("a ceiling above the frame cost was released");
            last = c;
        }
        Check(drops <= 6, "the descent to the budget is a few drops, not an oscillation: " + drops);
        Check(heavy.Now - settledAt > 15_000, "the last drop is well before the end: the rate has settled");
        var settledMs = Math.Round(heavy.Ceiling.TicksPerSecond * FrameWallMs / 1000.0) * heavy.MsPerTick;
        Check(settledMs <= ClockCeiling.DefaultFrameBudgetMs && settledMs > ClockCeiling.DefaultFrameBudgetMs * 0.5, "it settles inside the budget, not far under it: " + settledMs + " ms");
        Check(heavy.OverBudget <= drops + 1, "only the frames that caused a drop ran over budget: " + heavy.OverBudget + " over, " + drops + " drops");

        // A cheap colony recovers to unlimited after a one-off spike, one step per ramp interval.
        var spike = new Rig(0.001);
        spike.Ceiling.Beat(spike.Now);
        spike.Ceiling.Frame(spike.Now, 80, 100, 600);
        Check(spike.Ceiling.TicksPerSecond > 0, "the spike lowers the ceiling");
        var start = spike.Now; var firstRaise = 0L; var steps = 0; var prev = spike.Ceiling.TicksPerSecond;
        while (spike.Ceiling.TicksPerSecond != 0 && spike.Now - start < 30_000)
        {
            spike.Ceiling.Beat(spike.Now); spike.Step();
            if (spike.Ceiling.TicksPerSecond != prev) { if (firstRaise == 0) firstRaise = spike.Now - start; steps++; prev = spike.Ceiling.TicksPerSecond; }
        }
        Check(firstRaise >= ClockCeiling.FrameHoldMs, "no raise before the hold after a drop");
        Check(spike.Ceiling.TicksPerSecond == 0 && steps > 3, "a cheap colony recovers to no ceiling in several gradual steps");

        // The lower input binds.
        var both = new ClockCeiling(0);
        both.Frame(1, 80, 100, 600);           // frame ceiling 420
        both.Frame(ClockCeiling.HeartbeatToleranceMs + 100, 1, 1, 60); // heartbeat 60
        Check(both.TicksPerSecond == 60 && both.Binding == ClockCeiling.Input.Heartbeat, "the lower of the two inputs is the ceiling");

        Console.WriteLine("Clock ceiling passed " + checks + " assertions; fake tick source, defaults unmeasured (#3199).");
    }
}
