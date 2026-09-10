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
    public sealed class ResourceAcquisitionTools
    {
        public ResourceAcquisitionTools() { MiningGuard.Install(); }
        private static ThingDef Product(Thing t) => t is Plant p ? p.def.plant.harvestedThingDef : t is Mineable ? t.def.building.mineableThing : null;
        private static bool Designated(Thing t) => t is Mineable
            ? t.Map.designationManager.DesignationAt(t.Position, DesignationDefOf.Mine) != null
            : t.Map.designationManager.DesignationOn(t, DesignationDefOf.HarvestPlant) != null
                || t.Map.designationManager.DesignationOn(t, DesignationDefOf.CutPlant) != null;
        private static Designator DesignatorFor(Thing t) => t is Mineable ? (Designator)new Designator_Mine() :
            t.def.plant.IsTree ? new Designator_PlantsHarvestWood() : new Designator_PlantsHarvest();
        private static bool Eligible(Thing t, Map map)
        {
            if (!t.Spawned || t.Position.Fogged(map) || t.IsForbidden(Faction.OfPlayer) || Product(t) == null) return false;
            if (t is Plant plant && (!plant.HarvestableNow || map.zoneManager.ZoneAt(t.Position) is Zone_Growing)) return false;
            if (t is Mineable && MiningBlocker(t, map) != null) return false;
            var work = t is Mineable ? WorkTypeDefOf.Mining : WorkTypeDefOf.PlantCutting;
            return (t is Plant || t is Mineable) && (Designated(t) || DesignatorFor(t).CanDesignateThing(t).Accepted) && map.mapPawns.FreeColonistsSpawned.Any(p => !p.Downed && !p.Drafted
                && !p.InMentalState && !p.WorkTypeIsDisabled(work) && !t.IsForbidden(p)
                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                && p.Position.DistanceTo(t.Position) <= 50 && p.CanReach(t, PathEndMode.Touch, Danger.None));
        }

        // Only surface excavation is certified. Never infer support from a partial
        // map read or remove a roof holder whose influence includes any roof.
        internal static string MiningBlocker(Thing t, Map map)
        {
            if (t.Faction != null) return "Faction-owned extraction target is protected";
            foreach (var cell in GenRadial.RadialCellsAround(t.Position, RoofCollapseUtility.RoofMaxSupportDistance, true))
            {
                if (!cell.InBounds(map) || cell.Fogged(map)) return "Unknown excavation geometry";
                if (cell.Roofed(map) || map.roofCollapseBuffer.IsMarkedToCollapse(cell)) return "Roof support requires a supported excavation plan";
            }
            foreach (var cell in GenAdj.CellsAdjacent8WayAndInside(t))
            {
                if (!cell.InBounds(map)) return "Map edge excavation is protected";
                if (map.zoneManager.ZoneAt(cell) != null || map.areaManager.Home[cell]) return "Excavation overlaps protected colony space";
                if (cell.GetThingList(map).Any(other => other != t && (other is Blueprint || other is Frame
                    || (other is Building && !(other is Mineable))))) return "Excavation borders a protected structure";
            }
            return null;
        }

        private static object ExtractionInfrastructure(Map map, string resource, bool development)
        {
            var definitions = DefDatabase<ThingDef>.AllDefs.Where(d => d.CompDefFor<CompDeepDrill>() != null
                || d.CompDefFor<CompDeepScanner>() != null).OrderBy(d => d.defName).Select(d => new {
                    defName = d.defName, method = d.CompDefFor<CompDeepDrill>() != null ? "drill" : "scan",
                    constructionSkill = d.constructionSkillPrerequisite, artisticSkill = d.artisticSkillPrerequisite,
                    research = (d.researchPrerequisites ?? new List<ResearchProjectDef>()).Select(r => new { defName = r.defName, finished = r.IsFinished }).ToList(),
                    available = d.researchPrerequisites == null || d.researchPrerequisites.All(r => r.IsFinished),
                    costs = d.MadeFromStuff ? null : d.CostListAdjusted(null, false).ToDictionary(c => c.thingDef.defName, c => c.count)
                }).ToList();
            var scannersActive = map.deepResourceGrid.AnyActiveDeepScannersOnMap();
            var scanners = map.listerBuildings.allBuildingsColonist.Where(b => b.GetComp<CompDeepScanner>() != null)
                .Select(b => new { thingId = b.ThingID, defName = b.def.defName, x = b.Position.x, z = b.Position.z,
                    available = b.GetComp<CompDeepScanner>().CanUseNow.Accepted,
                    forbidden = b.IsForbidden(Faction.OfPlayer), powered = b.GetComp<CompPowerTrader>()?.PowerOn == true }).ToList();
            var deposits = scannersActive ? map.AllCells.Where(c => !c.Fogged(map)
                && map.deepResourceGrid.ThingDefAt(c)?.defName == resource && map.deepResourceGrid.CountAt(c) > 0)
                .Take(40).Select(c => new { x = c.x, z = c.z, remaining = map.deepResourceGrid.CountAt(c) }).ToList() : null;
            var drills = map.listerBuildings.allBuildingsColonist.Where(b => b.GetComp<CompDeepDrill>() != null)
                .Select(b => new { thingId = b.ThingID, defName = b.def.defName, x = b.Position.x, z = b.Position.z,
                    resource = DeepDrillUtility.GetNextResource(b.Position, map)?.defName,
                    available = b.GetComp<CompDeepDrill>().CanDrillNow(), forbidden = b.IsForbidden(Faction.OfPlayer),
                    switchOn = (bool?)b.GetComp<CompFlickable>()?.SwitchIsOn,
                    flickDesignated = map.designationManager.DesignationOn(b, DesignationDefOf.Flick) != null,
                    flickWorkers = map.mapPawns.FreeColonistsSpawned.Where(p => ExtractionDevelopment.Worker(p, ExtractionDevelopment.FlickWork)
                        && p.CanReach(b, PathEndMode.Touch, Danger.None)).Select(p => p.ThingID).ToList(),
                    progress = b.GetComp<CompDeepDrill>().ProgressToNextPortionPercent }).ToList();
            return new { definitions, scannersActive, scanners, deposits, drills,
                flickWorkType = ExtractionDevelopment.FlickWork == null ? null : HomeBillsTools.WorkTypeMetadata(ExtractionDevelopment.FlickWork),
                sites = development ? ExtractionDevelopment.Sites(map, resource) : new List<object>(),
                owned = MiningGuard.State().Drills.Where(r => r.MapId == map.uniqueID && r.Resource == resource)
                    .Select(r => new { defName = r.Definition, thingId = r.ThingId, pendingId = r.PendingId, x = r.X, z = r.Z, recovered = r.Recovered,
                        missing = !new IntVec3(r.X, 0, r.Z).GetThingList(map).Any(t =>
                            (r.ThingId != null && t.ThingID == r.ThingId) || (r.PendingId != null && t.ThingID == r.PendingId)),
                        depleted = DeepDrillUtility.GetNextResource(new IntVec3(r.X, 0, r.Z), map)?.defName != resource }).ToList(),
                risk = "Native drill infestations remain enabled; deep development requires explicit player acceptance" };
        }

        private static object Storage(Map map, string resource)
        {
            var def = DefDatabase<ThingDef>.GetNamed(resource);
            var haulers = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState
                && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)).ToList();
            bool Accessible(IntVec3 c) => !c.Fogged(map) && c.Standable(map) && haulers.Any(p => !c.IsForbidden(p)
                && p.CanReach(c, PathEndMode.OnCell, Danger.None));
            bool Protected(IntVec3 c) => def.GetStatValueAbstract(StatDefOf.DeteriorationRate) <= 0 || c.Roofed(map);
            // Count only empty floor slots, conservatively excluding shelves and partial stacks.
            var capacity = map.haulDestinationManager.AllGroups.Where(g => g.Settings.filter.Allows(def))
                .SelectMany(g => g.CellsList).Distinct().Count(c => Accessible(c) && Protected(c)
                    && c.GetEdifice(map) == null && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)) * def.stackLimit;
            var stored = map.haulDestinationManager.AllGroups.SelectMany(g => g.HeldThings
                .Where(t => t.def == def && g.Settings.AllowedToAccept(t))).Distinct().Sum(t => t.stackCount);
            var border = typeof(AutoHomeAreaMaker).GetField("BorderWidth", BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue();
            var knownBorder = border is int width && width >= 0 && width <= 32;
            var margin = knownBorder ? (int)border + 1 : 0;
            var candidates = haulers.Count == 0 || !knownBorder ? new List<IntVec3>() : GenRadial.RadialCellsAround(haulers[0].Position, 20, true)
                .Where(c => c.InBounds(map) && Accessible(c) && Protected(c) && map.zoneManager.ZoneAt(c) == null
                    && !CellRect.CenteredOn(c, margin).Any(q => q.InBounds(map) && q.GetEdifice(map) is Mineable)
                    && !c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame || t.def.category == ThingCategory.Item))
                .Take(8).ToList();
            return new { capacity, stored, stackLimit = def.stackLimit, haulers = haulers.Select(p => p.ThingID).ToList(),
                deepPortion = (int)Math.Ceiling(def.deepCountPerPortion * map.mapPawns.FreeColonistsSpawned
                    .Where(p => ExtractionDevelopment.Worker(p, WorkTypeDefOf.Mining)).Select(p => p.GetStatValue(StatDefOf.MiningYield)).DefaultIfEmpty(1f).Max()),
                candidates = candidates.Select(c => new { x = c.x, z = c.z }).ToList(),
                workType = HomeBillsTools.WorkTypeMetadata(WorkTypeDefOf.Hauling) };
        }
        [Tool("home/resource_sources", Title = "Reachable native resource sources",
            Description = "Up to 40 visible nearby safely reachable native mining or mature wild-plant sources for an exact output resource. Normal yields are estimates; pawn work must produce actual stock. Existing growing zones are excluded.")]
        public async Task<object> Sources(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact native output ThingDef")] string resource,
            [ToolParameter(Description = "Include bounded native extraction facility sites; research, power, worker and access prerequisites are required.")] bool development = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || DefDatabase<ThingDef>.GetNamedSilentFail(resource) == null)
                    return new { success = false, error = "Unknown resource or no map" };
                var deposits = map.listerThings.AllThings.Where(t => Product(t)?.defName == resource && !t.Position.Fogged(map)).ToList();
                var eligible = deposits.Where(t => Eligible(t, map)).ToList();
                float Distance(Thing t) => map.mapPawns.FreeColonistsSpawned.Min(p => p.Position.DistanceTo(t.Position));
                var rows = eligible.OrderByDescending(t => Designated(t)).ThenBy(Distance)
                    .ThenBy(t => t.thingIDNumber).Take(40).Select(t => new { thingId = t.ThingID,
                        sourceId = MiningGuard.State().Records.FirstOrDefault(r => r.MapId == map.uniqueID && r.ThingId == t.ThingID)?.SourceId ?? t.ThingID,
                        resource, workTypes = new[] { HomeBillsTools.WorkTypeMetadata(
                            t is Mineable ? WorkTypeDefOf.Mining : WorkTypeDefOf.PlantCutting) },
                        distance = Distance(t), safety = t is Mineable ? "open_surface" : "native_eligible",
                        x = t.Position.x, z = t.Position.z, designated = Designated(t), hitPoints = t.HitPoints,
                        method = t is Mineable ? "mine" : t.def.plant.IsTree ? "cut" : "harvest",
                        yield = t is Plant plant ? plant.YieldNow() : t.def.building.mineableYield }).ToList();
                var identity = Current.Game?.GetComponent<ColonyIdentity>();
                return new { success = true, resource, sources = rows, tick = Find.TickManager.TicksGame,
                    colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    eligibleCount = eligible.Count, truncated = eligible.Count > 40,
                    infrastructure = ExtractionInfrastructure(map, resource, development),
                    storage = Storage(map, resource),
                    pendingYield = eligible.Where(Designated).Sum(t => t is Plant p ? p.YieldNow() : t.def.building.mineableYield),
                    extractions = MiningGuard.State().Records.Where(r => r.MapId == map.uniqueID && r.Resource == resource)
                        .Select(r => new { thingId = r.ThingId, sourceId = r.SourceId ?? r.ThingId, x = r.X, z = r.Z, started = r.Started,
                            finished = r.Finished, recovered = r.Recovered, blocker = r.Blocker, cancelled = r.Cancelled }).ToList(),
                    blocked = deposits.OfType<Mineable>().Where(t => !Eligible(t, map)).Take(40)
                        .Select(t => new { thingId = t.ThingID, x = t.Position.x, z = t.Position.z,
                            reason = MiningBlocker(t, map) ?? "No eligible safely reachable miner or native designation" }).ToList() };
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
                Designator designator = DesignatorFor(thing);
                var verdict = designator.CanDesignateThing(thing);
                if (!verdict.Accepted) return new { success = false, error = string.IsNullOrEmpty(verdict.Reason) ? "Native resource designation refused" : verdict.Reason };
                if (!dryRun)
                {
                    if (thing is Mineable)
                    {
                        var records = MiningGuard.State().Records;
                        var retained = records.FirstOrDefault(r => r.MapId == map.uniqueID && r.ThingId == thingId);
                        if (retained != null) { retained.Cancelled = false; retained.Blocker = null; }
                        if (!records.Any(r => r.MapId == map.uniqueID && r.ThingId == thingId))
                        {
                            if (records.Count >= 256) records.RemoveAll(r => r.Finished >= 0);
                            if (records.Count >= 256) return new { success = false, error = "Mining ownership capacity requires inspection" };
                            records.Add(new MiningRecord { ThingId = thingId, SourceId = thingId, Definition = thing.def.defName, Resource = resource, MapId = mapId,
                                X = x, Z = z, Started = Find.TickManager.TicksGame });
                        }
                    }
                    designator.DesignateThing(thing);
                }
                return new { success = dryRun || Designated(thing), dryRun, designated = Designated(thing), thingId, resource };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
