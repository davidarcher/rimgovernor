#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    internal static class ResourceAcquisitionTools
    {
        internal static ThingDef? Product(Thing t) => t is Plant p ? p.def.plant.harvestedThingDef : t is Mineable ? t.def.building.mineableThing : null;
        internal static bool Designated(Thing t) => t is Mineable
            ? t.Map.designationManager.DesignationAt(t.Position, DesignationDefOf.Mine) != null
            : t.Map.designationManager.DesignationOn(t, DesignationDefOf.HarvestPlant) != null
                || t.Map.designationManager.DesignationOn(t, DesignationDefOf.CutPlant) != null;
        // Census designation age (#1043): the tick a designation was first
        // seen, per thing, dropped when the designation goes. Not persisted:
        // a new Game (load) starts it empty, so designations read fresh.
        private static Game? designationGame;
        private static readonly Dictionary<string, int> designationSeen = new Dictionary<string, int>();
        internal static long? DesignatedTick(Thing t, bool designated)
        {
            if (!ReferenceEquals(designationGame, Current.Game)) { designationSeen.Clear(); designationGame = Current.Game; }
            var id = t.GetUniqueLoadID();
            if (!designated) { designationSeen.Remove(id); return null; }
            if (!designationSeen.TryGetValue(id, out var tick)) designationSeen[id] = tick = Find.TickManager.TicksGame;
            return tick;
        }
        // Taken (#1043): any pawn's reservation, or a colonist's current job, targets the thing.
        internal static bool Taken(Thing t) => Taken(t, null);
        // reserved, when given, is the map's reserved things read once for a
        // whole census instead of per row (#1295).
        internal static bool Taken(Thing t, HashSet<Thing>? reserved) => (reserved != null ? reserved.Contains(t) : t.Map.reservationManager.AllReservedThings().Contains(t))
            || t.Map.mapPawns.FreeColonistsSpawned.Any(p => p.CurJob is Job job && (job.targetA.Thing == t || job.targetB.Thing == t || job.targetC.Thing == t
                || job.targetQueueA?.Any(q => q.Thing == t) == true || job.targetQueueB?.Any(q => q.Thing == t) == true));
        internal static Designator DesignatorFor(Thing t) => t is Mineable ? (Designator)new Designator_Mine() :
            t.def.plant.IsTree ? new Designator_PlantsHarvestWood() : new Designator_PlantsHarvest();
        internal static bool Eligible(Thing t, Map map)
        {
            if (!t.Spawned || t.Position.Fogged(map) || t.IsForbidden(Faction.OfPlayer) || Product(t) == null) return false;
            if (t is Plant plant && (!plant.HarvestableNow || map.zoneManager.ZoneAt(t.Position) is Zone_Growing)) return false;
            if (t is Mineable && MiningBlocker(t, map) != null) return false;
            var work = t is Mineable ? WorkTypeDefOf.Mining : WorkTypeDefOf.PlantCutting;
            return (t is Plant || t is Mineable) && (Designated(t) || DesignatorFor(t).CanDesignateThing(t).Accepted) && map.mapPawns.FreeColonistsSpawned.Any(p => !p.Downed && !p.Drafted
                && !p.InMentalState && !p.WorkTypeIsDisabled(work) && !t.IsForbidden(p)
                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                && (t is Mineable || p.Position.DistanceTo(t.Position) <= 50) && p.CanReach(t, PathEndMode.Touch, Danger.None));
        }

        // A deposit may be mined under roof when it is not the last holder of
        // any roof cell (#986). The check reads the true map through fog, as
        // the game does for collapse. A pending collapse is transient and
        // map-wide, so it is a wait (CollapsePending), never a blocker, and
        // support is judged only while none is pending (MineSafetyRule). Ore
        // is always mineable (#1133): a building, blueprint or frame beside
        // the rock does not protect it. The roof rule covers what its removal
        // can bring down, and ReplaceWall walls what it opens.
        internal static string? MiningBlocker(Thing t, Map map)
        {
            if (t.Faction != null) return "Faction-owned extraction target is protected";
            if (!CollapsePending(map) && ExcavationSafety.Check(map, new[] { t.Position }, out _, out _, throughFog: true) != ExcavationSafety.Support.Supported)
                return "Roof support requires a supported excavation plan";
            if (GenAdj.CellsAdjacent8WayAndInside(t).Any(cell => !cell.InBounds(map))) return "Map edge excavation is protected";
            return null;
        }

        internal static bool CollapsePending(Map map) => map.roofCollapseBuffer.CellsMarkedToCollapse.Count > 0;

        // A mined cell that borders a zone or Home opens protected colony
        // space, so the mine is followed by an ordinary wall blueprint on it
        // in the most-stocked wall material (#1133).
        internal static bool OpensProtectedSpace(IntVec3 cell, Map map) => GenAdj.CellsAdjacent8Way(new TargetInfo(cell, map)).Concat(new[] { cell })
            .Any(c => c.InBounds(map) && (map.zoneManager.ZoneAt(c) != null || map.areaManager.Home[c]));
        internal static void ReplaceWall(IntVec3 cell, Map map)
        {
            if (!OpensProtectedSpace(cell, map)) return;
            var allowed = GenStuff.AllowedStuffsFor(ThingDefOf.Wall).ToList();
            var stuff = allowed.OrderByDescending(s => map.resourceCounter.GetCount(s)).FirstOrDefault(s => map.resourceCounter.GetCount(s) > 0)
                ?? GenStuff.DefaultStuffFor(ThingDefOf.Wall);
            if (!GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, cell, Rot4.North, map, false, null, null, stuff).Accepted) return;
            GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, cell, map, Rot4.North, Faction.OfPlayer, stuff);
        }

        // Buried ore (#1072): a mineable deposit whose roof support holds and
        // that no colonist can reach, usually because it sits in fog. It is
        // not mined directly; Go tunnels a corridor to it first, and the
        // excavation re-checks support per cell at dispatch.
        internal static bool Buried(Thing t, Map map) => t is Mineable && t.Spawned && !t.IsForbidden(Faction.OfPlayer)
            && Product(t) != null && MiningBlocker(t, map) == null
            && !map.mapPawns.FreeColonistsSpawned.Any(p => p.CanReach(t, PathEndMode.Touch, Danger.None));

        // Safety label for an admitted row: mined ore with roof in its support
        // radius is "supported_roof", other ore "open_surface".
        internal static string Safety(Thing t, Map map) => !(t is Mineable) ? "native_eligible"
            : GenRadial.RadialCellsAround(t.Position, RoofCollapseUtility.RoofMaxSupportDistance, true)
                .Any(cell => cell.InBounds(map) && cell.Roofed(map)) ? "supported_roof" : "open_surface";

    }
}
