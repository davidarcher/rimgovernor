#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class NativeShrineBreachSafety
    {
        // Sealed shrines necessarily contain fog. Inspect only their structural
        // footprint for the counterfactual roof check, without revealing occupants
        // or unfogging cells. Fog outside that footprint still fails closed.
        internal static HashSet<IntVec3>? StructuralCells(Building wall)
        {
            var map = wall.Map;
            if (map == null || !wall.Spawned || wall.Position.Fogged(map)
                || wall.Faction == Faction.OfPlayer || wall.def != ThingDefOf.Wall && !(wall is Building_Door)) return null;
            var rooms = map.listerThings.AllThings.OfType<Building_AncientCryptosleepCasket>()
                .Where(c => c.Spawned && c.Position.Fogged(map)).Select(c => c.GetRoom())
                .Where(r => r != null && r.ProperRoom && !r.TouchesMapEdge && !r.PsychologicallyOutdoors && r.OpenRoofCount == 0)
                .Distinct();
            var cells = new HashSet<IntVec3>();
            foreach (var room in rooms) {
                var border = room.BorderCellsCardinal.ToList();
                if (!border.Contains(wall.Position)) continue;
                var rect = CellRect.FromCellList(room.Cells.ToList());
                if (rect.Width > 256 || rect.Height > 256) return null;
                cells.UnionWith(room.Cells);
                cells.UnionWith(border);
            }
            return cells.Count == 0 ? null : cells;
        }

        internal static string? RoofBlocker(Building wall) =>
            RoofSupportSafety.Blocker(wall, null, out _, StructuralCells(wall));
    }
}
