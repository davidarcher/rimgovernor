using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // the multi-stage production ladder (research -> bench ->
    // ingredient storage -> bill) proved on the Core tribal baseline. Prepare
    // stages what the ladder does not build itself: the room the bench rung
    // furnishes (FixtureHut: a roofed wood hut with a sleeping spot per
    // colonist, so the initial shelter is met and the ladder has clean floor
    // for the bench and its ingredient stockpile), a simple research bench
    // inside it, ingredients beside its door, and Fabrication research a few
    // points short of done so the derived EnsureResearch target finishes
    // within a minute-scale watch. Audit reads the same native state
    // back.
    public sealed class ProductionLadderFixture
    {
        const string Project = "Fabrication";

        // HutSize is the ring the production fixtures stage: 9x9 inside, so
        // eight sleeping spots, the research bench, a fabrication bench or stonecutter's
        // table and an ingredient stockpile all fit without the workshop
        // ladder siting a second shell.
        const int HutSize = 11;

        [Tool("test/production_ladder_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: build one roofed wood hut with a sleeping spot per colonist with a simple research bench and a fueled wood-fired generator inside, move every colonist in, drop steel, wood and the fabrication bench's 12 components beside its door, finish Fabrication's prerequisites and advance Fabrication research to 97% of its base cost (IsFinished compares real progress to baseCost; a tribal colony still owes the tech-level factor on the rest).")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, [ToolParameter(Description = "South-west corner x of the hut the controller's starter search chose (required).")] int siteX = -1, [ToolParameter(Description = "South-west corner z of the hut.")] int siteZ = -1, [ToolParameter(Description = "Door cell x on the hut's ring; negative puts the door mid east wall.")] int doorX = -1, [ToolParameter(Description = "Door cell z on the hut's ring.")] int doorZ = -1)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var hut = FixtureHut.Build(map, HutSize, FixtureHut.Site(siteX, siteZ), FixtureHut.Site(doorX, doorZ));
                var bench = FixtureHut.SpawnInside(map, hut, ThingDef.Named("SimpleResearchBench"));
                // The fabrication bench costs steel and 12 components, a
                // component bill steel; the bench ladder may pay wood. The
                // tribal baseline holds none of them. The components lie loose
                // outside storage, so the native stock (ResourceCounter) reads
                // 0 against the default floor of 10.
                var steel = FixtureHut.DropOutside(map, hut, ThingDefOf.Steel, 300);
                var wood = FixtureHut.DropOutside(map, hut, ThingDefOf.WoodLog, 150);
                FixtureHut.DropOutside(map, hut, ThingDef.Named("ComponentIndustrial"), 12);
                // The fabrication bench draws power; the generator is its source.
                var generator = FixtureHut.SpawnInside(map, hut, ThingDef.Named("WoodFiredGenerator"));
                generator.TryGetComp<CompRefuelable>().Refuel(1000f);
                void Finish(ResearchProjectDef p) {
                    if (p.IsFinished) return;
                    if (p.prerequisites != null) foreach (var prerequisite in p.prerequisites) Finish(prerequisite);
                    Find.ResearchManager.FinishProject(p, false);
                }
                foreach (var prerequisite in DefDatabase<ResearchProjectDef>.GetNamed(Project).prerequisites ?? new List<ResearchProjectDef>()) Finish(prerequisite);
                var project = Advance(Project);
                return new { success = true, researchBench = bench.GetUniqueLoadID(), steel, wood, project = Project,
                    progress = project.ProgressPercent, finished = project.IsFinished, hut = hut.Summary(), tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        // Advance sets the named project to 97% of its base cost.
        static ResearchProjectDef Advance(string name)
        {
            var project = DefDatabase<ResearchProjectDef>.GetNamed(name);
            var progress = typeof(ResearchManager).GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Find.ResearchManager) as Dictionary<ResearchProjectDef, float>;
            if (progress == null) throw new InvalidOperationException("ResearchManager.progress unavailable.");
            progress[project] = project.baseCost * 0.97f;
            return project;
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

        [Tool("test/production_ladder_audit", Description = "Private read-only fixture: Fabrication state, fabrication benches with their room role and bills, stockpile zones with their room role and steel allowance, and the stored component count.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("No current map.");
                var project = DefDatabase<ResearchProjectDef>.GetNamed(Project);
                string RoleAt(IntVec3 cell) => cell.GetRoom(map)?.Role?.defName;
                var benches = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>()
                    .Where(b => b.def.defName == "FabricationBench")
                    .Select(b => new { thingId = b.GetUniqueLoadID(), defName = b.def.defName, x = b.Position.x, z = b.Position.z, roomRole = RoleAt(b.Position),
                        powered = b.TryGetComp<CompPowerTrader>()?.PowerOn, fuel = b.TryGetComp<CompRefuelable>()?.Fuel,
                        bills = b.BillStack.Bills.Select(bill => bill.recipe.defName).ToArray() }).ToArray();
                var stockpiles = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Select(z => new { id = z.ID, label = z.label, cells = z.Cells.Count,
                    roomRole = z.Cells.Count > 0 ? RoleAt(z.Cells[0]) : null, priority = z.settings.Priority.ToString(),
                    allowsSteel = z.settings.filter.Allows(ThingDefOf.Steel), allowedCount = z.settings.filter.AllowedDefCount,
                    steelStored = z.AllContainedThings.Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount) }).ToArray();
                var components = map.resourceCounter.GetCount(ThingDef.Named("ComponentIndustrial"));
                var researchers = map.mapPawns.FreeColonistsSpawned.Where(p => p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Research)).Select(p => p.LabelShort).ToArray();
                return new { success = true, project = Project, finished = project.IsFinished, progress = project.ProgressPercent,
                    current = Find.ResearchManager.GetProject()?.defName, researchers,
                    researchBenches = map.listerBuildings.allBuildingsColonist.OfType<Building_ResearchBench>().Count(),
                    benches, stockpiles, components, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
