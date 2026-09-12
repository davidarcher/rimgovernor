using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable state setup only, excluded from production and the model gateway.
    public sealed class ForecastFixture
    {
        private static Thing generator;
        private static Thing battery;
        private static Thing rice;

        [Tool("test/routine_temperature_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Prepare sleeping spots and native construction/fuel materials in a disposable roofed room. Initialize the room at actual outdoor temperature once; never create thermal facilities or force pawn construction/refueling jobs.")]
        public async Task<object> RoutineTemperature(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z, bool hot = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable map required.");
                var center = new IntVec3(x, 0, z);
                var room = center.GetRoom(map);
                if (room == null || !room.ProperRoom || room.UsesOutdoorTemperature || room.OpenRoofCount != 0)
                    throw new InvalidOperationException("Prepared enclosed sleeping room required.");
                var cells = room.Cells.ToList();
                if (cells.Count != 25 || map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Any())
                    throw new InvalidOperationException("Require empty 25-cell room without existing player beds.");
                var outdoor = map.mapTemperature.OutdoorTemp;
                if (hot ? outdoor <= 32 : outdoor >= 12) throw new InvalidOperationException("Native outdoor temperature does not require selected thermal method.");
                if (map.listerBuildings.allBuildingsColonist.Any(b => new[] { "Campfire", "PassiveCooler", "Heater", "Cooler" }.Contains(b.def.defName)))
                    throw new InvalidOperationException("Thermal fixture requires no existing thermal buildings.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                var definition = ThingDef.Named(hot ? "PassiveCooler" : "Campfire");
                foreach (var project in definition.researchPrerequisites ?? Enumerable.Empty<ResearchProjectDef>())
                    if (!project.IsFinished) Find.ResearchManager.FinishProject(project, false);
                var construction = DefDatabase<WorkTypeDef>.GetNamed("Construction");
                var builder = people.Where(p => !p.WorkTypeIsDisabled(construction) && !p.skills.GetSkill(SkillDefOf.Construction).TotallyDisabled)
                    .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Construction).Level).FirstOrDefault();
                if (builder == null) throw new InvalidOperationException("No capable fixture builder.");
                builder.skills.GetSkill(SkillDefOf.Construction).Level = Math.Max(definition.constructionSkillPrerequisite, builder.skills.GetSkill(SkillDefOf.Construction).Level);
                var bedDef = ThingDef.Named("SleepingSpot");
                var occupied = new System.Collections.Generic.HashSet<IntVec3>();
                var beds = new System.Collections.Generic.List<string>();
                foreach (var cell in cells.OrderBy(c => c.z).ThenBy(c => c.x)) {
                    var footprint = GenAdj.OccupiedRect(cell, Rot4.North, bedDef.size).Cells.ToList();
                    if (!footprint.All(c => cells.Contains(c) && !occupied.Contains(c))) continue;
                    var bed = ThingMaker.MakeThing(bedDef); bed.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(bed, cell, map, Rot4.North); bed.SetForbidden(false, false);
                    beds.Add(bed.GetUniqueLoadID()); foreach (var c in footprint) occupied.Add(c);
                    if (beds.Count == people.Count) break;
                }
                if (beds.Count != people.Count) throw new InvalidOperationException("Insufficient fixture sleeping footprints.");
                var supplies = definition.CostListAdjusted(null, false).ToList();
                supplies.Add(new ThingDefCountClass(ThingDefOf.WoodLog, 100));
                var stockCells = GenRadial.RadialCellsAround(center, 12, true).Where(c => c.InBounds(map) && c.Standable(map) && !cells.Contains(c)
                    && c.GetThingList(map).All(t => t is Plant) && map.zoneManager.ZoneAt(c) == null).Take(16).ToList();
                var slot = 0;
                foreach (var cost in supplies.GroupBy(c => c.thingDef)) {
                    var remaining = checked(cost.Sum(c => c.count) * 2);
                    while (remaining > 0) {
                        if (slot >= stockCells.Count) throw new InvalidOperationException("Insufficient fixture material cells.");
                        var stack = ThingMaker.MakeThing(cost.Key); stack.stackCount = Math.Min(stack.def.stackLimit, remaining); remaining -= stack.stackCount;
                        GenSpawn.Spawn(stack, stockCells[slot++], map); stack.SetForbidden(false, false);
                    }
                }
                // Only initial conditions are seeded. Seasonal native weather and
                // normal simulation determine every subsequent room temperature.
                room.Temperature = outdoor;
                return new { success = true, hot, definition = definition.defName, beds = beds.ToArray(),
                    outdoorTemperature = outdoor, roomTemperature = room.Temperature, requiredConstruction = definition.constructionSkillPrerequisite,
                    cells = cells.Select(c => new { x = c.x, z = c.z }).ToArray(), setupOnly = true };
            }, cancellationToken);
        }

        [Tool("test/routine_power_methods", Description = "UNSAFE FOR MODEL EXECUTION. Prepare a bounded concrete test lane, consumer, optional distant unfueled generator, research, materials and qualified builder. Does not create conduits or execute construction/refueling jobs.")]
        public async Task<object> RoutinePowerMethods(IRimBridgeContext ctx, CancellationToken cancellationToken, bool connectExisting = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required.");
                if (map.listerBuildings.allBuildingsColonist.Any(b => b.TryGetComp<CompPowerTrader>() != null))
                    throw new InvalidOperationException("Power method fixture requires no existing power traders.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
                var definitions = new[] { "WoodFiredGenerator", "PowerConduit", "StandingLamp" }.Select(ThingDef.Named).ToList();
                foreach (var project in definitions.SelectMany(d => d.researchPrerequisites ?? Enumerable.Empty<ResearchProjectDef>()).Distinct())
                    if (!project.IsFinished) Find.ResearchManager.FinishProject(project, false);
                var requiredConstruction = definitions.Max(d => d.constructionSkillPrerequisite);
                var construction = DefDatabase<WorkTypeDef>.GetNamed("Construction");
                var builder = people.Where(p => !p.WorkTypeIsDisabled(construction) && !p.skills.GetSkill(SkillDefOf.Construction).TotallyDisabled)
                    .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Construction).Level).FirstOrDefault();
                if (builder == null) throw new InvalidOperationException("No capable fixture builder.");
                builder.skills.GetSkill(SkillDefOf.Construction).Level = Math.Max(requiredConstruction, builder.skills.GetSkill(SkillDefOf.Construction).Level);
                var sites = GenRadial.RadialCellsAround(center, 20, true).Where(c =>
                    CellRect.FromLimits(c, c + new IntVec3(16, 0, 6)).Cells.All(p => p.InBounds(map) && !p.Fogged(map)
                        && Math.Abs(p.x - center.x) <= 20 && Math.Abs(p.z - center.z) <= 20
                        && map.zoneManager.ZoneAt(p) == null
                        && !p.GetThingList(map).Any(t => t is Pawn || t is Blueprint || t is Frame
                            || t is Building && !t.def.building.isNaturalRock))).Take(1).ToList();
                if (sites.Count != 1) throw new InvalidOperationException("No bounded power fixture site.");
                var origin = sites[0];
                var lane = CellRect.FromLimits(origin, origin + new IntVec3(16, 0, 6)).Cells.ToList();
                foreach (var cell in lane) {
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t is Building && t.def.building.isNaturalRock).ToList()) thing.Destroy();
                    map.terrainGrid.SetTerrain(cell, DefDatabase<TerrainDef>.GetNamed("Concrete"));
                }
                Func<string, int, int, Thing> spawn = (name, x, z) => {
                    var thing = ThingMaker.MakeThing(ThingDef.Named(name));
                    thing.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(thing, origin + new IntVec3(x, 0, z), map);
                    thing.SetForbidden(false, false);
                    return thing;
                };
                var lamp = spawn("StandingLamp", 2, 2);
                Thing source = null;
                if (connectExisting) {
                    source = spawn("WoodFiredGenerator", 14, 2);
                    var fuel = source.TryGetComp<CompRefuelable>();
                    fuel.ConsumeFuel(fuel.Fuel);
                }
                var supplies = definitions.SelectMany(d => d.CostListAdjusted(null, false)).ToList();
                supplies.Add(new ThingDefCountClass(ThingDefOf.WoodLog, 100));
                var stockSlot = 0;
                foreach (var cost in supplies.GroupBy(c => c.thingDef)) {
                    var remaining = checked(cost.Sum(c => c.count) * 2);
                    while (remaining > 0) {
                        if (stockSlot >= 14) throw new InvalidOperationException("Power fixture materials exceed lane storage.");
                        var stack = ThingMaker.MakeThing(cost.Key);
                        stack.stackCount = Math.Min(stack.def.stackLimit, remaining);
                        remaining -= stack.stackCount;
                        GenSpawn.Spawn(stack, origin + new IntVec3(2 + stockSlot++, 0, 5), map);
                        stack.SetForbidden(false, false);
                    }
                }
                return new { success = true, consumer = lamp.GetUniqueLoadID(), generator = source?.GetUniqueLoadID(),
                    connectExisting, requiredConstruction, builder = builder.GetUniqueLoadID(),
                    consumerCell = new { x = lamp.Position.x, z = lamp.Position.z },
                    setupOnly = true };
            }, cancellationToken);
        }

        [Tool("test/routine_power_setup", Description = "Spawn an unfueled generator and electrical consumer in a disposable paused colony. Power observation only; no construction acceptance.")]
        public async Task<object> RoutinePower(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable colony required");
                var center = map.mapPawns.FreeColonistsSpawned.First().Position;
                var ids = new System.Collections.Generic.List<string>();
                foreach (var name in new[] { "WoodFiredGenerator", "StandingLamp" }) {
                    var def = ThingDef.Named(name);
                    var candidates = GenRadial.RadialCellsAround(center, 40, true).Where(c =>
                        GenAdj.OccupiedRect(c, Rot4.North, def.size).Cells.All(p => p.InBounds(map)
                            && !p.Fogged(map) && p.Standable(map) && p.GetEdifice(map) == null
                            && !p.GetThingList(map).Any(t => t is Pawn)
                            && p.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy))).Take(1).ToList();
                    if (candidates.Count != 1) throw new InvalidOperationException("No native power fixture footprint for " + name);
                    var thing = ThingMaker.MakeThing(def);
                    thing.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(thing, candidates[0], map);
                    thing.SetForbidden(false, false);
                    var fuel = thing.TryGetComp<CompRefuelable>();
                    if (fuel != null) fuel.ConsumeFuel(fuel.Fuel);
                    ids.Add(thing.GetUniqueLoadID());
                }
                return new { success = true, buildings = ids, setupOnly = true };
            }, cancellationToken);
        }

        [Tool("test/forecast_setup", Description = "Create a disposable power/food forecast fixture. Test builds only; no construction acceptance.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (generator != null) throw new InvalidOperationException("Fixture already exists");
                var map = Find.CurrentMap;
                var anchor = map.mapPawns.FreeColonistsSpawned.First().Position;
                var origin = GenRadial.RadialCellsAround(anchor, 30, true).First(c =>
                    CellRect.FromLimits(c, c + new IntVec3(8, 0, 8)).Cells.All(p => p.InBounds(map)
                        && !p.Fogged(map) && p.Standable(map) && p.GetEdifice(map) == null
                        && p.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                Func<string, int, int, Thing> spawn = (name, x, z) => {
                    var thing = ThingMaker.MakeThing(ThingDef.Named(name));
                    thing.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(thing, origin + new IntVec3(x, 0, z), map);
                    thing.SetForbidden(false, false);
                    return thing;
                };
                generator = spawn("WoodFiredGenerator", 2, 2);
                battery = spawn("Battery", 5, 2);
                var lamp = spawn("StandingLamp", 5, 4);
                for (var x = 2; x <= 5; x++) spawn("PowerConduit", x, 2);
                var fuel = generator.TryGetComp<CompRefuelable>();
                fuel.ConsumeFuel(fuel.Fuel);
                battery.TryGetComp<CompPowerBattery>().AddEnergy(5);
                rice = ThingMaker.MakeThing(ThingDef.Named("RawRice"));
                rice.stackCount = 10;
                GenSpawn.Spawn(rice, origin + new IntVec3(7, 0, 7), map);
                rice.SetForbidden(false, false);
                var rot = rice.TryGetComp<CompRottable>();
                rot.RotProgress = rot.PropsRot.TicksToRotStart - 60;
                var animal = PawnGenerator.GeneratePawn(PawnKindDef.Named("Muffalo"), Faction.OfPlayer);
                GenSpawn.Spawn(animal, origin + new IntVec3(7, 0, 5), map);
                var hay = ThingMaker.MakeThing(ThingDef.Named("Hay"));
                hay.stackCount = 50;
                GenSpawn.Spawn(hay, origin + new IntVec3(7, 0, 6), map);
                hay.SetForbidden(false, false);
                var zone = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.SetPlantDefToGrow(ThingDef.Named("Plant_Rice"));
                var patch = GenRadial.RadialCellsAround(anchor, 25, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.GetEdifice(map) == null && c.GetPlant(map) == null
                    && map.zoneManager.ZoneAt(c) == null && map.fertilityGrid.FertilityAt(c) >= 1).Take(4).ToList();
                if (patch.Count != 4) throw new InvalidOperationException("No native fertile crop fixture cells");
                foreach (var cell in patch) zone.AddCell(cell);
                var plant = (Plant)ThingMaker.MakeThing(ThingDef.Named("Plant_Rice"));
                plant.Growth = 1;
                GenSpawn.Spawn(plant, patch[0], map);
                return new { success = true, generator = generator.ThingID, battery = battery.ThingID,
                    lamp = lamp.ThingID, food = rice.GetUniqueLoadID(), foodTemperature = rice.AmbientTemperature,
                    animal = animal.GetUniqueLoadID(), hay = hay.GetUniqueLoadID(), zone = zone.ID,
                    setup = "Spawned test infrastructure, seeded battery reserve and aged rice. Subsequent power/rot progression uses ordinary native ticks." };
            }, cancellationToken);
        }

        [Tool("test/forecast_state", Description = "Read retained fixture rot state even after native destruction. Test builds only.")]
        public async Task<object> State(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (rice == null) throw new InvalidOperationException("Missing fixture food");
                var rot = rice.TryGetComp<CompRottable>();
                return new { success = true, food = rice.GetUniqueLoadID(), destroyed = rice.Destroyed,
                    count = rice.stackCount, rotStage = rot.Stage.ToString(), rotProgress = rot.RotProgress };
            }, cancellationToken);
        }

        [Tool("test/forecast_refuel", Description = "Supply the disposable test generator; test builds only. Subsequent charging is native simulation.")]
        public async Task<object> Refuel(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (generator == null || !generator.Spawned || generator.Map != Find.CurrentMap)
                    throw new InvalidOperationException("Missing fixture generator");
                generator.TryGetComp<CompRefuelable>().Refuel(5);
                return new { success = true, generator = generator.ThingID,
                    storedWd = battery.TryGetComp<CompPowerBattery>().StoredEnergy };
            }, cancellationToken);
        }
    }
}
