using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class WallUpgradeFixture
    {
        [Tool("test/stonecutting_prerequisite", Description = "Supply the native stonecutter research prerequisite after the missing-research refusal has been observed. Fixture input, not research labor acceptance.")]
        public async Task<object> Research(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var projects = DefDatabase<ThingDef>.GetNamed("TableStonecutter").researchPrerequisites;
                foreach (var project in projects) if (!project.IsFinished) Find.ResearchManager.FinishProject(project, false);
                return new { success = projects.All(p => p.IsFinished), projects = projects.Select(p => p.defName).ToList() };
            }, cancellationToken).ConfigureAwait(false);
        [Tool("test/stone_upgrade_setup", Description = "Prepare empty exterior backup cells, stone chunks and enabled workers beside a disposable controller-built room. No blocks, workbench, bill or demolition are created.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken, string walls)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var ids = walls.Split(';');
                foreach (var wall in map.listerBuildings.allBuildingsColonist.Where(b => ids.Contains(b.GetUniqueLoadID()) && b.def == ThingDefOf.Wall)) {
                    foreach (var normal in GenAdj.CardinalDirections) {
                        var side = new IntVec3(-normal.z, 0, normal.x);
                        var inside = wall.Position - normal; var outside = wall.Position + normal;
                        var cells = new[] { outside - side, outside, outside + side };
                        if (!inside.InBounds(map) || inside.Fogged(map) || inside.GetRoom(map)?.OpenRoofCount != 0
                            || inside.GetRoom(map).TouchesMapEdge || !outside.InBounds(map) || outside.GetRoom(map)?.TouchesMapEdge != true
                            || (wall.Position - side).GetEdifice(map)?.def != ThingDefOf.Wall
                            || (wall.Position + side).GetEdifice(map)?.def != ThingDefOf.Wall
                            || RoofSupportSafety.Blocker(wall, out _) != null
                            || cells.Any(c => !c.InBounds(map) || c.Fogged(map) || !c.Standable(map)
                                || map.zoneManager.ZoneAt(c) != null || c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame))) continue;
                        foreach (var cell in cells)
                            foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();
                        var stone = GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Where(s => s.stuffProps.categories.Contains(StuffCategoryDefOf.Stony))
                            .OrderBy(s => s.defName).First();
                        var chunk = DefDatabase<ThingDef>.AllDefsListForReading.First(d => d.butcherProducts?.Any(p => p.thingDef == stone) == true);
                        var drop = GenRadial.RadialCellsAround(outside + normal * 4, 5, true).First(c => c.InBounds(map)
                            && c.Standable(map) && !cells.Contains(c) && !c.Fogged(map));
                        for (int i = 0; i < 4; i++) {
                            var thing = ThingMaker.MakeThing(chunk);
                            GenPlace.TryPlaceThing(thing, drop, map, ThingPlaceMode.Near);
                            thing.SetForbidden(false, false);
                        }
                        var benchCosts = DefDatabase<ThingDef>.GetNamed("TableStonecutter").CostListAdjusted(ThingDefOf.WoodLog);
                        foreach (var cost in benchCosts) {
                            var raw = ThingMaker.MakeThing(cost.thingDef); raw.stackCount = cost.count;
                            GenPlace.TryPlaceThing(raw, drop, map, ThingPlaceMode.Near); raw.SetForbidden(false, false);
                        }
                        foreach (var p in map.mapPawns.FreeColonistsSpawned) {
                            foreach (var name in new[] { "Construction", "Crafting", "Hauling" }) {
                                var work = DefDatabase<WorkTypeDef>.GetNamed(name);
                                if (!p.WorkTypeIsDisabled(work)) p.workSettings.SetPriority(work, 1);
                            }
                            p.needs.rest.CurLevelPercentage = .95f; p.needs.food.CurLevelPercentage = .95f;
                        }
                        return new { success = true, target = wall.GetUniqueLoadID(), stone = stone.defName,
                            chunks = chunk.defName, initialBlocks = map.listerThings.ThingsOfDef(stone).Sum(t => t.stackCount),
                            benchMaterials = benchCosts.ToDictionary(c => c.thingDef.defName, c => c.count),
                            x = wall.Position.x, z = wall.Position.z, nx = normal.x, nz = normal.z };
                    }
                }
                return new { success = false, error = "No disposable room wall has fully observed backup space" };
            }, cancellationToken).ConfigureAwait(false);

        [Tool("test/wall_enclosure", Description = "Observe the disposable room's original interior during replacement.")]
        public async Task<object> Enclosure(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z, int nx, int nz)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var cell = new IntVec3(x - nx, 0, z - nz); var room = cell.GetRoom(map);
                return new { success = true, enclosed = room != null && !room.TouchesMapEdge,
                    fullyRoofed = room != null && room.OpenRoofCount == 0,
                    pendingCollapse = map.roofCollapseBuffer.IsMarkedToCollapse(cell) };
            }, cancellationToken).ConfigureAwait(false);
    }
}
