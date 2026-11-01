using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Native;

// RG-HABITAT-009: the raid arrival tracks the defense probe reads survive a day without their lord and a save and load.
internal static class NativeRaidTrackLogProbe
{
    internal static void Invoke()
    {
        var log = new RaidTrackLog();
        Require(log.Count == 0 && log.Export().Ints.Count == 0, "a fresh log holds no track");

        // A ground raid walks in from the west edge; a drop-pod lord lands at the colony.
        log.Sample(7, "Pirate", 600, (2, 40), true, (2, 40), 0f);
        log.Sample(7, "Pirate", 660, (2, 40), true, (6, 40), 24.9f);
        log.Sample(7, "Pirate", 720, (2, 40), true, (6, 40), 12f);
        log.Sample(9, "Pirate", 700, (50, 50), false, (50, 50), 0f);
        var raid = log.Tracks.First();
        Require(raid.LordId == 7 && raid.Ground && raid.SpawnX == 2 && raid.SpawnZ == 40, "the first sample is the spawn");
        Require(raid.Trail.Count == 2 && raid.Trail[1] == (6, 40) && raid.Range == 24.9f && raid.LastTick == 720, "a trail cell per move and the longest range");

        // A day after both lords were last seen, the ground raid stays and the drop-pod lord is evicted.
        log.Evict(720 + RaidTrackLog.FinishedAge + 1);
        Require(log.Count == 1 && log.Tracks.Single().LordId == 7, "a finished ground track stays, another finished one goes");

        // The save round-trips every field.
        var (ints, factions, ranges) = log.Export();
        var loaded = new RaidTrackLog();
        loaded.Import(ints, factions, ranges);
        var back = loaded.Tracks.Single();
        Require(back.LordId == 7 && back.FactionDef == "Pirate" && back.SpawnTick == 600 && (back.SpawnX, back.SpawnZ) == (2, 40) && back.Ground
            && back.LastTick == 720 && back.Range == 24.9f && back.Trail.SequenceEqual(raid.Trail), "a loaded track keeps its spawn, trail, range and ticks");
        loaded.Sample(7, "Pirate", 900000, (2, 40), true, (8, 41), 0f);
        Require(loaded.Tracks.Single().Trail.Count == 3, "a loaded track samples on");

        // Every finished ground track stays (no authored count), and a trail holds each cell once however long the lord lives.
        var many = new RaidTrackLog();
        for (var lord = 0; lord < 11; lord++) many.Sample(lord, "Tribe", 1000 * lord, (0, lord), true, (1, lord), 0f);
        many.Evict(11000 + RaidTrackLog.FinishedAge + 1);
        Require(many.Count == 11 && many.Tracks.First().LordId == 0, "every finished ground raid stays");
        var pacing = new RaidTrackLog();
        for (var tick = 0; tick < 6000; tick += 60) pacing.Sample(1, "Tribe", tick, (0, 0), true, (tick / 60 % 3, 0), 0f);
        Require(pacing.Tracks.Single().Trail.Count == 3, "a trail holds each cell once");

        // A save from before the log, or a malformed one, loads no track.
        loaded.Import(null, null, null);
        Require(loaded.Count == 0, "a save without the nodes loads none");
        loaded.Import(new List<int> { 7, 600, 2, 40, 1, 720, 5, 1, 1 }, new List<string> { "Pirate" }, new List<float> { 0f });
        Require(loaded.Count == 0, "a truncated trail loads none");
        loaded.Import(ints, factions, new List<float>());
        Require(loaded.Count == 0, "mismatched lists load none");
        Console.WriteLine("native-raid-track-log: sampling, ground retention, unbounded ground retention, save round trip and malformed loads passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
