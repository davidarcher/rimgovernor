#nullable enable
using System;
using System.Collections.Generic;

namespace HomeBridge.BridgeTools
{
    // Per colonist, where it stood at each of its own observer passes (#3806):
    // PawnObserver.ObserveSurroundingThings sees every corpse within 5 cells in
    // line of sight while the pawn is awake, so a corpse's sightings by one
    // colonist are the passes it stood near enough to see it. Free of game types
    // so the contract probes can check the ring. Saved in the map's TrafficState
    // as Export's flat list (#3835); a save from before it loads empty, and the
    // brain credits that record from what it holds (nothing yet covered).
    //
    // Bound: one ring per pawn recorded, each Capacity (tick, cell) pairs, so
    // 267 x 8 bytes (about 2 KB) per colonist; Retain drops the rings of pawns
    // no longer recorded, so the log holds at most one ring per colonist on the map.
    public sealed class SightingLog
    {
        // Two game days of passes: a colonist's routine repeats daily, and the
        // brain folds the whole days the ring covers (ForecastCorpses).
        public const int WindowTicks = 120000;

        // The observer's fastest cadence: Need_Mood.NeedInterval runs every 150
        // ticks and PawnObserver.ObserverInterval observes every 4 +
        // Rand.RangeInclusive(-1, 1) intervals, so passes are at least 450 ticks
        // apart and a window holds at most Capacity of them.
        public const int FastestPassTicks = 3 * 150;
        public const int Capacity = WindowTicks / FastestPassTicks + 1;

        private sealed class Ring
        {
            public readonly int[] Ticks = new int[Capacity];
            public readonly int[] Cells = new int[Capacity];
            public int Head, Count, Since;
        }

        private readonly Dictionary<int, Ring> rings = new Dictionary<int, Ring>();

        public int Pawns => rings.Count;

        // Record notes one observer pass of pawn at tick, standing on cell. The
        // first pass starts the pawn's record (Since); the oldest pass is
        // overwritten once the ring is full, which only a pass older than the
        // window can be.
        public void Record(int pawn, int tick, int cell)
        {
            if (!rings.TryGetValue(pawn, out var ring)) rings[pawn] = ring = new Ring { Since = tick };
            int at = (ring.Head + ring.Count) % Capacity;
            if (ring.Count == Capacity) ring.Head = (ring.Head + 1) % Capacity;
            else ring.Count++;
            ring.Ticks[at] = tick;
            ring.Cells[at] = cell;
        }

        // Covered is the ticks before now the pawn's record covers: since its
        // first pass, at most the window; 0 for a pawn with no record.
        public int Covered(int pawn, int now) =>
            rings.TryGetValue(pawn, out var ring) ? Math.Max(0, Math.Min(WindowTicks, now - ring.Since)) : 0;

        // Passes lists the pawn's passes inside the window before now, oldest first.
        public List<(int Tick, int Cell)> Passes(int pawn, int now)
        {
            var passes = new List<(int, int)>();
            if (!rings.TryGetValue(pawn, out var ring)) return passes;
            for (int i = 0; i < ring.Count; i++)
            {
                int at = (ring.Head + i) % Capacity;
                if (now - ring.Ticks[at] < WindowTicks && ring.Ticks[at] <= now) passes.Add((ring.Ticks[at], ring.Cells[at]));
            }
            return passes;
        }

        // Export flattens the log for the save: per pawn its id, Since, the count
        // of its passes, then each pass's tick and cell, oldest first.
        public List<int> Export()
        {
            var flat = new List<int>();
            foreach (var pair in rings)
            {
                var ring = pair.Value;
                flat.Add(pair.Key);
                flat.Add(ring.Since);
                flat.Add(ring.Count);
                for (int i = 0; i < ring.Count; i++)
                {
                    int at = (ring.Head + i) % Capacity;
                    flat.Add(ring.Ticks[at]);
                    flat.Add(ring.Cells[at]);
                }
            }
            return flat;
        }

        // Import replaces the log with an Export's list; a null list (a save
        // from before the log was saved) or a malformed one leaves it empty.
        public void Import(List<int>? flat)
        {
            rings.Clear();
            if (flat == null) return;
            int i = 0;
            while (i < flat.Count)
            {
                if (flat.Count - i < 3) { rings.Clear(); return; }
                int pawn = flat[i], since = flat[i + 1], count = flat[i + 2];
                i += 3;
                if (count < 0 || count > Capacity || flat.Count - i < 2 * count || rings.ContainsKey(pawn)) { rings.Clear(); return; }
                var ring = new Ring { Since = since, Count = count };
                for (int k = 0; k < count; k++, i += 2)
                {
                    ring.Ticks[k] = flat[i];
                    ring.Cells[k] = flat[i + 1];
                }
                rings[pawn] = ring;
            }
        }

        // Retain drops every pawn's ring keep refuses.
        public void Retain(Func<int, bool> keep)
        {
            List<int>? gone = null;
            foreach (var pawn in rings.Keys)
                if (!keep(pawn)) (gone ??= new List<int>()).Add(pawn);
            if (gone != null) foreach (var pawn in gone) rings.Remove(pawn);
        }
    }
}
