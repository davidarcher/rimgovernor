#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace HomeBridge.BridgeTools;

// Private disposable acceptance only (#3273): one open patch holding nothing but a wood stack, one standing wall and
// one deep-water cell. The planned-site read must estimate unplaced walls, a door and a shelf from native work and
// paths, and reject the wall and water cells with native placement reasons. Nothing is placed or ordered.
public sealed partial class PawnControlFixture
{
    private static IntVec3 plannedOrigin;

    [Tool("test/planned_site_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Clear one open 10x10 patch near a builder; spawn a wood stack, one wood wall and one deep-water cell.")]
    public async Task<object> PlannedSitePrepare(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("Load a colony first.");
            var player = Faction.OfPlayer;
            var builder = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                .FirstOrDefault(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                ?? throw new InvalidOperationException("No colonist able to construct.");
            const int width = 10, height = 10;
            plannedOrigin = GenRadial.RadialCellsAround(builder.Position, 40, true).FirstOrDefault(c =>
                new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                    && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !cell.GetTerrain(map).IsWater
                    && cell.GetTerrain(map).passability != Traversability.Impassable
                    && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building))
                && builder.CanReach(c, PathEndMode.Touch, Danger.None));
            if (plannedOrigin == default) throw new InvalidOperationException("No open reachable 10x10 patch.");
            foreach (var cell in new CellRect(plannedOrigin.x, plannedOrigin.z, width, height).Cells)
            {
                foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category is ThingCategory.Item or ThingCategory.Filth).ToList()) thing.Destroy();
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
            var shelf = ThingDef.Named("Shelf");
            foreach (var project in shelf.researchPrerequisites ?? new List<ResearchProjectDef>())
                if (!project.IsFinished) Find.ResearchManager.FinishProject(project);
            var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
            wall.SetFaction(player);
            GenSpawn.Spawn(wall, At(4, 5), map);
            map.terrainGrid.SetTerrain(At(7, 5), DefDatabase<TerrainDef>.GetNamed("WaterDeep"));
            var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
            wood.stackCount = wood.def.stackLimit;
            GenSpawn.Spawn(wood, At(9, 3), map);
            return new { success = true, origin = new { x = plannedOrigin.x, z = plannedOrigin.z }, wood = wood.GetUniqueLoadID() };
        }, ct).ConfigureAwait(false);

    private static IntVec3 At(int x, int z) => new(plannedOrigin.x + x, 0, plannedOrigin.z + z);

    [Tool("test/planned_site_read", Description = "UNSAFE FOR MODEL EXECUTION. Read planned walls, a door, a shelf and two illegal sites on the prepared patch and check native work, routes and legality.")]
    public async Task<object> PlannedSiteRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (plannedOrigin == default) throw new InvalidOperationException("Prepare planned_site first.");
            var map = Find.CurrentMap;
            var identity = Context().Identity!;
            Cell C(int x, int z) { var cell = At(x, z); return new(cell.x, cell.z); }
            Obs.PlannedConstructionSite Site(string id, string def, params Cell[] cells) =>
                new(id, def, "WoodLog", RimGovernor.Brain.Domain.Rotation.North, RimGovernor.Brain.Domain.EquatableArray.From(cells));
            var legal = new[] {
                Site("walls", "Wall", C(1, 1), C(2, 1), C(3, 1), C(4, 1), C(5, 1)),
                Site("door", "Door", C(7, 1)),
                Site("shelf", "Shelf", C(1, 3)) };
            var illegal = new[] { Site("on-wall", "Wall", C(4, 5)), Site("on-water", "Wall", C(7, 5)) };
            // One site per read: the caller batches, and a large wood census must not starve a later site's rows here.
            Obs.PlannedSiteAnswer Read(Obs.PlannedConstructionSite site, out Obs.ConstructionActivityRead read)
            {
                read = NativeConstructionActivity.Read(new(identity, default) { Planned = [site] });
                return read.Planned.SingleOrDefault(a => a.Site == site.Id) ?? throw new InvalidOperationException("Planned site " + site.Id + " has no answer.");
            }
            var report = new List<object>();
            foreach (var site in legal)
            {
                var answer = Read(site, out var read);
                if (!answer.CanPlace.IsKnown || !answer.CanPlace.Value)
                    throw new InvalidOperationException("Open-ground site " + site.Id + " was not placeable: " + string.Join("; ", answer.Rejections.Select(r => r.Reason)) + " findings " + string.Join(",", read.Findings));
                var definition = ThingDef.Named(site.Definition);
                var work = definition.GetStatValueAbstract(StatDefOf.WorkToBuild, ThingDefOf.WoodLog) * site.Positions.Count;
                var complete = answer.Options.Where(o => o.ApproachTicks.IsKnown && o.DeliveryTicks.IsKnown && o.FinishTicks.IsKnown && o.SiteApproachTicks.IsKnown).ToArray();
                if (complete.Length == 0)
                    throw new InvalidOperationException("Site " + site.Id + " has no row with finite approach, delivery and finish ticks; findings " + string.Join(",", read.Findings));
                foreach (var option in complete)
                {
                    var pawn = map.mapPawns.FreeColonistsSpawned.Single(p => p.GetUniqueLoadID() == option.Pawn);
                    var expected = work / pawn.GetStatValue(StatDefOf.ConstructionSpeed);
                    if (Math.Abs(option.FinishTicks.Value - expected) > Math.Max(1.0, expected * 1e-4)
                        || !double.IsFinite(option.ApproachTicks.Value) || option.ApproachTicks.Value < 0
                        || !double.IsFinite(option.DeliveryTicks.Value) || option.DeliveryTicks.Value < 0)
                        throw new InvalidOperationException("Site " + site.Id + " row for " + option.Pawn + " disagrees with native work " + expected + ": " + option);
                }
                report.Add(new { site = site.Id, rows = answer.Options.Count, complete = complete.Length, work, finish = complete[0].FinishTicks.Value,
                    approach = complete[0].ApproachTicks.Value, delivery = complete[0].DeliveryTicks.Value, blockers = answer.Blockers.Count });
            }
            // #3274: the standing colony wall is removable but not estimable; deep water is illegal.
            var verdicts = new[] { Obs.PlannedSiteVerdict.Unestimable, Obs.PlannedSiteVerdict.Illegal };
            for (var i = 0; i < illegal.Length; i++)
            {
                var site = illegal[i];
                var answer = Read(site, out _);
                if (!answer.CanPlace.IsKnown || answer.CanPlace.Value || answer.Rejections.Count == 0)
                    throw new InvalidOperationException("Site " + site.Id + " should be known and rejected with a reason.");
                if (answer.Verdict != verdicts[i])
                    throw new InvalidOperationException("Site " + site.Id + " verdict " + answer.Verdict + ", expected " + verdicts[i] + ": " + string.Join("; ", answer.Rejections));
                report.Add(new { site = site.Id, canPlace = false, verdict = answer.Verdict.ToString(), reasons = answer.Rejections.Select(r => r.Reason).ToArray(),
                    blockers = answer.Blockers.Select(b => b.Definition + ":" + b.Verdict).ToArray() });
            }
            return new { success = true, sites = report };
        }, ct).ConfigureAwait(false);

    // #3274: spawn and read in one main-thread call, so no colonist hauls, cuts or mines between the two.
    [Tool("test/planned_site_clearing", Description = "UNSAFE FOR MODEL EXECUTION. On the prepared patch's free rows spawn grass and a grown tree, a steel stack beside a small stockpile, and two natural rock cells; read planned walls over each and check Cut, Haul and Mine ops against native work.")]
    public async Task<object> PlannedSiteClearing(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (plannedOrigin == default) throw new InvalidOperationException("Prepare planned_site first.");
            var map = Find.CurrentMap;
            var identity = Context().Identity!;
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var x in new[] { 0, 1, 2 }) zone.AddCell(At(x, 9));
            var grass = (Plant)ThingMaker.MakeThing(ThingDef.Named("Plant_Grass"));
            grass.Growth = 1f;
            GenSpawn.Spawn(grass, At(0, 7), map);
            var tree = (Plant)ThingMaker.MakeThing(ThingDef.Named("Plant_TreeOak"));
            tree.Growth = 1f;
            GenSpawn.Spawn(tree, At(1, 7), map);
            var steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 30;
            GenSpawn.Spawn(steel, At(4, 7), map);
            var granite = DefDatabase<ThingDef>.GetNamed("Granite");
            var rock = new[] { At(6, 7), At(7, 7) }.Select(c => GenSpawn.Spawn(ThingMaker.MakeThing(granite), c, map)).ToArray();
            // #3336: the paused lab never advances the tick, and PathFinderMapData gathers once per tick: the read's routes
            // would path over the data gathered before this spawn (stale connectivity after the rock's region rebuild) and
            // find nothing. Ticking would let a colonist start the clearing; dirty the data the way a fogged map does instead.
            HarmonyLib.AccessTools.Method(typeof(PathFinderMapData), "Notify_MapDirtied").Invoke(map.pathFinder.MapData, null);
            Cell C(int x, int z) { var cell = At(x, z); return new(cell.x, cell.z); }
            Obs.PlannedSiteAnswer Read(string id, params Cell[] cells)
            {
                var read = NativeConstructionActivity.Read(new(identity, default)
                    { Planned = [new(id, "Wall", "WoodLog", RimGovernor.Brain.Domain.Rotation.North, RimGovernor.Brain.Domain.EquatableArray.From(cells))] });
                var answer = read.Planned.SingleOrDefault(a => a.Site == id) ?? throw new InvalidOperationException("Planned site " + id + " has no answer.");
                if (answer.Verdict != Obs.PlannedSiteVerdict.Feasible)
                    throw new InvalidOperationException("Site " + id + " verdict " + answer.Verdict + "; rejections " + string.Join("; ", answer.Rejections)
                        + "; blockers " + string.Join("; ", answer.Blockers) + "; findings " + string.Join(",", read.Findings));
                return answer;
            }
            Pawn PawnOf(string id) => map.mapPawns.FreeColonistsSpawned.Single(p => p.GetUniqueLoadID() == id);
            Obs.PlannedSiteClearing Op(Obs.PlannedSiteAnswer answer, Obs.PlannedClearingKind kind, Thing thing)
            {
                var op = answer.Clearing.SingleOrDefault(c => c.Kind == kind) ?? throw new InvalidOperationException("Site " + answer.Site + " has no " + kind + " op: " + string.Join("; ", answer.Clearing));
                var blocker = answer.Blockers.SingleOrDefault(b => b.Thing == thing.GetUniqueLoadID());
                if (blocker == null || blocker.Verdict != Obs.PlannedBlockerVerdict.Cleared || blocker.Clearing != op.Id)
                    throw new InvalidOperationException("Blocker " + thing.GetUniqueLoadID() + " is not cleared by " + op.Id + ": " + blocker);
                if (!op.Options.Any(o => o.FinishTicks.IsKnown && o.SiteApproachTicks.IsKnown))
                    throw new InvalidOperationException("Op " + op.Id + " has no row with finite approach and finish: " + string.Join("; ", op.Options));
                return op;
            }
            void Agree(Obs.PlannedSiteClearing op, Func<Pawn, double> expected)
            {
                foreach (var row in op.Options.Where(o => o.FinishTicks.IsKnown))
                {
                    var value = expected(PawnOf(row.Pawn));
                    if (!double.IsFinite(row.FinishTicks.Value) || Math.Abs(row.FinishTicks.Value - value) > Math.Max(1.0, value * 1e-4))
                        throw new InvalidOperationException("Op " + op.Id + " row for " + row.Pawn + " disagrees with native work " + value + ": " + row);
                }
            }
            var plants = Read("on-plants", C(0, 7), C(1, 7));
            var cut = Op(plants, Obs.PlannedClearingKind.Cut, tree);
            Agree(cut, pawn => cut.Cells.Contains(C(0, 7))
                ? tree.def.plant.harvestWork / JobDriver_PlantWork.WorkDonePerTick(pawn, tree) + grass.def.plant.harvestWork / JobDriver_PlantWork.WorkDonePerTick(pawn, grass)
                : tree.def.plant.harvestWork / JobDriver_PlantWork.WorkDonePerTick(pawn, tree));
            var stack = Read("on-stack", C(4, 7));
            var haul = Op(stack, Obs.PlannedClearingKind.Haul, steel);
            if (!haul.Options.Any(o => o.ApproachTicks.IsKnown && o.DeliveryTicks.IsKnown && o.Count == steel.stackCount && o.Input == steel.GetUniqueLoadID()))
                throw new InvalidOperationException("Haul op " + haul.Id + " has no finite pickup and carry row for the whole stack: " + string.Join("; ", haul.Options));
            var mountain = Read("in-rock", C(6, 7), C(7, 7));
            var mine = Op(mountain, Obs.PlannedClearingKind.Mine, rock[0]);
            Agree(mine, pawn => rock.Sum(r => Math.Ceiling(r.HitPoints / 80.0)) * Math.Round(JobDriver_Mine.BaseTicksBetweenPickHits / pawn.GetStatValue(StatDefOf.MiningSpeed)));
            object Summary(Obs.PlannedSiteAnswer answer) => new { site = answer.Site, canPlace = answer.CanPlace.IsKnown ? answer.CanPlace.Value.ToString() : "unknown",
                ops = answer.Clearing.Select(c => new { c.Id, kind = c.Kind.ToString(), cells = c.Cells.Count, rows = c.Options.Count,
                    finish = c.Options.Where(o => o.FinishTicks.IsKnown).Select(o => o.FinishTicks.Value).DefaultIfEmpty(-1).First() }).ToArray(),
                blockers = answer.Blockers.Select(b => b.Definition + ":" + b.Verdict).ToArray() };
            return new { success = true, sites = new[] { Summary(plants), Summary(stack), Summary(mountain) } };
        }, ct).ConfigureAwait(false);
}
