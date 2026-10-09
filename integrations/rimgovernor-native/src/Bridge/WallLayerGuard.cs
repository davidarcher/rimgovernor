#nullable enable

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Funding is unrestricted. Completion must retain reachable work cells
    // beside unfinished neighbours and an escape for nearby colonists.
    internal static class WallLayerGuard
    {
        private static bool patched;
        internal static void Install()
        {
            if (patched) return;
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
                if (typeof(Frame).IsAssignableFrom(def.thingClass) && Wall(def.entityDefToBuild as ThingDef))
                    def.passability = Traversability.Standable;
            var harmony = new Harmony("rimgovernor.wall-layers");
            harmony.Patch(
                AccessTools.Method(typeof(Frame), nameof(Frame.CompleteConstruction)),
                prefix: new HarmonyMethod(typeof(WallLayerGuard), nameof(Complete)));
            harmony.Patch(AccessTools.Method(typeof(GenConstruct), nameof(GenConstruct.CanConstruct),
                new[] { typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool), typeof(JobDef) }),
                postfix: new HarmonyMethod(typeof(WallLayerGuard), nameof(WorkAvailable)));
            patched = true;
        }
        private static bool Wall(ThingDef? def) => def?.building != null
            && def.passability == Traversability.Impassable && !def.building.isNaturalRock;
        internal static bool UnfinishedWall(Thing t) =>
            (t is Blueprint_Build || t is Frame) && t.Faction == Faction.OfPlayer
            && Wall(t.def.entityDefToBuild as ThingDef);
        // A refused completion leaves the work done. Skip that finished-work
        // frame until it is safe, so a builder can choose another frame instead
        // of repeatedly finishing the same blocked one. Delivery stays legal.
        private static void WorkAvailable(Thing t, Pawn p, ref bool __result)
        {
            if (__result && Supervisor.IsActive && t is Frame frame && UnfinishedWall(frame)
                && frame.WorkLeft <= 0) __result = CanComplete(frame, p);
        }
        private static bool Complete(Frame __instance, Pawn worker)
        {
            if (!Supervisor.IsActive || !UnfinishedWall(__instance) || __instance.Map == null) return true;
            if (CanComplete(__instance, worker)) return true;
            JobFailReason.Is("wall completion would strand construction or a colonist");
            return false;
        }
        internal static bool CanComplete(Frame frame, Pawn worker)
        {
            var map = frame.Map;
            var blocked = new HashSet<IntVec3>(frame.OccupiedRect());
            bool Open(IntVec3 c) => c.InBounds(map) && !blocked.Contains(c) && !c.Fogged(map) && c.Walkable(map)
                && !c.IsForbidden(worker) && (worker.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                    || worker.playerSettings.AreaRestrictionInPawnCurrentMap[c])
                && (!(c.GetEdifice(map) is Building_Door door) || door.PawnCanOpen(worker));
            bool Step(IntVec3 a, IntVec3 b) => Open(b) && (a.x == b.x || a.z == b.z
                || Open(new IntVec3(a.x, 0, b.z)) || Open(new IntVec3(b.x, 0, a.z)));
            // One escape seed: multiple seeds could hide disconnected regions.
            var start = worker.Position;
            if (blocked.Contains(start)) {
                start = GenAdj.AdjacentCells.Select(d => worker.Position + d).OrderBy(c => c.x).ThenBy(c => c.z)
                    .Where(c => Step(worker.Position, c) && c.Standable(map))
                    .Select(c => (IntVec3?)c).FirstOrDefault() ?? IntVec3.Invalid;
                if (!Open(start)) return false;
            }
            var reached = new HashSet<IntVec3> { start };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(start);
            while (queue.Count != 0) {
                var c = queue.Dequeue();
                foreach (var d in GenAdj.AdjacentCells) {
                    var n = c + d;
                    if (Step(c, n) && reached.Add(n)) queue.Enqueue(n);
                }
            }
            foreach (var cell in blocked)
                foreach (var d in GenAdj.AdjacentCells) {
                    var neighbour = cell + d;
                    if (!neighbour.InBounds(map)) continue;
                    foreach (var t in neighbour.GetThingList(map)) {
                        if (t == frame || !UnfinishedWall(t)) continue;
                        if (!GenAdj.AdjacentCells.Any(a => {
                            var approach = neighbour + a;
                            return reached.Contains(approach) && approach.Standable(map) && Step(neighbour, approach);
                        })) return false;
                    }
                }
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned) {
                if (blocked.Contains(pawn.Position)) {
                    if (!GenAdj.AdjacentCells.Any(d => reached.Contains(pawn.Position + d)
                        && (pawn.Position + d).Standable(map) && Step(pawn.Position, pawn.Position + d))) return false;
                } else if (!reached.Contains(pawn.Position)
                    && worker.CanReach(pawn.Position, PathEndMode.OnCell, Danger.Deadly)) return false;
            }
            return true;
        }
    }
}
