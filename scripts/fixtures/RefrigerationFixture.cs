using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Builds one enclosed, roofed 6x4
    // stockpile room holding warm raw meat, a fuelled wood-fired generator
    // outside it with conduits under the room's wall ring, finishes the
    // Cooler research and forces a hot room so refrigerationaccept can
    // exercise MaintainRefrigeration (issue #6 slice 1) deterministically:
    // the room's measured temperature and the meat's rot runway are what the
    // Go review latches on, one stack starts part-way to rotting so the case
    // can watch native cooling stop its rot progress (test/refrigeration_rot),
    // and every wall cell of the ring has a vented outdoor neighbour for the
    // cooler placement search. Optionally spawns an existing outward-facing
    // Cooler on the west wall at a warm setpoint (the setpoint-patch path),
    // with or without a conduit run back to the generator (the "hot-weather
    // freezer failure" power hand-off path).
    // With season, the storeroom starts settled cold and the heat waves ramp
    // in from the current tick instead of arriving fully ramped: the harness
    // runs the game through the warming (an inactive family asks for no
    // clock window) and the live controller then meets warm stock the
    // settled freezer's warm setpoint no longer covers (#160). With a heater,
    // a powered Heater inside the room outputs as much heat as one Cooler
    // removes (21 energy/s each, before the cooler's hot-side efficiency
    // loss), so a freezer already at the controller's target can never hold
    // the room alone and the season turn is the heater warming it; only a
    // second Cooler brings it back (#220). That room is larger (roomWidth x
    // roomHeight, default 6x4): a cooler's change per rare tick is capped at
    // the gap to its own setpoint, so in a small room two coolers nearing
    // the freezer target remove less than the heater adds and the room
    // settles just above freezing; spread over more cells the cap stops
    // binding and the room settles below it. Raw meat rots in the two days
    // that cooler waits, so that scenario stocks a longer-lived raw food
    // (foodDef) with every stack part-way to rotting (rotStacks) inside
    // the review's at-risk runway.
    // Nothing here places, sets or cools anything on the controller's behalf.
    public sealed class RefrigerationFixture
    {
        [Tool("test/refrigeration_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: build one enclosed roofed stockpile room with warm raw meat, a fuelled generator and wall-ring conduits, finish Cooler research, force a hot room and a heat wave; optionally an existing warm-setpoint Cooler, optionally disconnected from the generator; one meat stack starts part-way to rotting; season ramps the heat waves in from now instead of arriving fully ramped; coolerTargetC is the existing cooler's setpoint; heater adds a powered Heater inside the room that one Cooler cannot beat; foodDef is the stocked raw food and rotStacks how many of its three stacks start part-way to rotting; roomWidth x roomHeight (at least 6x4) sizes the room including its walls.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, bool existingCooler = false, bool disconnected = false, float roomTemperatureC = 30f, float rotProgressFraction = 0.25f, bool season = false, float coolerTargetC = 21f, bool heater = false, string foodDef = "Meat_Muffalo", int rotStacks = 1, int roomWidth = 6, int roomHeight = 4, bool corpseLarder = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var builder = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (builder == null) return Refuse("No existing colonist able to construct.");
                // Cooler and generator construction carry a skill prerequisite
                // (Cooler 5, WoodFiredGenerator 4) the controller checks before
                // admitting a build; a random debug colony may have nobody that
                // skilled, so the fixture guarantees one qualified builder.
                var construction = builder.skills?.GetSkill(SkillDefOf.Construction);
                if (construction != null && construction.Level < 6) { construction.Level = 6; construction.xpSinceLastLevel = 0f; }
                if (builder.workSettings != null && builder.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0)
                    builder.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                foreach (var name in new[] { "Electricity", "AirConditioning" })
                {
                    var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);
                    if (project == null) return Refuse("Research project " + name + " unavailable in this ruleset.");
                    Finish(project);
                }
                var coolerDef = DefDatabase<ThingDef>.GetNamedSilentFail("Cooler");
                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                var conduitDef = DefDatabase<ThingDef>.GetNamedSilentFail("PowerConduit");
                var generatorDef = DefDatabase<ThingDef>.GetNamedSilentFail("WoodFiredGenerator");
                var heaterDef = DefDatabase<ThingDef>.GetNamedSilentFail("Heater");
                var meatDef = DefDatabase<ThingDef>.GetNamedSilentFail(foodDef ?? "Meat_Muffalo");
                if (coolerDef == null || wallDef == null || doorDef == null || conduitDef == null || generatorDef == null || heaterDef == null || meatDef == null)
                    return Refuse("Cooler, Wall, Door, PowerConduit, WoodFiredGenerator, Heater or " + foodDef + " unavailable in this ruleset.");
                if (meatDef.GetCompProperties<CompProperties_Rottable>() == null) return Refuse(foodDef + " does not rot.");
                if (!coolerDef.IsResearchFinished) return Refuse("Cooler research did not finish.");

                // Heavy-affordance clearing, 12x8 for the default 6x4 room: the
                // room at (0..5, 2..5), the generator at (8,3), construction
                // stock on the rows above the room, a spare far cell at (11,0)
                // for the harness's own player building plan, and a ring of
                // open ground around the room so every wall cell's outward
                // neighbour is outdoors. A larger room grows the clearing with it.
                if (roomWidth < 6 || roomHeight < 4) return Refuse("The room must be at least 6x4 including its walls.");
                int width = roomWidth + 6, height = roomHeight + 4;
                // Any open, unfogged, buildable-passability ground will do: plants
                // and loose items are cleared below and the terrain is paved to
                // concrete so wall/cooler heavy-affordance never depends on the
                // debug map's random biome.
                var origin = GenRadial.RadialCellsAround(builder.Position, 75, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && !map.roofGrid.Roofed(cell)
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building))
                    && builder.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable 12x8 area for the fixture storeroom.");
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

                var room = new CellRect(origin.x, origin.z + 2, roomWidth, roomHeight);
                var coolerCell = At(0, 4);
                var walls = new List<object>();
                foreach (var cell in room.Cells)
                {
                    var edge = cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ;
                    if (!edge) { map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed); continue; }
                    Spawn(conduitDef, cell, Rot4.North);
                    if (cell == At(0, 3))
                    {
                        Spawn(doorDef, cell, Rot4.North);
                        map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    }
                    else if (existingCooler && cell == coolerCell)
                    {
                        map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    }
                    else
                    {
                        Spawn(wallDef, cell, Rot4.North);
                        walls.Add(new { x = cell.x, z = cell.z });
                    }
                }
                Thing cooler = null;
                if (existingCooler)
                {
                    // Front (hot side) faces west, outdoors; cold side at (1,4) inside.
                    cooler = Spawn(coolerDef, coolerCell, Rot4.West);
                    var control = cooler.TryGetComp<CompTempControl>();
                    if (control == null) return Refuse("Fixture cooler unexpectedly has no CompTempControl.");
                    control.targetTemperature = coolerTargetC;
                    if (corpseLarder)
                    {
                        var secondCell = At(roomWidth - 1, 4);
                        secondCell.GetEdifice(map)?.Destroy();
                        map.roofGrid.SetRoof(secondCell, RoofDefOf.RoofConstructed);
                        var second = Spawn(coolerDef, secondCell, Rot4.East);
                        second.TryGetComp<CompTempControl>().targetTemperature = coolerTargetC;
                    }
                }
                // The heater sits on the interior cell farthest from the cooler's
                // cold side and the meat, off the stockpile, wall conduits one
                // cell away; its setpoint is far above chilled so it runs whenever
                // the room is cooler than a warm day.
                var heaterCell = At(roomWidth - 2, roomHeight);
                Thing heaterThing = null;
                if (heater)
                {
                    heaterThing = Spawn(heaterDef, heaterCell, Rot4.North);
                    var control = heaterThing.TryGetComp<CompTempControl>();
                    if (control == null) return Refuse("Fixture heater unexpectedly has no CompTempControl.");
                    control.targetTemperature = 30f;
                }
                var generator = Spawn(generatorDef, At(roomWidth + 2, 3), Rot4.North);
                var fuel = generator.TryGetComp<CompRefuelable>();
                fuel.Refuel(fuel.Props.fuelCapacity);
                var generatorRect = generator.OccupiedRect();
                if (!disconnected)
                    for (var x = room.maxX + 1; x < generatorRect.minX; x++) Spawn(conduitDef, new IntVec3(x, 0, origin.z + 3), Rot4.North);
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                map.powerNetManager.UpdatePowerNetsAndConnections_First();

                var interior = room.ContractedBy(1);
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                foreach (var cell in interior.Cells) if (heaterThing == null || cell != heaterCell) zone.AddCell(cell);
                // Meat only: a default stockpile would also take the leftover
                // construction stock, and every haul through the wood door
                // lets the heat wave into the freezer.
                zone.settings.filter.SetDisallowAll();
                zone.settings.filter.SetAllow(meatDef, true);
                var meat = new List<string>();
                object rotting = null;
                foreach (var cell in interior.Cells.Take(3))
                {
                    var stack = ThingMaker.MakeThing(meatDef);
                    stack.stackCount = meatDef.stackLimit;
                    GenSpawn.Spawn(stack, cell, map);
                    stack.SetForbidden(false, false);
                    meat.Add(stack.GetUniqueLoadID());
                    // The first rotStacks stacks are already part-way to
                    // rotting: the spoilage-recovery read (test/refrigeration_rot)
                    // follows the first one's CompRottable progress, which
                    // native cooling must stop advancing. The rest stay fresh
                    // so the room keeps warm at-risk stock even if that one
                    // spoils first.
                    if (meat.Count <= rotStacks && rotProgressFraction > 0f)
                    {
                        var rot = stack.TryGetComp<CompRottable>();
                        if (rot == null) return Refuse("Fixture food unexpectedly has no CompRottable.");
                        rot.RotProgress = rot.PropsRot.TicksToRotStart * System.Math.Min(rotProgressFraction, 0.9f);
                        if (rotting == null)
                            rotting = new { id = stack.GetUniqueLoadID(), rotProgress = rot.RotProgress, ticksToRotStart = rot.PropsRot.TicksToRotStart };
                    }
                }
                // Construction stock for one Cooler (Steel + components) on the
                // open rows above the room, one full stack per cell: a single
                // stack above its def's stackLimit does not spawn as a countable pile.
                var stock = new List<object>();
                var stockCell = 0;
                foreach (var (name, count) in new[] { ("Steel", 150), ("ComponentIndustrial", 6), ("WoodLog", 100) })
                {
                    var def = DefDatabase<ThingDef>.GetNamed(name);
                    for (var remaining = count; remaining > 0; remaining -= def.stackLimit)
                    {
                        var stack = ThingMaker.MakeThing(def);
                        stack.stackCount = System.Math.Min(remaining, def.stackLimit);
                        GenSpawn.Spawn(stack, At(roomWidth + stockCell % 6, roomHeight + 2 + stockCell / 6), map);
                        stack.SetForbidden(false, false);
                        stock.Add(new { id = stack.GetUniqueLoadID(), defName = name, count = stack.stackCount });
                        stockCell++;
                    }
                }

                // A heat wave adds +17C at full ramp (GameCondition_HeatWave lerps
                // in over 12000 ticks). The debug biome is random, and a small
                // walled room equalises with outdoors within minutes, so stack
                // enough fully ramped heat waves (start tick moved back past the
                // ramp) that the outdoors sits clearly above the 10C chilled
                // threshold for four days: the meat then stays warm until the
                // controller's cooler runs. Aiming any higher stacks waves that
                // give the unsheltered debug colonists heatstroke, and the
                // resulting CriticalMedical hold suspends refrigeration itself.
                // The season scenario keeps the ramp: the outdoors warms over
                // the first 12000 ticks and the cold room follows it up. With
                // a heater the room warms on its own and an already warm
                // outdoors gets no wave: every degree on the coolers' hot side
                // costs them efficiency, and two must still beat the heater.
                var heat = DefDatabase<GameConditionDef>.GetNamedSilentFail("HeatWave");
                var heatWaves = 0;
                if (heat != null && !corpseLarder)
                {
                    var outdoors = map.mapTemperature.OutdoorTemp;
                    heatWaves = System.Math.Max(heater ? 0 : 1, System.Math.Min(3, (int)System.Math.Ceiling((16f - outdoors) / 17f)));
                    for (var i = 0; i < heatWaves; i++)
                    {
                        var wave = GameConditionMaker.MakeCondition(heat, 4 * 60000 + 12000);
                        // RegisterCondition clamps startTick to now, so the wave is
                        // backdated after registration (#437).
                        map.gameConditionManager.RegisterCondition(wave);
                        wave.startTick = Find.TickManager.TicksGame - (season ? 0 : 12000);
                    }
                }
                var inside = At(1, 3).GetRoom(map);
                if (inside == null || inside.OpenRoofCount > 0 || inside.TouchesMapEdge || inside.PsychologicallyOutdoors)
                    return Refuse("Fixture room is not enclosed after construction.");
                inside.Temperature = roomTemperatureC;
                if (corpseLarder)
                {
                    foreach (var item in map.listerThings.AllThings.Where(t => t.def.IsMeat).ToList()) item.Destroy();
                    var raw = ThingMaker.MakeThing(meatDef); raw.stackCount = 60;
                    GenSpawn.Spawn(raw, interior.Cells.First(), map); raw.SetForbidden(false, false);
                    zone.settings.filter.SetAllow(ThingCategoryDefOf.CorpsesAnimal, true);
                    zone.settings.filter.SetAllow(DefDatabase<SpecialThingFilterDef>.GetNamed("AllowRotten"), false);
                    foreach (var cell in interior.Cells.Skip(1).Take(3))
                    {
                        var animal = PawnGenerator.GeneratePawn(PawnKindDefOf.Muffalo);
                        GenSpawn.Spawn(animal, cell, map); animal.Kill(null);
                        animal.Corpse.SetForbidden(true, false);
                    }
                    var butcher = (Building_WorkTable)Spawn(DefDatabase<ThingDef>.GetNamed("ButcherSpot"), At(roomWidth + 1, 0), Rot4.North);
                    var butcherBill = (Bill_Production)DefDatabase<RecipeDef>.GetNamed("ButcherCorpseFlesh").MakeNewBill();
                    butcherBill.repeatMode = BillRepeatModeDefOf.Forever; butcher.BillStack.AddBill(butcherBill);
                    var stove = (Building_WorkTable)Spawn(DefDatabase<ThingDef>.GetNamed("Campfire"), At(roomWidth + 3, 0), Rot4.North);
                    stove.TryGetComp<CompRefuelable>().Refuel(50);
                    var cookBill = (Bill_Production)DefDatabase<RecipeDef>.GetNamed("CookMealSimple").MakeNewBill();
                    cookBill.repeatMode = BillRepeatModeDefOf.TargetCount; cookBill.targetCount = 1; cookBill.suspended = true;
                    stove.BillStack.AddBill(cookBill);
                    var cooking = DefDatabase<WorkTypeDef>.GetNamed("Cooking");
                    // The debug start can be lightly clothed: even -5 C then
                    // has native Danger.Some, outside this planner's safe route.
                    foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                        pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Apparel_Parka"), ThingDefOf.Cloth), false);
                    foreach (var pawn in map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTypeIsDisabled(cooking)))
                    { pawn.workSettings.SetPriority(cooking, 1); pawn.skills.GetSkill(SkillDefOf.Cooking).Level = 10; }
                }
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, builder = builder.GetUniqueLoadID(),
                    roomId = inside.ID, roomTemperatureC = inside.Temperature, outdoorTemperatureC = map.mapTemperature.OutdoorTemp, heatWaves,
                    origin = new { x = origin.x, z = origin.z }, interior = new { minX = interior.minX, minZ = interior.minZ, maxX = interior.maxX, maxZ = interior.maxZ },
                    walls, door = new { x = At(0, 3).x, z = At(0, 3).z },
                    cooler = cooler?.GetUniqueLoadID(), coolerCell = existingCooler ? new { x = coolerCell.x, z = coolerCell.z } : null,
                    generator = generator.GetUniqueLoadID(), disconnected, season, foodDef = meatDef.defName, meat, rotting, stock,
                    heater = heaterThing?.GetUniqueLoadID(), heaterCell = heaterThing != null ? new { x = heaterCell.x, z = heaterCell.z } : null,
                    spareCell = new { x = At(width - 1, 0).x, z = At(width - 1, 0).z },
                    setup = "Test-only enclosed roofed stockpile room with warm raw meat, wall-ring conduits, fuelled generator, Cooler research, forced hot room and heat wave; cooler placement, setpoint and cooling remain the controller's and native simulation's.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/refrigeration_rot", Description = "Private read-only probe: rot progress, stage, runway, ambient temperature and pawns sharing the room of one thing by unique load id, with the game tick.")]
        public async Task<object> Rot(IRimBridgeContext ctx, CancellationToken cancellationToken, string id)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null) return Refuse("A loaded colony map is required.");
                var thing = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == id);
                if (thing == null) return new { success = true, tick = Find.TickManager.TicksGame, id, destroyed = true };
                var rot = thing.TryGetComp<CompRottable>();
                if (rot == null) return Refuse("Thing " + id + " has no CompRottable.");
                return new {
                    success = true, tick = Find.TickManager.TicksGame, id, destroyed = thing.Destroyed, spawned = thing.Spawned,
                    defName = thing.def.defName, count = thing.stackCount,
                    x = thing.Position.x, z = thing.Position.z,
                    temperatureC = thing.Spawned ? (float?)thing.AmbientTemperature : null,
                    pawnsInRoom = thing.Spawned ? (int?)map.mapPawns.AllPawnsSpawned.Count(p => p.GetRoom() == thing.GetRoom()) : null,
                    stage = rot.Stage.ToString(), rotProgress = rot.RotProgress, ticksToRotStart = rot.PropsRot.TicksToRotStart,
                    rotTicks = thing.Spawned ? (int?)rot.TicksUntilRotAtCurrentTemp : null,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static void Finish(ResearchProjectDef project)
        {
            if (project.IsFinished) return;
            if (project.prerequisites != null) foreach (var p in project.prerequisites) Finish(p);
            Find.ResearchManager.FinishProject(project, false);
        }

        [Tool("test/corpse_larder_probe", Description = "UNSAFE FOR MODEL EXECUTION when drain=true. Disposable larder probe; optionally remove raw meat and enable the cook bill, then report exact native corpses and butcher-bill state.")]
        public async Task<object> CorpseLarderProbe(IRimBridgeContext ctx, CancellationToken cancellationToken, bool drain = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) return Refuse("No map.");
                if (drain) {
                    if (!Find.TickManager.Paused) return Refuse("Drain requires paused disposable map.");
                    foreach (var item in map.listerThings.AllThings.Where(t => t.def.IsMeat).ToList()) item.Destroy();
                    foreach (var bench in map.listerThings.AllThings.OfType<Building_WorkTable>())
                    foreach (var bill in bench.BillStack.Bills.OfType<Bill_Production>().Where(b => b.recipe.defName == "CookMealSimple")) bill.suspended = false;
                }
                var corpses = map.listerThings.AllThings.OfType<Corpse>().Where(c => c.InnerPawn.def.defName == "Muffalo").ToList();
                var butcherBills = map.listerThings.AllThings.OfType<Building_WorkTable>().SelectMany(b => b.BillStack.Bills).OfType<Bill_Production>().Where(b => b.recipe.defName == "ButcherCorpseFlesh").ToList();
                return new { success = true, tick = Find.TickManager.TicksGame,
                    rawMeat = map.listerThings.AllThings.Where(t => t.def.IsMeat).Sum(t => t.stackCount),
                    corpses = corpses.Select(c => new { id = c.GetUniqueLoadID(), forbidden = c.IsForbidden(Faction.OfPlayer),
                        meat = c.InnerPawn.GetStatValue(StatDefOf.MeatAmount), temperature = c.AmbientTemperature,
                        rot = c.GetRotStage().ToString(), safe = EventLootFacts.Safe(c),
                        access = map.mapPawns.FreeColonistsSpawned.Select(p => new { pawn = p.GetUniqueLoadID(),
                            reachable = p.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None),
                            danger = c.Position.GetDangerFor(p, map).ToString(),
                            meatAllowed = p.WillEat(c.InnerPawn.RaceProps.meatDef),
                            mealAllowed = p.WillEat(ThingDefOf.MealSimple) }).ToArray() }).ToArray(),
                    corpseTiles = corpses.Count,
                    butcheredTiles = corpses.Sum(c => (int)System.Math.Ceiling(c.InnerPawn.GetStatValue(StatDefOf.MeatAmount) / 75f)),
                    foreverButcher = butcherBills.Count == 1 && butcherBills[0].repeatMode == BillRepeatModeDefOf.Forever && !butcherBills[0].suspended && !butcherBills[0].paused };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
