#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace RimGovernor.Native
{
    // The hostile lords RaidArrivalState has sampled (RG-HABITAT-009), free of
    // game types so the contract probes can check it. A lord seen within
    // FinishedAge keeps its whole trail; one unseen longer is finished, and the
    // newest KeptGroundRaids finished ground tracks stay so the defense probe
    // keeps the approaches it was priced over across days and loads. Every other
    // finished track is evicted. Saved with the map as Export's three flat lists;
    // a save from before it, or a malformed list, loads no tracks.
    //
    // Bound: the live lords' tracks plus KeptGroundRaids finished ones; a trail
    // grows one cell a sample only while its lord's nearest pawn moves.
    public sealed class RaidTrackLog
    {
        public const int FinishedAge = 60000;

        // An authored bound (kept-constants.md), not a game rule: enough walks
        // for the probe's approaches without growing the save each raid.
        public const int KeptGroundRaids = 8;

        public sealed class Track
        {
            public int LordId;
            public string FactionDef = "";
            public int SpawnTick;
            public int SpawnX, SpawnZ;
            public bool Ground;
            public int LastTick;
            // The longest primary-verb range any sampled pawn carried (verbProps.range); 0 for melee only.
            public float Range;
            public readonly List<(int X, int Z)> Trail = new List<(int X, int Z)>();
        }

        private readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();

        // The tracks oldest first (spawn tick, then lord).
        public IEnumerable<Track> Tracks => tracks.Values.OrderBy(t => t.SpawnTick).ThenBy(t => t.LordId);

        public int Count => tracks.Count;

        // One sample of a live hostile lord: the first one records its spawn
        // (ground when it lies on the map edge); each adds the nearest pawn's
        // cell when it moved and keeps the longest range.
        public void Sample(int lordId, string factionDef, int tick, (int X, int Z) first, bool ground, (int X, int Z) nearest, float range)
        {
            if (!tracks.TryGetValue(lordId, out var track))
            {
                track = new Track { LordId = lordId, FactionDef = factionDef, SpawnTick = tick, SpawnX = first.X, SpawnZ = first.Z, Ground = ground };
                tracks[lordId] = track;
            }
            track.LastTick = tick;
            track.Range = Math.Max(track.Range, range);
            if (track.Trail.Count == 0 || track.Trail[track.Trail.Count - 1] != nearest) track.Trail.Add(nearest);
        }

        // Evicts every finished track but the newest KeptGroundRaids ground ones.
        public void Evict(int tick)
        {
            var finished = tracks.Values.Where(t => tick - t.LastTick > FinishedAge).ToList();
            var kept = new HashSet<int>(finished.Where(t => t.Ground).OrderByDescending(t => t.LastTick).ThenByDescending(t => t.LordId)
                .Take(KeptGroundRaids).Select(t => t.LordId));
            foreach (var t in finished)
            {
                if (!kept.Contains(t.LordId)) tracks.Remove(t.LordId);
            }
        }

        // The save's lists: per track, in Tracks order, its ints (lord, spawn
        // tick, spawn x, spawn z, ground, last tick, trail length, then each
        // trail cell's x and z), its faction def and its range.
        public (List<int> Ints, List<string> Factions, List<float> Ranges) Export()
        {
            var ints = new List<int>();
            var factions = new List<string>();
            var ranges = new List<float>();
            foreach (var t in Tracks)
            {
                ints.AddRange(new[] { t.LordId, t.SpawnTick, t.SpawnX, t.SpawnZ, t.Ground ? 1 : 0, t.LastTick, t.Trail.Count });
                foreach (var (x, z) in t.Trail) { ints.Add(x); ints.Add(z); }
                factions.Add(t.FactionDef);
                ranges.Add(t.Range);
            }
            return (ints, factions, ranges);
        }

        // Replaces the log with a save's lists; any list missing or malformed loads none.
        public void Import(List<int>? ints, List<string>? factions, List<float>? ranges)
        {
            tracks.Clear();
            if (ints == null || factions == null || ranges == null || factions.Count != ranges.Count) return;
            var read = new List<Track>();
            var i = 0;
            for (var n = 0; n < factions.Count; n++)
            {
                if (i + 7 > ints.Count) return;
                var track = new Track
                {
                    LordId = ints[i], SpawnTick = ints[i + 1], SpawnX = ints[i + 2], SpawnZ = ints[i + 3], Ground = ints[i + 4] == 1,
                    LastTick = ints[i + 5], FactionDef = factions[n] ?? "", Range = ranges[n],
                };
                var cells = ints[i + 6];
                i += 7;
                if ((ints[i - 3] != 0 && ints[i - 3] != 1) || cells < 0 || i + 2L * cells > ints.Count || float.IsNaN(track.Range) || track.Range < 0) return;
                for (var c = 0; c < cells; c++, i += 2) track.Trail.Add((ints[i], ints[i + 1]));
                read.Add(track);
            }
            if (i != ints.Count || read.Select(t => t.LordId).Distinct().Count() != read.Count) return;
            foreach (var t in read) tracks[t.LordId] = t;
        }
    }
}
