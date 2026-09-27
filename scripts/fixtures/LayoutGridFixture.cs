using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Issue #607: the layout/grid case proves the tiered colony layout on
    // the tribal baseline. Prepare finishes Stonecutting so the build tier
    // reads Masonry, stages the starter hut (FixtureHut) whose south-west
    // corner the controller fixes the colony grid on, and drops wood beside
    // its door; the field the controller then plans is the case's own.
    // Audit reads every finished player wall ring and growing zone back
    // with their cells so the case checks both footprints against the grid,
    // and every wall, door, blueprint and frame with its stuff, so the case
    // reads the tier's wall stuff and door def back natively (#637).
    public sealed class LayoutGridFixture
    {
        [Tool("test/layout_grid_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture: finish the named research (default Stonecutting) with its prerequisites, build one roofed wood hut with sleepingSpots sleeping spots, drop wood and stoneBlocks blocks of the map's own stone beside its door and move every colonist inside. Plans no field: the field planner sites its own.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "ResearchProjectDef to finish (default Stonecutting).")] string project = "Stonecutting",
            [ToolParameter(Description = "Sleeping spots to lay in the hut; negative lays one per colonist, fewer leaves a bed deficit the capacity goal plans against.")] int sleepingSpots = -1,
            [ToolParameter(Description = "Stone blocks of the map's own stone to drop beside the door; 0 drops none.")] int stoneBlocks = 0,
            [ToolParameter(Description = "South-west corner x of the 9x9 hut the controller's starter search chose (required).")] int siteX = -1,
            [ToolParameter(Description = "South-west corner z of the hut.")] int siteZ = -1,
            [ToolParameter(Description = "Door cell x on the hut's ring; negative puts the door mid east wall.")] int doorX = -1,
            [ToolParameter(Description = "Door cell z on the hut's ring.")] int doorZ = -1,
            [ToolParameter(Description = "Bedroom start (#838): enable Construction on every able colonist, lay wooden beds instead of sleeping spots, give each colonist one, drop 120 survival meals and raise every shell blueprint at once.")] bool builders = false)
        {
            var name = string.IsNullOrEmpty(project) ? "Stonecutting" : project;
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                var def = DefDatabase<ResearchProjectDef>.GetNamed(name);
                Finish(def);
                var hut = FixtureHut.Build(map, 9, FixtureHut.Site(siteX, siteZ), FixtureHut.Site(doorX, doorZ), sleepingSpots, builders ? "Bed" : "SleepingSpot");
                FixtureHut.DropOutside(map, hut, ThingDefOf.WoodLog, 4 * ThingDefOf.WoodLog.stackLimit);
                // A bedroom case (#838) starts once everyone owns a hut bed,
                // with food for the stage to leave Foothold.
                var owned = 0;
                if (builders) {
                    var spots = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Where(b => b.GetRoom() == hut.Room).ToList();
                    for (int i = 0; i < spots.Count && i < hut.People.Count; i++)
                        if (spots[i].CompAssignableToPawn.CanAssignTo(hut.People[i]).Accepted) { spots[i].CompAssignableToPawn.TryAssignPawn(hut.People[i]); owned++; }
                    FixtureHut.DropOutside(map, hut, ThingDefOf.MealSurvivalPack, 120);
                }
                if (builders) {
                    // A planned bedroom is furnished with a real Bed, which needs
                    // ComplexFurniture; a sleeping spot never counts as suitable.
                    var furniture = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("ComplexFurniture");
                    if (furniture != null && !furniture.IsFinished) Find.ResearchManager.FinishProject(furniture, doCompletionDialog: false, researcher: null, doCompletionLetter: false);
                    ArmInstantShells();
                }
                var enabled = 0;
                if (builders) foreach (var p in map.mapPawns.FreeColonistsSpawned) {
                    if (p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) || p.workSettings.GetPriority(WorkTypeDefOf.Construction) > 0) continue;
                    p.workSettings.SetPriority(WorkTypeDefOf.Construction, 3);
                    enabled++;
                }
                ThingDef blocks = null;
                if (stoneBlocks > 0) {
                    blocks = StoneBlocks(map);
                    FixtureHut.DropOutside(map, hut, blocks, stoneBlocks);
                }
                return new { success = true, project = name, finished = def.IsFinished, hut = hut.Summary(),
                    hutOrigin = new { x = hut.Origin.x, z = hut.Origin.z }, hutSize = 9,
                    sleepingSpots = hut.SleepingSpots, colonists = hut.People.Count,
                    stoneBlocks = blocks == null ? 0 : stoneBlocks, stoneBlocksDef = blocks?.defName, builders = enabled, ownedBeds = owned,
                    tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        // StoneBlocks is the block definition of the map's own stone, the
        // stuff a Masonry shell is built from: the first natural rock type of
        // the tile, granite where the tile names none.
        private static ThingDef StoneBlocks(Map map)
        {
            foreach (var rock in Find.World.NaturalRockTypesIn(map.Tile)) {
                var blocks = DefDatabase<ThingDef>.GetNamedSilentFail("Blocks" + rock.defName);
                if (blocks != null) return blocks;
            }
            var granite = DefDatabase<ThingDef>.GetNamedSilentFail("BlocksGranite");
            if (granite == null) throw new InvalidOperationException("No stone block def available in this ruleset.");
            return granite;
        }

        // InstantShells (#838): once armed, every player wall, door, autodoor
        // or embrasure blueprint is raised through the game's own path
        // (blueprint to frame, materials in, CompleteConstruction by a
        // colonist) within a tick of being placed, so a bedroom case watches
        // the planner's shell plan complete without minutes of hauling.
        private static bool instantShells, instantHook;

        private static void ArmInstantShells()
        {
            instantShells = true;
            if (instantHook) return;
            new Harmony("rimgovernor.fixture.instant-shells").Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)),
                postfix: new HarmonyMethod(typeof(LayoutGridFixture), nameof(RaiseShells)));
            instantHook = true;
        }

        private static void RaiseShells()
        {
            if (!instantShells) return;
            var map = Find.CurrentMap;
            if (map == null) return;
            var worker = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Dead);
            if (worker == null) return;
            var pending = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).OfType<Blueprint_Build>()
                .Where(b => b.Faction == Faction.OfPlayer && b.def.entityDefToBuild is ThingDef d && (Shell(d.defName) || d.defName == "Bed")).ToList();
            foreach (var blueprint in pending) {
                if (!blueprint.TryReplaceWithSolidThing(worker, out var solid, out _) || !(solid is Frame frame)) continue;
                foreach (var cost in frame.TotalMaterialCost()) {
                    var need = cost.count - frame.resourceContainer.TotalStackCountOfDef(cost.thingDef);
                    if (need <= 0) continue;
                    var stack = ThingMaker.MakeThing(cost.thingDef);
                    stack.stackCount = need;
                    frame.resourceContainer.TryAdd(stack, true);
                }
                frame.CompleteConstruction(worker);
            }
            // Vanilla marks an enclosed room for roofing and waits on a builder;
            // the planner sites beds only under a roof, so roof it at once too.
            foreach (var cell in map.areaManager.BuildRoof.ActiveCells.ToList())
                if (!cell.Roofed(map)) map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
            // A cold night would read every bed unsafe (below the comfort band)
            // and hand the case to the temperature family; hold rooms mild.
            foreach (var room in map.regionGrid.AllRooms)
                if (!room.UsesOutdoorTemperature) room.Temperature = 21f;
        }

        [Tool("test/layout_grid_audit", Description = "Private read-only fixture: every finished player wall, door, autodoor or embrasure cell with its stuff, every such blueprint and frame with the stuff it is to be built from, and every growing zone with its cells and crop.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) return new { success = false, reason = "A disposable colony map is required." };
                var walls = map.listerBuildings.allBuildingsColonist
                    .Where(b => Shell(b.def.defName))
                    .Select(b => new { x = b.Position.x, z = b.Position.z, def = b.def.defName, stuff = b.Stuff?.defName }).ToList();
                // A ring still under construction reads back the same way: the
                // stuff a blueprint or frame carries is the stuff the wall
                // becomes, so the tier's style is visible before the last wall
                // is finished.
                var planned = map.listerThings.AllThings
                    .Where(t => t.Spawned && (t.Faction == null || t.Faction.IsPlayer) && (t is Blueprint_Build || t is Frame))
                    .Select(t => new { thing = t, built = ((t as Blueprint_Build)?.def.entityDefToBuild ?? (t as Frame)?.def.entityDefToBuild) as ThingDef })
                    .Where(row => row.built != null && Shell(row.built.defName))
                    .Select(row => new { x = row.thing.Position.x, z = row.thing.Position.z, def = row.built.defName,
                        stuff = ((row.thing as Blueprint_Build)?.stuffToUse ?? (row.thing as Frame)?.Stuff)?.defName,
                        frame = row.thing is Frame }).ToList();
                var zones = map.zoneManager.AllZones.OfType<Zone_Growing>()
                    .Select(z => new { id = z.ID, loadId = z.GetUniqueLoadID(), label = z.label, crop = z.GetPlantDefToGrow()?.defName,
                        cells = z.Cells.Select(c => new { x = c.x, z = c.z }).ToList() }).ToList();
                return new { success = true, tick = Find.TickManager.TicksGame, walls, planned, zones };
            }, cancellationToken).ConfigureAwait(false);
        }

        // Shell names the definitions a room boundary is built from.
        private static bool Shell(string defName) =>
            defName == "Wall" || defName == "Door" || defName == "Autodoor" || defName == "Embrasure";

        private static void Finish(ResearchProjectDef project)
        {
            if (project.IsFinished) return;
            if (project.prerequisites != null) foreach (var p in project.prerequisites) Finish(p);
            Find.ResearchManager.FinishProject(project, false);
        }
    }
}
