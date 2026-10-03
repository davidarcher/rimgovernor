#nullable enable

using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Staged room/corridor excavation. Unlike surface mining (MiningBlocker),
    // roofed cells are admissible: the safety question is whether every roof
    // that remains stays within the installed support radius of a holder once
    // the requested cells are gone. The map is never edited to preview removal.
    internal static class ExcavationSafety
    {
        internal enum Support { Supported, Unknown, Unsupported }

        // Counterfactual support for roofs near a set of removed holders,
        // following the installed RoofCollapseUtility's connected-roof/radius
        // rule. Fogged cells are unknown by default: they can neither support nor
        // be assumed safe. With throughFog the true map is read under fog, as the
        // game does for collapse: fogged rock or ore holds, a fogged open cell
        // does not (#986). Pending collapse anywhere near the set blocks.
        // Shortcut settles a roof by a holder beside it without the flood;
        // the result is the same (#1295). test/colony_facts_equality turns it off.
        internal static bool Shortcut = true;
        internal static Support Check(Map map, ICollection<IntVec3> removed, out int checkedRoofs, out string? blocker, bool throughFog = false)
        {
            checkedRoofs = 0; blocker = null;
            var radius = RoofCollapseUtility.RoofMaxSupportDistance;
            var removedSet = new HashSet<IntVec3>(removed);
            // One removed cell whose every nearby roof has a holder beside it
            // is Supported whatever order the roofs are checked in, so the
            // common single-rock check skips the set, the sort and the
            // floods (#1295); anything else takes the full walk below.
            if (Shortcut && removed.Count == 1 && AllHeldBeside(map, removedSet, radius, throughFog, out checkedRoofs)) return Support.Supported;
            checkedRoofs = 0;
            var roots = new HashSet<IntVec3>();
            // The roof over a removed holder stays up and needs support like
            // any other: a room whose surroundings were levelled keeps its
            // rock roof only while its own remaining rock holds it.
            foreach (var cell in removed)
                foreach (var near in GenRadial.RadialCellsAround(cell, radius, true))
                    if (near.InBounds(map) && (throughFog || !near.Fogged(map)) && near.Roofed(map)) roots.Add(near);
            var result = Support.Supported;
            foreach (var root in roots.OrderBy(c => c.x).ThenBy(c => c.z)) {
                checkedRoofs++;
                if (map.roofCollapseBuffer.IsMarkedToCollapse(root)) { blocker = "Roof collapse is already pending"; return Support.Unsupported; }
                // The flood's first step alone: a holder beside the root
                // supports it, the common case under rock, without the
                // flood's allocations (#1295).
                if (Shortcut && HolderBeside(map, root, radius, removedSet, throughFog)) continue;
                var queue = new Queue<IntVec3>();
                var seen = new HashSet<IntVec3>();
                queue.Enqueue(root); seen.Add(root);
                bool supported = false, unknown = false;
                while (queue.Count > 0 && !supported) {
                    var c = queue.Dequeue();
                    foreach (var offset in GenAdj.CardinalDirectionsAndInside) {
                        var near = c + offset;
                        if (!near.InBounds(map) || !near.InHorDistOf(root, radius)) continue;
                        if (!throughFog && near.Fogged(map)) { unknown = true; continue; }
                        if (removedSet.Contains(near)) continue;
                        var holder = near.GetEdifice(map);
                        if (holder != null && holder.def.holdsRoof) { supported = true; break; }
                    }
                    foreach (var offset in GenAdj.CardinalDirections) {
                        var next = c + offset;
                        if (!next.InBounds(map) || !next.InHorDistOf(root, radius) || !next.Roofed(map)) continue;
                        if (!throughFog && next.Fogged(map)) { unknown = true; continue; }
                        if (seen.Add(next)) queue.Enqueue(next);
                    }
                }
                if (supported) continue;
                if (!unknown) { blocker = "Removal would leave unsupported roof"; return Support.Unsupported; }
                result = Support.Unknown; blocker = "Alternate support geometry is unknown";
            }
            return result;
        }

        private static bool AllHeldBeside(Map map, HashSet<IntVec3> removedSet, float radius, bool throughFog, out int roofs)
        {
            roofs = 0;
            foreach (var cell in removedSet)
                foreach (var root in GenRadial.RadialCellsAround(cell, radius, true)) {
                    if (!root.InBounds(map) || (!throughFog && root.Fogged(map)) || !root.Roofed(map)) continue;
                    roofs++;
                    if (map.roofCollapseBuffer.IsMarkedToCollapse(root) || !HolderBeside(map, root, radius, removedSet, throughFog)) return false;
                }
            return true;
        }

        private static bool HolderBeside(Map map, IntVec3 root, float radius, HashSet<IntVec3> removedSet, bool throughFog)
        {
            foreach (var offset in GenAdj.CardinalDirectionsAndInside) {
                var near = root + offset;
                if (!near.InBounds(map) || !near.InHorDistOf(root, radius)) continue;
                if (!throughFog && near.Fogged(map)) continue;
                if (removedSet.Contains(near)) continue;
                var holder = near.GetEdifice(map);
                if (holder != null && holder.def.holdsRoof) return true;
            }
            return false;
        }
    }

    internal static class ExcavationTools
    {
        internal static Mineable? RockAt(IntVec3 cell, Map map) => cell.InBounds(map) ? cell.GetEdifice(map) as Mineable : null;

        // Per-cell excavation eligibility. Roof, home area, zones and adjacent
        // structures are deliberately not blockers here; those belong to the
        // surface-mining rule. Support is a site-level counterfactual check.
        internal static string? CellBlocker(IntVec3 cell, Map map)
        {
            if (!cell.InBounds(map)) return "Cell is outside the map";
            var rock = RockAt(cell, map);
            if (rock == null) return "No native rock at cell";
            if (rock.Faction != null) return "Faction-owned excavation target is protected";
            if (cell.GetThingList(map).Any(other => other != rock && (other is Blueprint || other is Frame || (other is Building && !(other is Mineable)))))
                return "Excavation cell holds a protected structure";
            foreach (var near in GenAdj.CellsAdjacent8Way(cell, Rot4.North, IntVec2.One))
                if (!near.InBounds(map)) return "Map edge excavation is protected";
            return null;
        }

        internal static bool Designated(IntVec3 cell, Map map) => map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) != null;

        internal static bool Eligible(IntVec3 cell, Map map) => CellBlocker(cell, map) == null
            && (Designated(cell, map) || new Designator_Mine().CanDesignateCell(cell).Accepted);

        // The cell a miner would stand on to reach access: access itself when
        // walkable, else its first visible walkable cardinal neighbour, else
        // IntVec3.Invalid.
        internal static IntVec3 StandingCell(IntVec3 access, Map map)
        {
            if (!access.InBounds(map) || access.Fogged(map)) return IntVec3.Invalid;
            if (access.Walkable(map)) return access;
            foreach (var offset in GenAdj.CardinalDirections) {
                var near = access + offset;
                if (near.InBounds(map) && !near.Fogged(map) && near.Walkable(map)) return near;
            }
            return IntVec3.Invalid;
        }

        // Mining-capable free colonists that can currently stand on the access cell.
        internal static List<Pawn> Workers(Map map, IntVec3 access) => map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted
            && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining)
            && p.workSettings?.Initialized == true && p.workSettings.GetPriority(WorkTypeDefOf.Mining) > 0
            && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
            && p.CanReach(access, PathEndMode.OnCell, Danger.None)).OrderBy(p => p.GetUniqueLoadID(), System.StringComparer.Ordinal).ToList();
    }
}
