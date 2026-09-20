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
    // ingredient storage -> bill) proved on the Core tribal baseline. Prepare
    // stages what the ladder does not build itself: the room the bench rung
    // furnishes (FixtureHut: a roofed wood hut with a sleeping spot per
    // colonist, so the initial shelter is met and the ladder has clean floor
    // for the bench and its ingredient stockpile), a simple research bench
    // inside it, ingredients beside its door, and Smithing research a few
    // points short of done so the derived EnsureResearch target finishes
    // within a minute-scale watch (#344). Audit reads the same native state
    // back.
    public sealed class ProductionLadderFixture
    {
        const string Project = "Smithing";

        // HutSize is the ring the production fixtures stage: 9x9 inside, so
        // eight sleeping spots, the research bench, a smithy or stonecutter's
        // table and an ingredient stockpile all fit without the workshop
        // ladder siting a second shell (#218).
        const int HutSize = 11;

        [Tool("test/production_ladder_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: build one roofed wood hut with a sleeping spot per colonist and a simple research bench inside, move every colonist in, drop steel and wood beside its door, and advance Smithing research to 97% of its base cost (IsFinished compares real progress to baseCost; a tribal colony still owes the tech-level factor on the rest).")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var hut = FixtureHut.Build(map, HutSize);
                var bench = FixtureHut.SpawnInside(map, hut, ThingDef.Named("SimpleResearchBench"));
                // The smithy and the gladius cost steel; the bench ladder may
                // pay wood. The tribal baseline holds neither.
                var steel = FixtureHut.DropOutside(map, hut, ThingDefOf.Steel, 150);
                var wood = FixtureHut.DropOutside(map, hut, ThingDefOf.WoodLog, 150);
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

        // #231: the stonecutting variant. Prepare stages the same hut and
        // research bench, Stonecutting at 97% and the steel a stonecutter's
        // table costs (the tribal save holds none); the blocks must come from
        // the chunks the map already holds, so it also reports the stone
        // chunks in reach.
        [Tool("test/production_stone_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: build one roofed wood hut with a sleeping spot per colonist and a simple research bench inside, move every colonist in, drop the steel a stonecutter's table costs beside its door, advance Stonecutting research to 97% of its base cost, and count the unforbidden stone chunks within 40 cells of the colonists by definition.")]
        public async Task<object> PrepareStone(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var hut = FixtureHut.Build(map, HutSize);
                var bench = FixtureHut.SpawnInside(map, hut, ThingDef.Named("SimpleResearchBench"));
                var steel = FixtureHut.DropOutside(map, hut, ThingDefOf.Steel, 60);
                var project = Advance(StoneProject);
                return new { success = true, researchBench = bench.GetUniqueLoadID(), steel, project = StoneProject,
                    progress = project.ProgressPercent, finished = project.IsFinished, chunks = StoneChunks(map, hut.People[0].Position),
                    hut = hut.Summary(), tick = Find.TickManager.TicksGame };
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

        [Tool("test/production_materials_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage an exhausted surface, powered workshop, completed research and either a seeded steel lump plus scanner or a fabrication bench; no drill or bill is supplied.")]
        public async Task<object> PrepareMaterials(IRimBridgeContext ctx, CancellationToken cancellationToken, string scenario = "deepdrill")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                if (scenario != "deepdrill" && scenario != "components") throw new InvalidOperationException("Unknown materials scenario.");
                var component = ThingDef.Named("ComponentIndustrial");
                bool Material(Thing t) => t.def == ThingDefOf.Steel || t.def == component || t.def.defName == "Plasteel";
                // Remove every alternative mineral source, including fogged ore,
                // slag and carried stock. The only newly produced steel is deep.
                foreach (var t in map.listerThings.AllThings.Where(t => t is Mineable && t.def.building.mineableThing != null || Material(t) || t.def.defName == "ChunkSlagSteel").ToList()) t.Destroy(DestroyMode.Vanish);
                foreach (var pawn in map.mapPawns.AllPawnsSpawned) {
                    foreach (var t in pawn.inventory.innerContainer.Where(Material).ToList()) t.Destroy(DestroyMode.Vanish);
                    if (pawn.carryTracker.CarriedThing != null && Material(pawn.carryTracker.CarriedThing)) pawn.carryTracker.CarriedThing.Destroy(DestroyMode.Vanish);
                }
                foreach (var cell in map.AllCells) map.deepResourceGrid.SetAt(cell, null, 0);
                var hut = FixtureHut.Build(map, HutSize);
                void Finish(ResearchProjectDef project) {
                    if (project.IsFinished) return;
                    if (project.prerequisites != null) foreach (var prerequisite in project.prerequisites) Finish(prerequisite);
                    Find.ResearchManager.FinishProject(project, false);
                }
                Finish(DefDatabase<ResearchProjectDef>.GetNamed(scenario == "deepdrill" ? "GroundPenetratingScanner" : "Fabrication"));
                var generator = FixtureHut.SpawnInside(map, hut, ThingDef.Named("WoodFiredGenerator"));
                generator.TryGetComp<CompRefuelable>().Refuel(1000f);
                var end = generator.Position;
                if (scenario == "components") {
                    FixtureHut.SpawnInside(map, hut, ThingDef.Named("FabricationBench"));
                } else {
                    var origin = GenRadial.RadialCellsAround(hut.Door, 24, true).FirstOrDefault(c =>
                        new CellRect(c.x, c.z, 9, 7).Cells.All(x => x.InBounds(map) && !x.Fogged(map) && x.Standable(map) && x.GetEdifice(map) == null && x.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                    if (origin == default) throw new InvalidOperationException("No clear scanner/lump site beside hut.");
                    foreach (var cell in new CellRect(origin.x, origin.z, 9, 7).Cells) {
                        map.roofGrid.SetRoof(cell, null);
                        foreach (var plant in cell.GetThingList(map).OfType<Plant>().ToList()) plant.Destroy(DestroyMode.Vanish);
                        map.areaManager.Home[cell] = true;
                    }
                    var scanner = ThingMaker.MakeThing(ThingDefOf.GroundPenetratingScanner);
                    scanner.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(scanner, origin + new IntVec3(1, 0, 1), map);
                    end = origin + new IntVec3(6, 0, 3);
                    foreach (var cell in CellRect.CenteredOn(end, 1).Cells) map.deepResourceGrid.SetAt(cell, ThingDefOf.Steel, 300);
                }
                // A funded ordinary network already reaches the future drill.
                // No power flags, work progress, bills or drill are fabricated.
                var wireCells = hut.Interior.Concat(GenAdj.OccupiedRect(generator.Position, generator.Rotation, generator.def.size).Cells).ToList();
                var cursor = generator.Position;
                while (cursor.x != end.x) { wireCells.Add(cursor); cursor.x += Math.Sign(end.x - cursor.x); }
                while (cursor.z != end.z) { wireCells.Add(cursor); cursor.z += Math.Sign(end.z - cursor.z); }
                wireCells.Add(end);
                foreach (var cell in wireCells.Distinct())
                    if (!cell.GetThingList(map).Any(t => t.def == ThingDef.Named("HiddenConduit"))) {
                        var wire = ThingMaker.MakeThing(ThingDef.Named("HiddenConduit")); wire.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(wire, cell, map);
                    }
                foreach (var pawn in hut.People) {
                    pawn.skills.GetSkill(SkillDefOf.Construction).Level = 15;
                    pawn.skills.GetSkill(SkillDefOf.Mining).Level = 15;
                    pawn.skills.GetSkill(SkillDefOf.Crafting).Level = 15;
                    pawn.workSettings.EnableAndInitialize();
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                        if (!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work, work == WorkTypeDefOf.Construction || work == WorkTypeDefOf.Mining || work == WorkTypeDefOf.Crafting ? 1 : 0);
                }
                FixtureHut.DropOutside(map, hut, ThingDefOf.Steel, scenario == "deepdrill" ? 150 : 600);
                FixtureHut.DropOutside(map, hut, component, scenario == "deepdrill" ? 10 : 0);
                map.powerNetManager.UpdatePowerNetsAndConnections_First();
                // Pair with the harness's synthetic, cancelled history plan;
                // this advances the fixture date, not simulation or production.
                Find.TickManager.DebugSetTicksGame(Math.Max(120000, Find.TickManager.TicksGame));
                return new { success = true, scenario, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/production_materials_audit", Description = "Read-only material stock, deep steel remaining, drill/lump overlap, scanner, fabrication bench and MakeComponent bill census.")]
        public async Task<object> AuditMaterials(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) throw new InvalidOperationException("No current map.");
                int Stock(ThingDef def) => map.listerThings.ThingsOfDef(def).Where(t => t.Spawned).Sum(t => t.stackCount)
                    + map.mapPawns.AllPawnsSpawned.Sum(p => p.inventory.innerContainer.Where(t => t.def == def).Sum(t => t.stackCount)
                        + (p.carryTracker.CarriedThing?.def == def ? p.carryTracker.CarriedThing.stackCount : 0));
                var drills = map.listerBuildings.allBuildingsColonist.Where(b => b.def.defName == "DeepDrill").ToArray();
                var benches = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().Where(b => b.def.defName == "FabricationBench").ToArray();
                return new { success = true, tick = Find.TickManager.TicksGame, steel = Stock(ThingDefOf.Steel), components = Stock(ThingDef.Named("ComponentIndustrial")),
                    surfaceOre = map.listerThings.AllThings.Count(t => t is Mineable && t.def.building.mineableThing != null),
                    deepSteel = map.AllCells.Where(c => map.deepResourceGrid.ThingDefAt(c) == ThingDefOf.Steel).Sum(c => map.deepResourceGrid.CountAt(c)),
                    drills = drills.Length, drillsOnLump = drills.Count(b => b.OccupiedRect().Cells.Any(c => map.deepResourceGrid.ThingDefAt(c) == ThingDefOf.Steel)),
                    scanners = map.listerBuildings.allBuildingsColonist.Count(b => b.def == ThingDefOf.GroundPenetratingScanner), benches = benches.Length,
                    componentBills = benches.Sum(b => b.BillStack.Bills.Count(bill => bill.recipe.defName == "MakeComponent")),
                    researched = DefDatabase<ResearchProjectDef>.GetNamed(benches.Length > 0 ? "Fabrication" : "GroundPenetratingScanner").IsFinished };
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
