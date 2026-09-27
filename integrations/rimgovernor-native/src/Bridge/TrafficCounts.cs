#nullable enable
using System;
using System.Collections.Generic;

namespace HomeBridge.BridgeTools
{
    // Traffic heat counters (#817), free of game types so the contract
    // probes can check counting, decay and crossing detection. One ushort
    // per cell per layer, saturating; each layer halves on its own
    // half-life, swept incrementally so no tick pays for a whole map.
    // Layer numbers are Obs.TrafficLayer's values minus one.
    internal sealed class TrafficCounts
    {
        internal const int Colonist = 0, Crossing = 1, Animal = 2, Visitor = 3, Hostile = 4, Layers = 5;

        // Half-lives in ticks: colonist, crossing and animal ~2 days;
        // visits are occasional, ~5 days; raids are weeks apart, ~20 days.
        internal static readonly int[] HalfLife = { 120000, 120000, 120000, 300000, 1200000 };

        private readonly ushort[][] counts;
        private readonly int[] cursor = new int[Layers];
        private readonly double[] owed = new double[Layers];

        internal TrafficCounts(int cells)
        {
            counts = new ushort[Layers][];
            for (int l = 0; l < Layers; l++) counts[l] = new ushort[cells];
        }

        internal int Cells => counts[0].Length;

        internal ushort this[int layer, int index] => counts[layer][index];

        // Crossing is any pawn stepping from terrain that makes tracked
        // filth (TerrainDef.generatedFilth: soil, sand, gravel...) onto a
        // built floor (TerrainDef.IsFloor, the "Floor" tag: tiles, carpet,
        // straw matting). That is the step on which Pawn_FilthTracker can
        // drop terrain dirt on a floor; floor-to-floor and soil-to-soil
        // steps never count.
        internal static bool IsCrossing(bool fromMakesFilth, bool toIsFloor) => fromMakesFilth && toIsFloor;

        // Step counts one cell change onto index for the pawn's layer
        // (negative for none) and the crossing layer when it is one.
        internal void Step(int layer, bool crossing, int index)
        {
            if (index < 0 || index >= Cells) return;
            if (layer >= 0) Bump(counts[layer], index);
            if (crossing) Bump(counts[Crossing], index);
        }

        private static void Bump(ushort[] layer, int index)
        {
            if (layer[index] != ushort.MaxValue) layer[index]++;
        }

        // Decay halves each layer once per half-life: elapsed ticks earn
        // each layer its share of a full sweep, and that many cells past its
        // cursor are halved now.
        internal void Decay(int elapsedTicks)
        {
            if (elapsedTicks <= 0) return;
            for (int l = 0; l < Layers; l++)
            {
                owed[l] += (double)Cells * elapsedTicks / HalfLife[l];
                int n = (int)Math.Min(owed[l], Cells);
                owed[l] -= n;
                var layer = counts[l];
                for (int i = 0; i < n; i++)
                {
                    int c = cursor[l];
                    layer[c] = (ushort)(layer[c] >> 1);
                    cursor[l] = c + 1 == Cells ? 0 : c + 1;
                }
            }
        }

        // Total is the layer's summed count.
        internal uint Total(int layer)
        {
            uint total = 0;
            foreach (var n in counts[layer]) total += n;
            return total;
        }

        // Top is the layer's n busiest non-zero cells, busiest first, ties
        // by index so a read is deterministic.
        internal List<int> Top(int layer, int n)
        {
            var source = counts[layer];
            var heap = new SortedSet<(ushort count, int negIndex)>();
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == 0) continue;
                if (heap.Count < n) { heap.Add((source[i], -i)); continue; }
                var min = heap.Min;
                if (source[i] > min.count) { heap.Remove(min); heap.Add((source[i], -i)); }
            }
            var top = new List<int>(heap.Count);
            foreach (var entry in heap.Reverse()) top.Add(-entry.negIndex);
            return top;
        }
    }
}
