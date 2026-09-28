#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
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
        internal static bool Taken(Thing t) => t.Map.reservationManager.AllReservedThings().Contains(t)
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
        // the game does for collapse; a pending collapse nearby still refuses.
        internal static string? MiningBlocker(Thing t, Map map)
        {
            if (t.Faction != null) return "Faction-owned extraction target is protected";
            if (GenRadial.RadialCellsAround(t.Position, RoofCollapseUtility.RoofMaxSupportDistance, true)
                    .Any(cell => cell.InBounds(map) && map.roofCollapseBuffer.IsMarkedToCollapse(cell))
                || ExcavationSafety.Check(map, new[] { t.Position }, out _, out _, throughFog: true) != ExcavationSafety.Support.Supported)
                return "Roof support requires a supported excavation plan";
            foreach (var cell in GenAdj.CellsAdjacent8WayAndInside(t))
            {
                if (!cell.InBounds(map)) return "Map edge excavation is protected";
                if (map.zoneManager.ZoneAt(cell) != null || map.areaManager.Home[cell]) return "Excavation overlaps protected colony space";
                if (cell.GetThingList(map).Any(other => other != t && (other is Blueprint || other is Frame
                    || (other is Building && !(other is Mineable))))) return "Excavation borders a protected structure";
            }
            return null;
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
