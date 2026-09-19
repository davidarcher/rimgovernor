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
        public async Task<object> RoutineTemperature(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z, bool hot = false,
            [ToolParameter(Description = "When the outdoor temperature is too warm for the cold method, register ordinary ColdSnap conditions (ramp already complete) sized on the day's peak so the whole run stays under the cold threshold; no direct temperature edit.", DefaultValue = false)] bool coldSnap = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var coldSnaps = 0;
                if (coldSnap && !hot && map != null && map.mapTemperature.OutdoorTemp >= 12) {
                    var snapDef = DefDatabase<GameConditionDef>.GetNamedSilentFail("ColdSnap");
                    if (snapDef == null) throw new InvalidOperationException("ColdSnap unavailable in this ruleset.");
                    // The sun cycle swings +-7C, so size the snaps on the day's
                    // peak (as FarmEnvironmentFixture does): each snap is -20C.
                    var peak = map.mapTemperature.OutdoorTemp - GenTemperature.OffsetFromSunCycle(Find.TickManager.TicksAbs, map.Tile) + 7f;
                    var snaps = System.Math.Max(1, System.Math.Min(3, (int)System.Math.Ceiling((peak - 4f) / 20f)));
                    for (var i = 0; i < snaps; i++) {
                        var snap = GameConditionMaker.MakeCondition(snapDef, 4 * 60000 + 12000);
                        map.gameConditionManager.RegisterCondition(snap);
                        // RegisterCondition clamps startTick up to now; move it
                        // back afterwards so the ramp-in is already complete.
                        snap.startTick = Find.TickManager.TicksGame - 12000;
                        coldSnaps++;
                    }
                    // The tile temperature cache is keyed on the game tick and
                    // the game is paused: drop it so OutdoorTemp carries the snaps.
                    Find.World.tileTemperatures.ClearCaches();
                }
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
                    outdoorTemperature = outdoor, coldSnaps, roomTemperature = room.Temperature, requiredConstruction = definition.constructionSkillPrerequisite,
                    cells = cells.Select(c => new { x = c.x, z = c.z }).ToArray(), setupOnly = true };
            }, cancellationToken);
        }

        [Tool("test/heatwave_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Prepare the powered-cooler heat wave scenario (#406) on the prepared enclosed 25-cell sleeping room at x,z: a sleeping spot per colonist, Electricity and AirConditioning research, a fuelled wood-fired generator outside the room with hidden conduits under the room's walls and a run to it (the power family leaves hidden conduits alone), Cooler construction stock and wood, dusters and hats on every colonist, and the fully ramped HeatWave (and ColdSnap) stack that brings the day's mean outdoors to meanTargetC: hot enough that a 25-cell room needs a powered cooler, the night's trough above the temperature review's hot exit, the day's peak under the dressed colonists' heatstroke threshold. The room starts at the outdoors' current reading or the hot entry threshold, whichever is warmer. No thermal facility is created; native weather and simulation set every later temperature.")]
        public async Task<object> HeatWave(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z,
            [ToolParameter(Description = "The temperature review's hot entry threshold; the room starts at least this warm so the deficit is live at the first review.", DefaultValue = 32f)] float hotEnterC = 32f,
            [ToolParameter(Description = "The temperature review's hot exit threshold; the fixture refuses a day whose trough would fall under it and release the latch by weather alone.", DefaultValue = 28f)] float hotExitC = 28f,
            [ToolParameter(Description = "The day's mean outdoor temperature the condition stack aims for, without the +/-7 C sun cycle.", DefaultValue = 40f)] float meanTargetC = 40f,
            [ToolParameter(Description = "Margin the day's peak must keep under the colonists' lowest safe maximum; the fixture refuses a stack that does not.", DefaultValue = 1.5f)] float peakMarginC = 1.5f)
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
                if (map.listerBuildings.allBuildingsColonist.Any(b => new[] { "Campfire", "PassiveCooler", "Heater", "Cooler" }.Contains(b.def.defName)))
                    throw new InvalidOperationException("Thermal fixture requires no existing thermal buildings.");
                if (map.listerBuildings.allBuildingsColonist.Any(b => b.TryGetComp<CompPowerTrader>() != null))
                    throw new InvalidOperationException("Heat wave fixture requires no existing power traders.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                if (people.Count == 0) throw new InvalidOperationException("No colonists.");
                foreach (var name in new[] { "Electricity", "AirConditioning" }) {
                    var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);
                    if (project == null) throw new InvalidOperationException("Research project " + name + " unavailable in this ruleset.");
                    if (!project.IsFinished) Find.ResearchManager.FinishProject(project, false);
                }
                var coolerDef = ThingDef.Named("Cooler");
                var generatorDef = ThingDef.Named("WoodFiredGenerator");
                var conduitDef = ThingDef.Named("HiddenConduit");
                if (!coolerDef.IsResearchFinished) throw new InvalidOperationException("Cooler research did not finish.");
                var construction = DefDatabase<WorkTypeDef>.GetNamed("Construction");
                var builder = people.Where(p => !p.WorkTypeIsDisabled(construction) && !p.skills.GetSkill(SkillDefOf.Construction).TotallyDisabled)
                    .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Construction).Level).FirstOrDefault();
                if (builder == null) throw new InvalidOperationException("No capable fixture builder.");
                builder.skills.GetSkill(SkillDefOf.Construction).Level = Math.Max(coolerDef.constructionSkillPrerequisite, builder.skills.GetSkill(SkillDefOf.Construction).Level);

                // Sleeping spots, one per colonist, as routine_temperature_prepare lays them.
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

                // Conduits under every wall cell of the 7x7 shell, then a run east
                // to a fuelled generator two cells past the wall; the generator's
                // own footprint is cleared of plants and loose items.
                var shell = CellRect.CenteredOn(center, 3);
                var walls = new System.Collections.Generic.List<object>();
                Thing Spawn(ThingDef def, IntVec3 cell, Rot4 rotation) {
                    var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    thing.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(thing, cell, map, rotation);
                    thing.SetForbidden(false, false);
                    return thing;
                }
                foreach (var cell in shell.EdgeCells) {
                    if (cell.GetThingList(map).All(t => t.def != conduitDef)) Spawn(conduitDef, cell, Rot4.North);
                    if (cell.GetEdifice(map)?.def == ThingDefOf.Wall) walls.Add(new { x = cell.x, z = cell.z });
                }
                var generatorCell = center + new IntVec3(6, 0, 0);
                var generatorRect = GenAdj.OccupiedRect(generatorCell, Rot4.North, generatorDef.size);
                var run = new[] { center + new IntVec3(4, 0, 0), center + new IntVec3(5, 0, 0) };
                foreach (var cell in generatorRect.Cells.Concat(run)) {
                    if (!cell.InBounds(map) || cell.Fogged(map) || cell.GetEdifice(map) != null || cell.GetTerrain(map).passability == Traversability.Impassable || cell.GetTerrain(map).IsWater)
                        throw new InvalidOperationException("Generator site east of the room is not open ground.");
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();
                }
                foreach (var cell in run) Spawn(conduitDef, cell, Rot4.North);
                var generator = Spawn(generatorDef, generatorCell, Rot4.North);
                var fuel = generator.TryGetComp<CompRefuelable>();
                fuel.Refuel(fuel.Props.fuelCapacity);
                map.powerNetManager.UpdatePowerNetsAndConnections_First();

                // Construction stock for two coolers plus wood for the generator,
                // outside the room within the planners' reach.
                var supplies = coolerDef.CostListAdjusted(null, false).Select(c => new ThingDefCountClass(c.thingDef, c.count * 2)).ToList();
                supplies.Add(new ThingDefCountClass(ThingDefOf.WoodLog, 300));
                var reserved = new System.Collections.Generic.HashSet<IntVec3>(shell.Cells.Concat(generatorRect.Cells).Concat(run));
                var stockCells = GenRadial.RadialCellsAround(center, 12, true).Where(c => c.InBounds(map) && c.Standable(map) && !reserved.Contains(c)
                    && c.GetThingList(map).All(t => t is Plant) && map.zoneManager.ZoneAt(c) == null).Take(24).ToList();
                var slot = 0;
                foreach (var cost in supplies) {
                    var remaining = cost.count;
                    while (remaining > 0) {
                        if (slot >= stockCells.Count) throw new InvalidOperationException("Insufficient fixture material cells.");
                        var stack = ThingMaker.MakeThing(cost.thingDef); stack.stackCount = Math.Min(stack.def.stackLimit, remaining); remaining -= stack.stackCount;
                        GenSpawn.Spawn(stack, stockCells[slot++], map); stack.SetForbidden(false, false);
                    }
                }

                // Dusters and cowboy hats widen every colonist's safe range upward;
                // the day's peak is then set just under the lowest safe maximum, so
                // nobody working outdoors heatstrokes, and the night's trough
                // (peak minus the 14 C sun swing) must stay above the hot exit.
                var dusterDef = ThingDef.Named("Apparel_Duster");
                var hatDef = ThingDef.Named("Apparel_CowboyHat");
                var safeMax = float.MaxValue;
                foreach (var pawn in people) {
                    if (pawn.apparel == null) continue;
                    if (!pawn.apparel.WornApparel.Any(a => a.def == dusterDef)) pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(dusterDef, ThingDefOf.Cloth), false);
                    if (!pawn.apparel.WornApparel.Any(a => a.def == hatDef)) pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(hatDef, ThingDefOf.Cloth), false);
                    safeMax = Math.Min(safeMax, pawn.SafeTemperatureRange().max);
                }
                var meanTarget = meanTargetC;
                var peakTarget = meanTarget + 7f;
                if (peakTarget > safeMax - peakMarginC)
                    throw new InvalidOperationException(string.Format("Day peak {0:F1} C reaches the colonists' safe maximum {1:F1} C less the {2:F1} C margin; they would heatstroke outdoors.", peakTarget, safeMax, peakMarginC));
                if (meanTarget - 7f < hotExitC + 0.5f)
                    throw new InvalidOperationException(string.Format("Mean {0:F1} C leaves a night trough of {1:F1} C under the hot exit {2:F1} C; the latch would release by weather alone.", meanTarget, meanTarget - 7f, hotExitC));
                var heatDef = DefDatabase<GameConditionDef>.GetNamedSilentFail("HeatWave");
                var snapDef = DefDatabase<GameConditionDef>.GetNamedSilentFail("ColdSnap");
                if (heatDef == null || snapDef == null) throw new InvalidOperationException("HeatWave or ColdSnap unavailable in this ruleset.");
                // The day's mean without the sun cycle; conditions already active are inside it.
                var meanNow = map.mapTemperature.OutdoorTemp - GenTemperature.OffsetFromSunCycle(Find.TickManager.TicksAbs, map.Tile);
                var needed = meanTarget - meanNow;
                int waves = 0, snaps = 0; var best = float.MaxValue;
                for (var a = 0; a <= 5; a++)
                    for (var b = 0; b <= 5; b++) {
                        var error = Math.Abs(17f * a - 20f * b - needed);
                        if (error < best - 0.01f || Math.Abs(error - best) <= 0.01f && a + b < waves + snaps) { best = error; waves = a; snaps = b; }
                    }
                if (best > 2f) throw new InvalidOperationException(string.Format("No heat wave/cold snap stack brings the day's mean from {0:F1} C within 2 C of {1:F1} C.", meanNow, meanTarget));
                var duration = 5 * 60000 + 2 * 12000;
                for (var i = 0; i < waves + snaps; i++) {
                    var condition = GameConditionMaker.MakeCondition(i < waves ? heatDef : snapDef, duration);
                    map.gameConditionManager.RegisterCondition(condition);
                    // RegisterCondition clamps startTick to now; backdate it past the ramp (#437).
                    condition.startTick = Find.TickManager.TicksGame - 12000;
                }
                Find.World.tileTemperatures.ClearCaches();
                var outdoor = map.mapTemperature.OutdoorTemp;
                // The room starts at the outdoors, or at the hot entry when the
                // outdoors reads cooler at this hour, so the first review sees the
                // deficit the afternoon would bring anyway.
                room.Temperature = Math.Max(outdoor, hotEnterC + 1f);
                return new { success = true, beds = beds.ToArray(), walls, generator = generator.GetUniqueLoadID(),
                    generatorCell = new { x = generatorCell.x, z = generatorCell.z },
                    cells = cells.Select(c => new { x = c.x, z = c.z }).ToArray(),
                    heatWaves = waves, coldSnaps = snaps, safeMaxC = safeMax, peakTargetC = peakTarget, meanTargetC = meanTarget, meanBeforeC = meanNow,
                    outdoorTemperature = outdoor, roomTemperatureC = room.Temperature, requiredConstruction = coolerDef.constructionSkillPrerequisite,
                    costs = coolerDef.CostListAdjusted(null, false).Select(c => new { defName = c.thingDef.defName, count = c.count }).ToArray(), setupOnly = true };
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
