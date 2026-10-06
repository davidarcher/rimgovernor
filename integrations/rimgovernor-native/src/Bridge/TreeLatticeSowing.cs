#nullable enable

using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Trees carry blockAdjacentSow: a sown plant blocks sowing on its 8
    // neighbours (PlantUtility.AdjacentSowBlocker), so a plain growing zone
    // fills in random order and ends well under one tree per 2x2 cells. For
    // such a plant WorkGiver_GrowerSow.JobOnCell offers only cells whose
    // offsets from the zone's minimum corner are both even: every lattice cell
    // keeps a free ring, none is blocked by another lattice tree, and the zone
    // fills completely (ceil(w/2) * ceil(h/2) trees on a w x h rectangle).
    // Other crops are untouched. The pitch is mirrored by
    // domain.TreeLatticePitch (a Go test reads this constant).
    internal static class TreeLatticeSowing
    {
        internal const int Pitch = 2;
        private static bool installed;

        internal static void Install()
        {
            if (installed) return;
            var harmony = new Harmony("rimgovernor.tree-lattice-sowing");
            harmony.Patch(AccessTools.Method(typeof(WorkGiver_GrowerSow), nameof(WorkGiver_GrowerSow.JobOnCell)),
                prefix: new HarmonyMethod(typeof(TreeLatticeSowing), nameof(Before)));
            installed = true;
        }

        // OnLattice reports whether c is a sowing cell for a blockAdjacentSow
        // plant in its growing zone, anchored on the zone's minimum corner.
        internal static bool OnLattice(Zone_Growing zone, IntVec3 c)
        {
            var cells = zone.Cells;
            if (cells.Count == 0) return true;
            int minX = cells.Min(cell => cell.x), minZ = cells.Min(cell => cell.z);
            return (c.x - minX) % Pitch == 0 && (c.z - minZ) % Pitch == 0;
        }

        private static bool Before(Pawn pawn, IntVec3 c, ref Job? __result)
        {
            var map = pawn?.Map;
            if (map == null || !(c.GetZone(map) is Zone_Growing zone)) return true;
            var plant = zone.GetPlantDefToGrow();
            if (plant?.plant == null || !plant.plant.blockAdjacentSow || OnLattice(zone, c)) return true;
            __result = null;
            return false;
        }
    }
}
