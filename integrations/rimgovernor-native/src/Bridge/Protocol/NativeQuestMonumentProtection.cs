#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Live intent preconditions: no cached copy of a quest's keep-time state.
    internal static class NativeQuestMonumentProtection
    {
        internal const string Refusal = "An ongoing quest requires this completed monument to remain intact.";

        private static IEnumerable<MonumentMarker> Markers(Map map) => Find.QuestManager.ActiveQuestsListForReading
            .Where(q => q.State == QuestState.Ongoing)
            .SelectMany(q => q.PartsListForReading).SelectMany(p => p.QuestLookTargets)
            .Select(t => t.Thing).OfType<MonumentMarker>()
            .Where(m => !m.Destroyed && m.Spawned && m.Map == map && m.complete).Distinct();

        internal static bool Protects(Thing thing) => thing != null && thing.Spawned
            && Markers(thing.Map).Any(m => m == thing || m.IsPart(thing));

        internal static bool ProtectsTerrain(Map map, IntVec3 cell) => Markers(map)
            .Any(m => m.sketch.Terrain.Any(t => t.OccupiedRect.MovedBy(m.Position).Contains(cell)));

        internal static bool BlocksPlacement(Map map, BuildableDef def, IntVec3 cell, Rot4 rotation)
        {
            var rect = GenAdj.OccupiedRect(cell, rotation, def.Size);
            return Markers(map).Any(m => m.sketch.OccupiedRect.MovedBy(m.Position).Overlaps(rect));
        }
    }
}
