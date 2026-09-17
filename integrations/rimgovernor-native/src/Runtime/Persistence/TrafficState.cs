using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Observed colonist travel (issue #6 slice 5): every 30 ticks each free
    // colonist that is actually walking adds one sample to the cell it
    // stands on. The counts are the only traffic evidence the controller
    // reads; nothing is projected from straight lines or flood fill. Counts
    // live for the loaded session only: a reload starts a fresh window, and
    // the census reports the window's start tick and total samples so a
    // short window is not mistaken for a quiet colony.
    public sealed class TrafficState : MapComponent
    {
        public const int SampleInterval = 30;
        public readonly Dictionary<IntVec3, uint> Samples = new Dictionary<IntVec3, uint>();
        public uint Total;
        public int SinceTick = -1;

        public TrafficState(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            var tick = Find.TickManager.TicksGame;
            if (tick % SampleInterval != 0) return;
            if (SinceTick < 0) SinceTick = tick;
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.Dead || pawn.Downed || pawn.pather == null || !pawn.pather.Moving) continue;
                var cell = pawn.Position;
                Samples.TryGetValue(cell, out var n);
                Samples[cell] = n + 1;
                Total++;
            }
        }
    }
}
