#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace RimGovernor.Native
{
    // The hostile lords RaidArrivalState has sampled (RG-HABITAT-009), free of
    // game types so the contract probes can check it. A lord seen within
    // FinishedAge keeps its whole trail; one unseen longer is finished. Every
    // finished ground raid's track stays, so the defense probe keeps every
    // observed approach across days and loads; a finished track that did not
    // walk in (a drop, a tunnel) is evicted. Saved with the map as Export's
    // three flat lists; a save from before it, or a malformed list, loads no
    // tracks.
    //
    // A trail holds each cell once, in the order first walked, so it is bounded
    // by the map and not by a count: a lord camped for days adds nothing while
    // its nearest pawn recrosses its own ground.
    public sealed class RaidTrackLog
    {
        public const int FinishedAge = 60000;

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
            private readonly HashSet<(int X, int Z)> walked = new HashSet<(int X, int Z)>();

            // Appends a cell the trail does not hold yet.
            public void Walk((int X, int Z) cell)
            {
                if (walked.Add(cell)) Trail.Add(cell);
            }
        }

        private readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();

        // The tracks oldest first (spawn tick, then lord).
        public IEnumerable<Track> Tracks => tracks.Values.OrderBy(t => t.SpawnTick).ThenBy(t => t.LordId);

        public int Count => tracks.Count;

        // One sample of a live hostile lord: the first one records its spawn
        // (ground when it lies on the map edge); each adds the nearest pawn's
        // cell when the trail does not hold it and keeps the longest range.
        public void Sample(int lordId, string factionDef, int tick, (int X, int Z) first, bool ground, (int X, int Z) nearest, float range)
        {
            if (!tracks.TryGetValue(lordId, out var track))
            {
                track = new Track { LordId = lordId, FactionDef = factionDef, SpawnTick = tick, SpawnX = first.X, SpawnZ = first.Z, Ground = ground };
                tracks[lordId] = track;
            }
            track.LastTick = tick;
            track.Range = Math.Max(track.Range, range);
            track.Walk(nearest);
        }

        // Evicts every finished track that did not walk in; a finished ground raid's stays.
        public void Evict(int tick)
        {
            foreach (var t in tracks.Values.Where(t => !t.Ground && tick - t.LastTick > FinishedAge).ToList()) tracks.Remove(t.LordId);
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
                for (var c = 0; c < cells; c++, i += 2) track.Walk((ints[i], ints[i + 1]));
                read.Add(track);
            }
            if (i != ints.Count || read.Select(t => t.LordId).Distinct().Count() != read.Count) return;
            foreach (var t in read) tracks[t.LordId] = t;
        }
    }
}
