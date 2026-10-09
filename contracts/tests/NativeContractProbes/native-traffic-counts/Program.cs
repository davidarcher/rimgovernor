using System;
using HomeBridge.BridgeTools;

internal static class NativeTrafficCountsProbe
{
    internal static void Invoke()
    {
        // Crossing: soil onto floor counts; floor onto floor, soil onto soil
        // and floor onto soil do not.
        Require(TrafficCounts.IsCrossing(true, true), "soil to floor is a crossing");
        Require(!TrafficCounts.IsCrossing(false, true), "floor to floor is no crossing");
        Require(!TrafficCounts.IsCrossing(true, false), "soil to soil is no crossing");
        Require(!TrafficCounts.IsCrossing(false, false), "floor to soil is no crossing");

        var counts = new TrafficCounts(100);
        for (int i = 0; i < 8; i++) counts.Step(TrafficCounts.Colonist, i == 0, 5);
        counts.Step(-1, true, 6);
        counts.Step(TrafficCounts.Hostile, false, 91);
        counts.Step(TrafficCounts.Visitor, false, 90);
        Require(counts[TrafficCounts.Colonist, 5] == 8 && counts[TrafficCounts.Crossing, 5] == 1, "colonist steps and one crossing counted");
        Require(counts[TrafficCounts.Crossing, 6] == 1 && counts[TrafficCounts.Colonist, 6] == 0, "an unlayered pawn still counts its crossing");
        counts.Step(TrafficCounts.Colonist, false, 100);
        Require(counts.Total(TrafficCounts.Colonist) == 8, "out-of-bounds step ignored");

        for (int i = 0; i < 70000; i++) counts.Step(TrafficCounts.Animal, false, 9);
        Require(counts[TrafficCounts.Animal, 9] == ushort.MaxValue, "counts saturate");

        // Per-layer decay: one colonist half-life halves colonist, crossing
        // and animal layers once, in slices from the first cell; the slower
        // layers have swept only part of the map (cells 90 and 91 unreached).
        for (int t = 0; t < TrafficCounts.HalfLife[TrafficCounts.Colonist]; t += 250) counts.Decay(250);
        Require(counts[TrafficCounts.Colonist, 5] == 4, "colonist halved once per half-life");
        Require(counts[TrafficCounts.Crossing, 5] == 0, "crossing halved once per half-life");
        Require(counts[TrafficCounts.Animal, 9] == ushort.MaxValue / 2, "animal halved once per half-life");
        Require(counts[TrafficCounts.Visitor, 90] == 1 && counts[TrafficCounts.Hostile, 91] == 1, "slow layers not yet halved");
        for (int t = 0; t < TrafficCounts.HalfLife[TrafficCounts.Hostile]; t += 250) counts.Decay(250);
        Require(counts[TrafficCounts.Hostile, 91] == 0 && counts[TrafficCounts.Visitor, 90] == 0, "slow layers halve on their own half-life");

        var top = new TrafficCounts(10);
        foreach (var (index, n) in new[] { (3, 2), (1, 5), (7, 2), (2, 9) })
            for (int i = 0; i < n; i++) top.Step(TrafficCounts.Colonist, false, index);
        var busiest = top.Nonzero(TrafficCounts.Colonist);
        Require(busiest.Count == 4 && busiest[0] == 2 && busiest[1] == 1 && busiest[2] == 3 && busiest[3] == 7, "every nonzero cell, busiest first, ties by index");
        Console.WriteLine("native-traffic-counts: crossing detection, counting, saturation, per-layer decay and nonzero cells passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
