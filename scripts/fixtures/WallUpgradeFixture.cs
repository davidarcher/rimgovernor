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
        [Tool("test/wall_material_loss", Description = "Remove or restore declared stone stock in a disposable wall scenario. Tests demolition safety under material loss; not production acceptance.")]
        public async Task<object> Materials(IRimBridgeContext ctx, CancellationToken cancellationToken, string material, int restore = 0)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var def = DefDatabase<ThingDef>.GetNamed(material);
                if (def.stuffProps?.categories?.Contains(StuffCategoryDefOf.Stony) != true || restore < 0 || restore > 1000)
                    return new { success = false, error = "Expected bounded stone material input" };
                var stock = map.listerThings.ThingsOfDef(def).ToList();
                var count = stock.Sum(t => t.stackCount);
                if (restore == 0) foreach (var thing in stock) thing.Destroy();
                else {
                    var pawn = map.mapPawns.FreeColonistsSpawned.First();
                    for (int remaining = restore; remaining > 0;) {
                        var thing = ThingMaker.MakeThing(def); thing.stackCount = Math.Min(remaining, def.stackLimit);
                        remaining -= thing.stackCount;
                        GenPlace.TryPlaceThing(thing, pawn.Position, map, ThingPlaceMode.Near);
                        thing.SetForbidden(false, false);
                    }
                }
                return new { success = true, before = count, after = map.listerThings.ThingsOfDef(def).Sum(t => t.stackCount) };
            }, cancellationToken).ConfigureAwait(false);
        [Tool("test/stonecutting_prerequisite", Description = "Supply the native stonecutter research prerequisite after the missing-research refusal has been observed. Fixture input, not research labor acceptance.")]
        public async Task<object> Research(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var projects = DefDatabase<ThingDef>.GetNamed("TableStonecutter").researchPrerequisites;
                foreach (var project in projects) if (!project.IsFinished) Find.ResearchManager.FinishProject(project, false);
                return new { success = projects.All(p => p.IsFinished), projects = projects.Select(p => p.defName).ToList() };
            }, cancellationToken).ConfigureAwait(false);
        [Tool("test/stone_walls_spawn", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: stand finished player stone Walls of one stuff at exact empty cells (\"x,z;x,z\"), standing in for completed backup or permanent walls so guarded demolition can be exercised without construction time.")]
        public async Task<object> SpawnWalls(IRimBridgeContext ctx, CancellationToken cancellationToken, string cells, string stuff)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var def = DefDatabase<ThingDef>.GetNamedSilentFail(stuff ?? "");
                if (map == null || def?.stuffProps?.categories?.Contains(StuffCategoryDefOf.Stony) != true || !GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Contains(def))
                    return new { success = false, error = "A current map and a stony Wall stuff are required" };
                var targets = (cells ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split(',')).ToList();
                if (targets.Count == 0 || targets.Count > 8 || targets.Any(p => p.Length != 2)) return new { success = false, error = "Expected 1..8 x,z cells" };
                var parsed = targets.Select(p => new IntVec3(int.Parse(p[0]), 0, int.Parse(p[1]))).ToList();
                if (parsed.Any(c => !c.InBounds(map) || c.GetEdifice(map) != null || c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame)))
                    return new { success = false, error = "Every cell must be in bounds and free of buildings" };
                var ids = new System.Collections.Generic.List<string>();
                foreach (var cell in parsed) {
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();
                    var wall = ThingMaker.MakeThing(ThingDefOf.Wall, def);
                    wall.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(wall, cell, map);
                    ids.Add(wall.GetUniqueLoadID());
                }
                return new { success = true, walls = ids, stuff = def.defName };
            }, cancellationToken).ConfigureAwait(false);
        [Tool("test/stone_upgrade_setup", Description = "Prepare empty exterior backup cells, stone chunks and enabled workers beside a disposable controller-built room. No blocks, workbench, bill or demolition are created.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken, string walls, bool corner = false)
            => await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var ids = walls.Split(';');
                foreach (var wall in map.listerBuildings.allBuildingsColonist.Where(b => ids.Contains(b.GetUniqueLoadID()) && b.def == ThingDefOf.Wall)) {
                    foreach (var normal in WallUpgradeSafety.Directions.Where(n => WallUpgradeSafety.Corner(n) == corner)) {
                        var inside = wall.Position - normal; var outside = wall.Position + normal;
                        var cells = WallUpgradeSafety.BackupCells(wall.Position, normal).ToList();
                        var staging = cells.Concat(corner ? WallUpgradeSafety.CornerApproaches(wall.Position, normal) : Enumerable.Empty<IntVec3>()).ToList();
                        if (!inside.InBounds(map) || inside.Fogged(map) || inside.GetRoom(map)?.OpenRoofCount != 0
                            || !inside.Standable(map)
                            || inside.GetRoom(map).TouchesMapEdge || !outside.InBounds(map) || outside.GetRoom(map)?.TouchesMapEdge != true
                            || WallUpgradeSafety.LeftCell(wall.Position, normal).GetEdifice(map)?.def != ThingDefOf.Wall
                            || WallUpgradeSafety.RightCell(wall.Position, normal).GetEdifice(map)?.def != ThingDefOf.Wall
                            || RoofSupportSafety.Blocker(wall, out _) != null
                            || staging.Any(c => !c.InBounds(map) || c.Fogged(map) || !c.Standable(map)
                                || cells.Contains(c) && !RoofSupportSafety.GeometryKnown(map, c)
                                || map.zoneManager.ZoneAt(c) != null || c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame))) continue;
                        foreach (var cell in staging)
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
                            var raw = ThingMaker.MakeThing(cost.thingDef); raw.stackCount = cost.count * 3;
                            GenPlace.TryPlaceThing(raw, drop, map, ThingPlaceMode.Near); raw.SetForbidden(false, false);
                        }
                        foreach (var p in map.mapPawns.FreeColonistsSpawned) {
                            foreach (var name in new[] { "Construction", "Crafting", "Hauling" }) {
                                var work = DefDatabase<WorkTypeDef>.GetNamed(name);
                                if (!p.WorkTypeIsDisabled(work)) p.workSettings.SetPriority(work, 1);
                            }
                            p.needs.rest.CurLevelPercentage = .95f; p.needs.food.CurLevelPercentage = .95f;
                        }
                        return new { success = true, target = wall.GetUniqueLoadID(), stone = stone.defName, corner, backupCount = cells.Count,
                            chunks = chunk.defName, initialBlocks = map.listerThings.ThingsOfDef(stone).Sum(t => t.stackCount),
                            benchMaterials = benchCosts.ToDictionary(c => c.thingDef.defName, c => c.count * 3),
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
