using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class ResourceAcquisitionTools
    {
        private static ThingDef Product(Thing t) => t is Plant p ? p.def.plant.harvestedThingDef : t is Mineable ? t.def.building.mineableThing : null;
        private static bool Designated(Thing t) => t is Mineable
            ? t.Map.designationManager.DesignationAt(t.Position, DesignationDefOf.Mine) != null
            : t.Map.designationManager.DesignationOn(t, DesignationDefOf.HarvestPlant) != null
                || t.Map.designationManager.DesignationOn(t, DesignationDefOf.CutPlant) != null;
        private static bool Eligible(Thing t, Map map)
        {
            if (!t.Spawned || t.Position.Fogged(map) || t.IsForbidden(Faction.OfPlayer) || Product(t) == null) return false;
            if (t is Plant plant && (!plant.HarvestableNow || map.zoneManager.ZoneAt(t.Position) is Zone_Growing)) return false;
            return (t is Plant || t is Mineable) && map.mapPawns.FreeColonistsSpawned.Any(p => !p.Downed && !p.Drafted
                && !p.InMentalState && p.Position.DistanceTo(t.Position) <= 50 && p.CanReach(t, PathEndMode.Touch, Danger.None));
        }
        [Tool("home/resource_sources", Title = "Reachable native resource sources",
            Description = "Up to 40 visible nearby safely reachable native mining or mature wild-plant sources for an exact output resource. Normal yields are estimates; pawn work must produce actual stock. Existing growing zones are excluded.")]
        public async Task<object> Sources(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact native output ThingDef")] string resource)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || DefDatabase<ThingDef>.GetNamedSilentFail(resource) == null)
                    return new { success = false, error = "Unknown resource or no map" };
                var rows = map.listerThings.AllThings.Where(t => Product(t)?.defName == resource && Eligible(t, map))
                    .OrderBy(t => t.thingIDNumber).Take(40).Select(t => new { thingId = t.ThingID,
                        resource, workTypes = new[] { HomeBillsTools.WorkTypeMetadata(
                            t is Mineable ? WorkTypeDefOf.Mining : t.def.plant.IsTree ? WorkTypeDefOf.PlantCutting : WorkTypeDefOf.Growing) },
                        x = t.Position.x, z = t.Position.z, designated = Designated(t),
                        method = t is Mineable ? "mine" : t.def.plant.IsTree ? "cut" : "harvest",
                        yield = t is Plant plant ? plant.YieldNow() : t.def.building.mineableYield }).ToList();
                return new { success = true, resource, sources = rows, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }
        [Tool("home/acquire_resource", Title = "Designate exact native resource source",
            Description = "Designate one observed mature wild plant or mineable through normal player designators. Exact resource, ThingID, position and colony/load/map required. Native eligibility and a safe reachable colonist are rechecked. Does not harvest, mine, spawn stock or certify labor.")]
        public async Task<object> Acquire(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact colony ID")] string colonyId,
            [ToolParameter(Description = "Exact load token")] string loadToken,
            [ToolParameter(Description = "Exact map ID")] int mapId,
            [ToolParameter(Description = "Exact source ThingID")] string thingId,
            [ToolParameter(Description = "Exact output resource definition")] string resource,
            [ToolParameter(Description = "Observed x")] int x,
            [ToolParameter(Description = "Observed z")] int z,
            [ToolParameter(Description = "Preview only", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (map == null || identity == null || identity.ColonyId != colonyId || identity.LoadToken != loadToken
                    || map.uniqueID != mapId || Find.TickManager.CurTimeSpeed != TimeSpeed.Paused || DebugSettings.godMode)
                    return new { success = false, error = "Paused normal-game colony/load/map required" };
                var thing = map.listerThings.AllThings.FirstOrDefault(t => t.ThingID == thingId);
                if (thing == null || thing.Position.x != x || thing.Position.z != z || Product(thing)?.defName != resource || !Eligible(thing, map))
                    return new { success = false, error = "Resource source changed or is unsafe/unavailable" };
                if (Designated(thing)) return new { success = true, dryRun, designated = true, thingId, resource };
                Designator designator = thing is Mineable ? (Designator)new Designator_Mine() :
                    thing.def.plant.IsTree ? new Designator_PlantsCut() : new Designator_PlantsHarvest();
                var verdict = designator.CanDesignateThing(thing);
                if (!verdict.Accepted) return new { success = false, error = verdict.Reason };
                if (!dryRun) designator.DesignateThing(thing);
                return new { success = dryRun || Designated(thing), dryRun, designated = Designated(thing), thingId, resource };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
