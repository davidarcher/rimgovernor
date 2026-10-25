
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    internal static class RoofSupportSafety
    {
        internal static bool GeometryKnown(Map map, IntVec3 cell) =>
            GenRadial.RadialCellsAround(cell, RoofCollapseUtility.RoofMaxSupportDistance, true)
                .All(c => c.InBounds(map) && !c.Fogged(map));
        // Counterfactual version of the installed RoofCollapseUtility's connected
        // roof/radius rule. The map is never edited to preview removal.
        internal static string? Blocker(Building building, out int checkedRoofs) => Blocker(building, null, out checkedRoofs);

        // assumedHolders are open cells counted as roof holders the removal
        // would find standing: the stone-shell census lists a fresh candidate
        // only when its backups, once built, keep every roof up.
        internal static string? Blocker(Building building, ICollection<IntVec3>? assumedHolders, out int checkedRoofs)
        {
            checkedRoofs = 0;
            if (building?.Map == null || !building.Spawned) return "Observed building is unavailable";
            // Removing a non-holder cannot change support, even under a roof.
            if (!building.def.holdsRoof) return null;
            var map = building.Map;
            var radius = RoofCollapseUtility.RoofMaxSupportDistance;
            return RoofSupportGeometry.Blocker(building.OccupiedRect().ToList(),
                c => GenRadial.RadialCellsAround(c, radius, true),
                c => GenAdj.CardinalDirections.Select(offset => c + offset),
                c => GenAdj.CardinalDirectionsAndInside.Select(offset => c + offset),
                (near, root) => near.InHorDistOf(root, radius),
                c => c.InBounds(map), c => c.Fogged(map), c => c.Roofed(map),
                c => c.GetEdifice(map)?.def.holdsRoof == true || assumedHolders?.Contains(c) == true,
                c => map.roofCollapseBuffer.IsMarkedToCollapse(c), out checkedRoofs);
        }

        // The roofed cells the game would collapse when the roof-holder despawns (#3617), as
        // RoofCollapseCellsFinder.ProcessRoofHolderDespawned finds them with the building's cells no longer holding
        // roof: the roof components around its footprint that no other holder connects to (CheckCollapseFlyingRoofs),
        // the collapsible roof cells within RoofMaxSupportDistance of its position with no holder within that range
        // of them through roofed cells (WithinRangeOfRoofHolder), and the components those leave with no holder.
        // Nearest the building first. Null when the answer is unknown: the geometry around the footprint is fogged or
        // out of bounds, a collapse is already pending, a cell found carries a thick roof (the NoRoof area refuses it)
        // or a component is too large to prove, the set would not fit a cell set step, or the set is empty while the
        // enclosure guard would still wait (the two rules disagree). The map is never edited.
        internal static List<IntVec3>? Unsupported(Building building)
        {
            if (building?.Map == null || !building.Spawned || !building.def.holdsRoof) return null;
            var map = building.Map;
            var radius = RoofCollapseUtility.RoofMaxSupportDistance;
            var excluded = new HashSet<IntVec3>(building.OccupiedRect());
            if (excluded.Any(c => !GeometryKnown(map, c))) return null;
            bool Roofed(IntVec3 c) => c.InBounds(map) && c.Roofed(map);
            bool Holder(IntVec3 c) => c.InBounds(map) && !excluded.Contains(c) && c.GetEdifice(map)?.def.holdsRoof == true;
            bool HolderBeside(IntVec3 c) => GenAdj.CardinalDirectionsAndInside.Any(offset => Holder(c + offset));
            bool Unknown(IntVec3 c) => c.Fogged(map) || map.roofCollapseBuffer.IsMarkedToCollapse(c);
            var collapsing = new HashSet<IntVec3>();
            var visited = new HashSet<IntVec3>();
            // A roofed component with no holder beside any cell flies; a bounded cardinal flood over roofed cells.
            // Returns false when the component is unknown (fogged, pending or too large).
            bool Component(IntVec3 root, out List<IntVec3> cells, out bool supported)
            {
                cells = new List<IntVec3>(); supported = false;
                var queue = new Queue<IntVec3>();
                var seen = new HashSet<IntVec3> { root };
                queue.Enqueue(root); visited.Add(root);
                while (queue.Count > 0)
                {
                    var cell = queue.Dequeue();
                    if (Unknown(cell)) return false;
                    cells.Add(cell);
                    if (cells.Count > 2048) return false;
                    if (HolderBeside(cell)) { supported = true; return true; }
                    foreach (var offset in GenAdj.CardinalDirections)
                    {
                        var next = cell + offset;
                        if (!Roofed(next) || !seen.Add(next)) continue;
                        // An earlier root's flood stopped here at a holder: the game counts the meeting as connected.
                        if (!visited.Add(next)) { supported = true; return true; }
                        queue.Enqueue(next);
                    }
                }
                return true;
            }
            bool Flying(IEnumerable<IntVec3> roots)
            {
                foreach (var root in roots)
                {
                    if (!Roofed(root) || visited.Contains(root) || collapsing.Contains(root)) continue;
                    if (!Component(root, out var cells, out var supported)) return false;
                    if (!supported) collapsing.UnionWith(cells);
                }
                return true;
            }
            // WithinRangeOfRoofHolder with the building gone: a holder within the radius of c beside a roofed cell
            // (or c itself) the radius-bounded cardinal flood from c reaches.
            bool WithinRange(IntVec3 c)
            {
                var seen = new HashSet<IntVec3> { c };
                var queue = new Queue<IntVec3>();
                queue.Enqueue(c);
                while (queue.Count > 0)
                {
                    var cell = queue.Dequeue();
                    if (GenAdj.CardinalDirectionsAndInside.Any(offset => cell + offset is var near && near.InHorDistOf(c, radius) && Holder(near))) return true;
                    foreach (var offset in GenAdj.CardinalDirections)
                    {
                        var next = cell + offset;
                        if (Roofed(next) && next.InHorDistOf(c, radius) && seen.Add(next)) queue.Enqueue(next);
                    }
                }
                return false;
            }
            visited.Clear();
            if (!Flying(excluded.SelectMany(c => GenAdj.CardinalDirectionsAndInside.Select(offset => c + offset)))) return null;
            var tooFar = new List<IntVec3>();
            foreach (var cell in GenRadial.RadialCellsAround(building.Position, radius, true))
            {
                if (!Roofed(cell) || collapsing.Contains(cell)) continue;
                if (Unknown(cell)) return null;
                if (map.roofGrid.RoofAt(cell).canCollapse && !WithinRange(cell)) tooFar.Add(cell);
            }
            collapsing.UnionWith(tooFar);
            visited.Clear();
            if (!Flying(tooFar.SelectMany(c => GenAdj.CardinalDirectionsAndInside.Select(offset => c + offset)))) return null;
            if (collapsing.Count > 256 || collapsing.Any(c => map.roofGrid.RoofAt(c).isThickRoof)) return null;
            if (collapsing.Count == 0 && NativeDesignationGuards.RoofWait(building) != null) return null;
            return collapsing.OrderBy(c => c.DistanceToSquared(building.Position)).ThenBy(c => c.x).ThenBy(c => c.z).ToList();
        }
    }
}
