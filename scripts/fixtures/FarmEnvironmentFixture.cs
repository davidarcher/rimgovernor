using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Stages the controlled-environment
    // precondition for the farm/select-* cases (issue #3 M4): one enclosed roofed
    // room with a running sun lamp and heaters, its own fuelled wood-fired
    // generators, a cold snap that closes the outdoor growing season, and
    // parkas so the colonists survive it. Two scenarios:
    //
    //   greenhouse  -- soil floor under the lamp and Electricity research
    //                  only, so greenhouse-reuse is the one plantable kind.
    //   hydroponics -- concrete floor (no soil anywhere lit) plus Hydroponics
    //                  research and basin stock, so hydroponics is the one
    //                  plantable kind.
    //
    // The room sits inside the planning region (22 cells around the colonist
    // centroid) so the environment census sees it. Nothing here plans a
    // zone or building: that is the field planner's own work.
    public sealed class FarmEnvironmentFixture
    {
        [Tool("test/farm_environment_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: build one enclosed roofed room with a powered sun lamp and heaters on its own fuelled generators, register a cold snap and dress the colonists; scenario 'greenhouse' floors it with soil, 'hydroponics' with concrete and finishes Hydroponics research with basin stock. unavailableCrops (comma-separated plant defs) gates those crops behind an unfinished research project for this game only, so a scenario can make another basin crop win.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, string scenario = "greenhouse", string unavailableCrops = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (scenario != "greenhouse" && scenario != "hydroponics") return Refuse("scenario must be 'greenhouse' or 'hydroponics'.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                if (people.Count == 0) return Refuse("No free colonists on the map.");
                var builder = people.Where(p => !p.Downed && !p.Drafted && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (builder == null) return Refuse("No existing colonist able to construct.");
                var construction = builder.skills?.GetSkill(SkillDefOf.Construction);
                if (construction != null && construction.Level < 6) { construction.Level = 6; construction.xpSinceLastLevel = 0f; }
                if (builder.workSettings != null && builder.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0)
                    builder.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);

                var lampDef = DefDatabase<ThingDef>.GetNamedSilentFail("SunLamp");
                var heaterDef = DefDatabase<ThingDef>.GetNamedSilentFail("Heater");
                var basinDef = DefDatabase<ThingDef>.GetNamedSilentFail("HydroponicsBasin");
                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                var conduitDef = DefDatabase<ThingDef>.GetNamedSilentFail("PowerConduit");
                var generatorDef = DefDatabase<ThingDef>.GetNamedSilentFail("WoodFiredGenerator");
                var parkaDef = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_Parka");
                var soilDef = DefDatabase<TerrainDef>.GetNamedSilentFail("Soil");
                var coldSnap = DefDatabase<GameConditionDef>.GetNamedSilentFail("ColdSnap");
                if (lampDef == null || heaterDef == null || basinDef == null || wallDef == null || doorDef == null || conduitDef == null
                    || generatorDef == null || parkaDef == null || soilDef == null || coldSnap == null)
                    return Refuse("SunLamp, Heater, HydroponicsBasin, Wall, Door, PowerConduit, WoodFiredGenerator, Apparel_Parka, Soil or ColdSnap unavailable in this ruleset.");
                // Research follows the definitions' own prerequisites; the
                // greenhouse scenario deliberately leaves the basin unresearched
                // so hydroponics is refused as unavailable rather than outscored.
                var researched = new[] { lampDef, heaterDef, generatorDef, conduitDef }.Concat(scenario == "hydroponics" ? new[] { basinDef } : new ThingDef[0]).ToList();
                foreach (var def in researched)
                    foreach (var project in def.researchPrerequisites ?? new List<ResearchProjectDef>()) Finish(project);
                if (researched.Any(d => !d.IsResearchFinished)) return Refuse("Fixture research did not finish.");
                // A crop named in unavailableCrops is gated behind an unfinished
                // project on both the definition (the planner's census reads
                // researchPrerequisites) and its sowing (the game's own
                // set-plant gizmo rule), for this disposable game only.
                var gated = new List<string>();
                foreach (var name in (unavailableCrops ?? string.Empty).Split(',').Select(n => n.Trim()).Where(n => n.Length > 0))
                {
                    var crop = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                    if (crop?.plant == null) return Refuse("unavailableCrops names a definition that is not a plant: " + name);
                    var unfinished = DefDatabase<ResearchProjectDef>.AllDefsListForReading.OrderBy(p => p.defName, StringComparer.Ordinal).FirstOrDefault(p => !p.IsFinished);
                    if (unfinished == null) return Refuse("No unfinished research project is left to gate " + name + " behind.");
                    crop.researchPrerequisites = new List<ResearchProjectDef> { unfinished };
                    crop.plant.sowResearchPrerequisites = new List<ResearchProjectDef> { unfinished };
                    gated.Add(name + "<-" + unfinished.defName);
                }

                // 15x12 clearing near the colonist centroid: the room at
                // (0..10, 0..10) with its lamp at (5,5), a conduit column at
                // x=12, five generators in the column x=13..14, and stock on
                // the two rows above them. Every cell stays within the planning
                // region's 22-cell half-width around the centroid.
                const int width = 15, height = 12;
                var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
                var origin = GenRadial.RadialCellsAround(center, 14, true).FirstOrDefault(c =>
                    c.x >= center.x - 20 && c.x + width - 1 <= center.x + 20 && c.z >= center.z - 20 && c.z + height - 1 <= center.z + 20
                    && new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !map.roofGrid.Roofed(cell)
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building || t is Blueprint || t is Frame))
                    && builder.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable 15x12 area near the colonist centroid for the fixture greenhouse.");
                var clearing = new CellRect(origin.x, origin.z, width, height);
                foreach (var cell in clearing.Cells)
                {
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth).ToList()) thing.Destroy();
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
                    map.areaManager.Home[cell] = true;
                }
                IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);
                Thing Spawn(ThingDef def, IntVec3 cell, Rot4 rotation)
                {
                    var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    thing.SetFaction(player);
                    return GenSpawn.Spawn(thing, cell, map, rotation);
                }

                var room = new CellRect(origin.x, origin.z, 11, 11);
                var interior = room.ContractedBy(1);
                var door = At(0, 5);
                foreach (var cell in room.Cells)
                {
                    var edge = cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ;
                    if (!edge)
                    {
                        map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                        if (scenario == "greenhouse") map.terrainGrid.SetTerrain(cell, soilDef);
                        continue;
                    }
                    Spawn(conduitDef, cell, Rot4.North);
                    if (cell == door) { Spawn(doorDef, cell, Rot4.North); map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed); }
                    else Spawn(wallDef, cell, Rot4.North);
                }
                var lamp = Spawn(lampDef, At(5, 5), Rot4.North);
                var heaters = new[] { Spawn(heaterDef, At(1, 1), Rot4.North), Spawn(heaterDef, At(9, 9), Rot4.North) };
                foreach (var heater in heaters) heater.TryGetComp<CompTempControl>().targetTemperature = 21f;
                // Conduits from the east wall to the generator column.
                Spawn(conduitDef, At(11, 5), Rot4.North);
                for (var z = 0; z <= 9; z++) Spawn(conduitDef, At(12, z), Rot4.North);
                var generators = new List<string>();
                foreach (var z in new[] { 0, 2, 4, 6, 8 })
                {
                    var generator = Spawn(generatorDef, At(13, z), Rot4.North);
                    var fuel = generator.TryGetComp<CompRefuelable>();
                    fuel.Refuel(fuel.Props.fuelCapacity);
                    generators.Add(generator.GetUniqueLoadID());
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                map.powerNetManager.UpdatePowerNetsAndConnections_First();
                // The game is paused, so consumers never tick on by themselves.
                foreach (var consumer in new[] { lamp }.Concat(heaters))
                    consumer.TryGetComp<CompPowerTrader>().PowerOn = true;

                var stock = new List<object>();
                var stockCell = 0;
                var wanted = new List<(string, int)> { ("WoodLog", 200) };
                if (scenario == "hydroponics") { wanted.Add(("Steel", 300)); wanted.Add(("ComponentIndustrial", 10)); }
                foreach (var (name, count) in wanted)
                {
                    var def = DefDatabase<ThingDef>.GetNamed(name);
                    for (var remaining = count; remaining > 0; remaining -= def.stackLimit)
                    {
                        if (stockCell >= 8) return Refuse("Fixture stock does not fit its two rows.");
                        var stack = ThingMaker.MakeThing(def);
                        stack.stackCount = System.Math.Min(remaining, def.stackLimit);
                        GenSpawn.Spawn(stack, At(11 + stockCell % 4, 10 + stockCell / 4), map);
                        stack.SetForbidden(false, false);
                        stock.Add(new { id = stack.GetUniqueLoadID(), defName = name, count = stack.stackCount });
                        stockCell++;
                    }
                }

                // A cold snap takes -20C at full ramp (lerped in over 12000
                // ticks); enough fully ramped snaps close the outdoor sowing
                // season (below 0C) with margin, and parkas keep the tribal
                // colonists out of hypothermia for the run.
                var outdoors = map.mapTemperature.OutdoorTemp;
                var snaps = System.Math.Max(1, System.Math.Min(3, (int)System.Math.Ceiling((outdoors + 8f) / 20f)));
                for (var i = 0; i < snaps; i++)
                {
                    var snap = GameConditionMaker.MakeCondition(coldSnap, 4 * 60000 + 12000);
                    map.gameConditionManager.RegisterCondition(snap);
                    // RegisterCondition clamps startTick up to now, so the ramp
                    // is skipped only by moving it back afterwards.
                    snap.startTick = Find.TickManager.TicksGame - 12000;
                }
                // The tile temperature cache is keyed on the game tick, and the
                // game is paused: drop it so the reported outdoor temperature
                // already carries the snaps.
                Find.World.tileTemperatures.ClearCaches();
                var dressed = 0;
                foreach (var pawn in people.Where(p => p.apparel != null))
                {
                    if (pawn.apparel.WornApparel.Any(a => a.def == parkaDef)) continue;
                    var parka = (Apparel)ThingMaker.MakeThing(parkaDef, ThingDefOf.Cloth);
                    pawn.apparel.Wear(parka, false);
                    dressed++;
                }
                var inside = At(5, 4).GetRoom(map);
                if (inside == null || inside.OpenRoofCount > 0 || inside.TouchesMapEdge || inside.PsychologicallyOutdoors)
                    return Refuse("Fixture greenhouse is not enclosed after construction.");
                inside.Temperature = 21f;
                var schedule = lamp.TryGetComp<CompSchedule>();
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, scenario, builder = builder.GetUniqueLoadID(),
                    origin = new { x = origin.x, z = origin.z },
                    interior = new { minX = interior.minX, minZ = interior.minZ, maxX = interior.maxX, maxZ = interior.maxZ },
                    roomId = inside.ID, roomTemperatureC = inside.Temperature, outdoorTemperatureC = map.mapTemperature.OutdoorTemp, coldSnaps = snaps,
                    lamp = lamp.GetUniqueLoadID(), lampPowered = lamp.TryGetComp<CompPowerTrader>().PowerOn, lampScheduled = schedule == null || schedule.Allowed,
                    lampGrowthRadius = lampDef.specialDisplayRadius, heaters = heaters.Select(h => h.GetUniqueLoadID()).ToList(),
                    generators, stock, dressed, unavailableCrops = gated,
                    setup = "Test-only greenhouse; the growing zone or basin placement remains the field planner's own work.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/farm_environment_observe", Description = "Read the growing zones and hydroponics basins (built, framed or blueprinted) inside a rectangle. Test-only; changes nothing.")]
        public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken cancellationToken, int minX = 0, int minZ = 0, int maxX = 0, int maxZ = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) return Refuse("A disposable colony map is required.");
                var rect = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                var zones = map.zoneManager.AllZones.OfType<Zone_Growing>()
                    .Select(z => new { id = z.ID, label = z.label, crop = z.GetPlantDefToGrow()?.defName, cells = z.Cells.Count, inside = z.Cells.Count(c => rect.Contains(c)) })
                    .Where(z => z.inside > 0).ToList();
                var basins = new List<object>();
                foreach (var cell in rect.Cells)
                    foreach (var thing in cell.GetThingList(map))
                    {
                        if (thing.Position != cell) continue;
                        var def = thing is Blueprint b ? b.def.entityDefToBuild as ThingDef : thing is Frame f ? f.def.entityDefToBuild as ThingDef : thing.def;
                        if (def?.defName != "HydroponicsBasin") continue;
                        basins.Add(new { id = thing.GetUniqueLoadID(), stage = thing is Blueprint ? "blueprint" : thing is Frame ? "frame" : "built", x = cell.x, z = cell.z,
                            crop = (thing as Building_PlantGrower)?.GetPlantDefToGrow()?.defName });
                    }
                return new { success = true, tick = Find.TickManager.TicksGame, zones, basins, outdoorTemperatureC = map.mapTemperature.OutdoorTemp };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static void Finish(ResearchProjectDef project)
        {
            if (project.IsFinished) return;
            if (project.prerequisites != null) foreach (var p in project.prerequisites) Finish(p);
            Find.ResearchManager.FinishProject(project, false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
