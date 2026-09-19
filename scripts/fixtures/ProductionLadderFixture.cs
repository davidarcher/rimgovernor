using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Issue #4 M4: the multi-stage production ladder (research -> bench ->
    // ingredient storage -> bill) proved on a save whose workshop room already
    // stands. Prepare seeds what the ladder does not build itself: ingredients
    // on the ground, a research bench, and Smithing research a few points
    // short of done so the derived EnsureResearch target finishes within a
    // minute-scale watch. Audit reads the same native state back.
    public sealed class ProductionLadderFixture
    {
        const string Project = "Smithing";

        [Tool("test/production_ladder_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: drop steel and wood near the colonists, spawn a simple research bench, and advance Smithing research to 97% of its base cost (IsFinished compares real progress to baseCost; a tribal colony still owes the tech-level factor on the rest).")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.First();
                var benchDef = ThingDef.Named("SimpleResearchBench");
                var benchCell = GenRadial.RadialCellsAround(pawn.Position, 20, true).FirstOrDefault(c => c.InBounds(map) && !c.Fogged(map)
                    && GenConstruct.CanPlaceBlueprintAt(benchDef, c, Rot4.North, map).Accepted
                    && GenAdj.OccupiedRect(c, Rot4.North, benchDef.size).Cells.All(cell => cell.Standable(map) && cell.GetFirstBuilding(map) == null));
                if (!benchCell.IsValid) throw new InvalidOperationException("No research bench site.");
                var bench = ThingMaker.MakeThing(benchDef, GenStuff.DefaultStuffFor(benchDef));
                bench.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(bench, benchCell, map, Rot4.North);
                int Drop(ThingDef def, int count)
                {
                    var placed = 0;
                    while (placed < count) {
                        var stack = Math.Min(def.stackLimit, count - placed);
                        var thing = ThingMaker.MakeThing(def); thing.stackCount = stack;
                        if (!GenPlace.TryPlaceThing(thing, pawn.Position, map, ThingPlaceMode.Near)) throw new InvalidOperationException("No drop site for " + def.defName);
                        placed += stack;
                    }
                    return placed;
                }
                var steel = Drop(ThingDefOf.Steel, 150);
                var wood = Drop(ThingDefOf.WoodLog, 150);
                var project = DefDatabase<ResearchProjectDef>.GetNamed(Project);
                var manager = Find.ResearchManager;
                var progress = typeof(ResearchManager).GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as Dictionary<ResearchProjectDef, float>;
                if (progress == null) throw new InvalidOperationException("ResearchManager.progress unavailable.");
                progress[project] = project.baseCost * 0.97f;
                return new { success = true, researchBench = bench.GetUniqueLoadID(), steel, wood, project = Project,
                    progress = project.ProgressPercent, finished = project.IsFinished, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        // #231: the stonecutting variant. Prepare seeds the research bench,
        // Stonecutting at 97% and the steel a stonecutter's table costs (the
        // tribal save holds none); the blocks must come from the chunks the
        // map already holds, so it also reports the stone chunks in reach.
        [Tool("test/production_stone_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: spawn a simple research bench near the colonists, drop the steel a stonecutter's table costs, advance Stonecutting research to 97% of its base cost, and count the unforbidden stone chunks within 40 cells of the colonists by definition.")]
        public async Task<object> PrepareStone(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var pawn = map.mapPawns.FreeColonistsSpawned.First();
                var benchDef = ThingDef.Named("SimpleResearchBench");
                var benchCell = GenRadial.RadialCellsAround(pawn.Position, 20, true).FirstOrDefault(c => c.InBounds(map) && !c.Fogged(map)
                    && GenConstruct.CanPlaceBlueprintAt(benchDef, c, Rot4.North, map).Accepted
                    && GenAdj.OccupiedRect(c, Rot4.North, benchDef.size).Cells.All(cell => cell.Standable(map) && cell.GetFirstBuilding(map) == null));
                if (!benchCell.IsValid) throw new InvalidOperationException("No research bench site.");
                var bench = ThingMaker.MakeThing(benchDef, GenStuff.DefaultStuffFor(benchDef));
                bench.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(bench, benchCell, map, Rot4.North);
                var steel = 0;
                while (steel < 60) {
                    var thing = ThingMaker.MakeThing(ThingDefOf.Steel); thing.stackCount = 60 - steel;
                    if (!GenPlace.TryPlaceThing(thing, pawn.Position, map, ThingPlaceMode.Near)) throw new InvalidOperationException("No drop site for Steel");
                    steel += thing.stackCount;
                }
                var project = DefDatabase<ResearchProjectDef>.GetNamed(StoneProject);
                var manager = Find.ResearchManager;
                var progress = typeof(ResearchManager).GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as Dictionary<ResearchProjectDef, float>;
                if (progress == null) throw new InvalidOperationException("ResearchManager.progress unavailable.");
                progress[project] = project.baseCost * 0.97f;
                return new { success = true, researchBench = bench.GetUniqueLoadID(), steel, project = StoneProject,
                    progress = project.ProgressPercent, finished = project.IsFinished, chunks = StoneChunks(map, pawn.Position), tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/production_stone_audit", Description = "Private read-only fixture: Stonecutting state, stonecutter's tables with their room role and bills, live stone block counts by definition, and the stone chunks within 40 cells of the colonists.")]
        public async Task<object> AuditStone(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("No current map.");
                var project = DefDatabase<ResearchProjectDef>.GetNamed(StoneProject);
                string RoleAt(IntVec3 cell) => cell.GetRoom(map)?.Role?.defName;
                var tables = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>()
                    .Where(b => b.def.defName == "TableStonecutter")
                    .Select(b => new { thingId = b.GetUniqueLoadID(), x = b.Position.x, z = b.Position.z, roomRole = RoleAt(b.Position),
                        bills = b.BillStack.Bills.Select(bill => bill.recipe.defName).ToArray() }).ToArray();
                var blocks = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsStuff && d.stuffProps?.categories != null && d.stuffProps.categories.Contains(StuffCategoryDefOf.Stony) && d.defName.StartsWith("Blocks"))
                    .Select(d => new { defName = d.defName, count = map.listerThings.ThingsOfDef(d).Where(t => t.Spawned).Sum(t => t.stackCount) })
                    .Where(row => row.count > 0).OrderBy(row => row.defName, StringComparer.Ordinal).ToArray();
                var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                return new { success = true, project = StoneProject, finished = project.IsFinished, progress = project.ProgressPercent,
                    current = Find.ResearchManager.GetProject()?.defName, tables, blocks,
                    chunks = pawn == null ? new object[0] : StoneChunks(map, pawn.Position), tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        const string StoneProject = "Stonecutting";

        static object[] StoneChunks(Map map, IntVec3 center) => map.listerThings.AllThings
            .Where(t => t.Spawned && !t.IsForbidden(Faction.OfPlayer) && !t.Position.Fogged(map) && t.def.thingCategories != null
                && t.def.thingCategories.Contains(ThingCategoryDefOf.StoneChunks) && t.Position.InHorDistOf(center, 40f))
            .GroupBy(t => t.def.defName).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (object)new { defName = g.Key, count = g.Sum(t => t.stackCount) }).ToArray();

        [Tool("test/production_ladder_audit", Description = "Private read-only fixture: Smithing state, smithies with their room role and bills, stockpile zones with their room role and steel allowance, and the live gladius count.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("No current map.");
                var project = DefDatabase<ResearchProjectDef>.GetNamed(Project);
                string RoleAt(IntVec3 cell) => cell.GetRoom(map)?.Role?.defName;
                var smithies = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>()
                    .Where(b => b.def.defName == "FueledSmithy" || b.def.defName == "ElectricSmithy")
                    .Select(b => new { thingId = b.GetUniqueLoadID(), defName = b.def.defName, x = b.Position.x, z = b.Position.z, roomRole = RoleAt(b.Position),
                        powered = b.TryGetComp<CompPowerTrader>()?.PowerOn, fuel = b.TryGetComp<CompRefuelable>()?.Fuel,
                        bills = b.BillStack.Bills.Select(bill => bill.recipe.defName).ToArray() }).ToArray();
                var stockpiles = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Select(z => new { id = z.ID, label = z.label, cells = z.Cells.Count,
                    roomRole = z.Cells.Count > 0 ? RoleAt(z.Cells[0]) : null, priority = z.settings.Priority.ToString(),
                    allowsSteel = z.settings.filter.Allows(ThingDefOf.Steel), allowedCount = z.settings.filter.AllowedDefCount,
                    steelStored = z.AllContainedThings.Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount) }).ToArray();
                var gladius = ThingDef.Named("MeleeWeapon_Gladius");
                var ground = map.listerThings.ThingsOfDef(gladius).Count(t => t.Spawned);
                var carried = map.mapPawns.FreeColonistsSpawned.Count(p => p.equipment?.Primary?.def == gladius);
                var researchers = map.mapPawns.FreeColonistsSpawned.Where(p => p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Research)).Select(p => p.LabelShort).ToArray();
                return new { success = true, project = Project, finished = project.IsFinished, progress = project.ProgressPercent,
                    current = Find.ResearchManager.GetProject()?.defName, researchers,
                    researchBenches = map.listerBuildings.allBuildingsColonist.OfType<Building_ResearchBench>().Count(),
                    smithies, stockpiles, gladiusGround = ground, gladiusCarried = carried, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
