#nullable enable

using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // WallLayerGuard (#2314): a thick wall can be laid, funded and worked all at
    // once and still be built from the inside out. A builder needs a standable
    // cell beside what it builds and a frame cannot be stood on, so the outer
    // layer's frames would seal a middle layer that is not finished yet and the
    // wall would never close. The guard refuses construction work (delivery
    // and finishing alike) on a wall blueprint or frame while a deeper wall cell
    // beside it is still unfinished. A blueprint is standable, so an outer layer
    // that has received nothing stays walkable until the layer inside it stands.
    //
    // Depth is the orthogonal steps through wall cells to the nearest open cell:
    // an outside corner touches open ground and is 1, an inside corner touches
    // it only diagonally and is 2, so it goes before the two arms that would
    // otherwise close it in.
    internal static class WallLayerGuard
    {
        private const int MaxDepth = 8;
        private static bool patched;

        internal static void Install()
        {
            if (patched) return;
            patched = true;
            new Harmony("rimgovernor.wall-layers").Patch(
                AccessTools.Method(typeof(GenConstruct), nameof(GenConstruct.CanConstruct), new[] { typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool), typeof(JobDef) }),
                postfix: new HarmonyMethod(typeof(WallLayerGuard), nameof(Guard)));
        }

        private static void Guard(Thing t, ref bool __result)
        {
            if (!__result || !Supervisor.IsActive || !UnfinishedWall(t)) return;
            var map = t.Map;
            if (map == null || !DeeperUnfinishedBeside(map, t.Position)) return;
            JobFailReason.Is("inner wall layer first");
            __result = false;
        }

        // UnfinishedWall is a player blueprint or frame of something impassable.
        internal static bool UnfinishedWall(Thing t) =>
            (t is Blueprint_Build || t is Frame) && t.Faction == Faction.OfPlayer
            && (t.def.entityDefToBuild as ThingDef)?.passability == Traversability.Impassable;

        // WallSolidAt is whether the cell is, or is to be, impassable: rock, a
        // standing wall, or an unfinished wall.
        private static bool WallSolidAt(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map)) return false;
            if (cell.Impassable(map)) return true;
            var things = map.thingGrid.ThingsListAtFast(cell);
            for (var i = 0; i < things.Count; i++)
                if (UnfinishedWall(things[i])) return true;
            return false;
        }

        private static bool HoldsUnfinishedWall(Map map, IntVec3 cell)
        {
            var things = map.thingGrid.ThingsListAtFast(cell);
            for (var i = 0; i < things.Count; i++)
                if (UnfinishedWall(things[i])) return true;
            return false;
        }

        // Depth is the orthogonal steps through solid cells from the cell to the
        // nearest open one.
        private static int Depth(Map map, IntVec3 cell)
        {
            var seen = new HashSet<IntVec3> { cell };
            var frontier = new List<IntVec3> { cell };
            for (var steps = 1; steps <= MaxDepth; steps++)
            {
                var next = new List<IntVec3>();
                foreach (var c in frontier)
                {
                    foreach (var n in new[] { new IntVec3(c.x + 1, 0, c.z), new IntVec3(c.x - 1, 0, c.z), new IntVec3(c.x, 0, c.z + 1), new IntVec3(c.x, 0, c.z - 1) })
                    {
                        if (!seen.Add(n)) continue;
                        if (!WallSolidAt(map, n)) return steps;
                        next.Add(n);
                    }
                }
                frontier = next;
            }
            return MaxDepth;
        }

        private static bool DeeperUnfinishedBeside(Map map, IntVec3 cell)
        {
            var depth = -1;
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var n = new IntVec3(cell.x + dx, 0, cell.z + dz);
                    if (!n.InBounds(map) || !HoldsUnfinishedWall(map, n)) continue;
                    if (depth < 0) depth = Depth(map, cell);
                    if (Depth(map, n) > depth) return true;
                }
            }
            return false;
        }
    }
}
