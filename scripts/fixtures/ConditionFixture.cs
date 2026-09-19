using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Forces the three named map
    // conditions issue #408 responds to onto the baseline colony at once and
    // stages what each response needs to show a deficit:
    //
    //   solar flare   -- an enclosed roofed stockpile room holding warm raw
    //                    meat (the refrigeration review's at-risk stock) and
    //                    two fuelled wood benches with the simple-meal
    //                    recipe (a FueledStove and a Campfire), so the
    //                    cook-ahead bill has a bench even when the ordinary
    //                    cooking bill has already claimed one.
    //   eclipse       -- the FueledStove stands outdoors, unroofed, far from
    //                    any glower, so its interaction cell measures dark
    //                    only while the eclipse counts unroofed work cells.
    //   psychic drone -- the drone targets the colony's larger gender at the
    //                    BadHigh level. Every colonist's mood need is pinned
    //                    a tenth above their own minor-break threshold with
    //                    joy at .4, so only the drone's widened entry margin
    //                    opens a mood state, and only for the affected
    //                    gender.
    //
    // test/condition_end ends the three conditions (the recovery half) and
    // re-pins the moods (joy high) so the recovery read is about the margin,
    // not about where the moods drifted during the run. test/condition_inspect is the
    // independent native read: conditions still active, the stove's work
    // cell glow and the bills standing on both benches.
    //
    // Nothing here orders, builds, lights or bills anything on the
    // controller's behalf.
    public sealed class ConditionFixture
    {
        private const string FlareDef = "SolarFlare", EclipseDef = "Eclipse", DroneDef = "PsychicDrone";
        private const float MoodAboveThreshold = 0.10f, DroneJoy = 0.4f, RecoveredJoy = 0.9f;

        [Tool("test/condition_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: register a solar flare, an eclipse and a psychic drone on the current map, build one enclosed roofed stockpile room with warm raw meat, spawn an outdoor fuelled stove and a campfire with wood, and pin every colonist's mood a tenth above their break threshold.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int flareTicks = 60000, int eclipseTicks = 3 * 60000, int droneTicks = 2 * 60000)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                var builder = colonists.FirstOrDefault(p => !p.Drafted && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction));
                if (builder == null) return Refuse("No existing colonist able to construct.");
                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                var stoveDef = DefDatabase<ThingDef>.GetNamedSilentFail("FueledStove");
                var campfireDef = DefDatabase<ThingDef>.GetNamedSilentFail("Campfire");
                var meatDef = DefDatabase<ThingDef>.GetNamedSilentFail("Meat_Muffalo");
                if (wallDef == null || doorDef == null || stoveDef == null || campfireDef == null || meatDef == null)
                    return Refuse("Wall, Door, FueledStove, Campfire or Meat_Muffalo unavailable in this ruleset.");
                var flareDef = DefDatabase<GameConditionDef>.GetNamedSilentFail(FlareDef);
                var eclipseDef = DefDatabase<GameConditionDef>.GetNamedSilentFail(EclipseDef);
                var droneDef = DefDatabase<GameConditionDef>.GetNamedSilentFail(DroneDef);
                var heatDef = DefDatabase<GameConditionDef>.GetNamedSilentFail("HeatWave");
                if (flareDef == null || eclipseDef == null || droneDef == null)
                    return Refuse("SolarFlare, Eclipse or PsychicDrone condition unavailable in this ruleset.");
                foreach (var def in new[] { flareDef, eclipseDef, droneDef })
                    if (map.gameConditionManager.ConditionIsActive(def)) return Refuse(def.defName + " is already active on this map.");

                // 24x8 clearing: the meat room at (0..5, 2..5) with its door
                // on the west wall, the stove centred on (9,3) facing north
                // (occupying (8..10, 3), interaction cell (9,2)) and the
                // campfire at (21,3): eleven cells from the stove's work
                // cell, past its own glow radius, so the campfire lights
                // its own cell and not the stove's. Wood on row 0.
                int width = 24, height = 8;
                var origin = GenRadial.RadialCellsAround(builder.Position, 75, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && !map.roofGrid.Roofed(cell)
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building))
                    && !GenRadial.RadialCellsAround(new IntVec3(c.x + 9, 0, c.z + 2), 16, true).Any(cell => cell.InBounds(map)
                        && cell.GetThingList(map).Any(t => t.TryGetComp<CompGlower>() != null))
                    && builder.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable " + width + "x" + height + " area without nearby lamps for the fixture.");
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
                var room = new CellRect(origin.x, origin.z + 2, 6, 4);
                foreach (var cell in room.Cells)
                {
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    var edge = cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ;
                    if (!edge) continue;
                    if (cell == new IntVec3(room.minX, 0, room.minZ + 1)) Spawn(doorDef, cell, Rot4.North);
                    else Spawn(wallDef, cell, Rot4.North);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var interior = room.ContractedBy(1);
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                foreach (var cell in interior.Cells) zone.AddCell(cell);
                // Meat only, so the wood outside is never hauled into the
                // room and the refrigeration census sees raw meat alone.
                zone.settings.filter.SetDisallowAll();
                zone.settings.filter.SetAllow(meatDef, true);
                // Eight stacks (30 nutrition): the cook-ahead bill nets its
                // target against every standing meal bill's reserve, and the
                // ordinary cooking bill of eight colonists reserves 21.6.
                var meat = new List<string>();
                foreach (var cell in interior.Cells.Take(8))
                {
                    var stack = ThingMaker.MakeThing(meatDef);
                    stack.stackCount = meatDef.stackLimit;
                    GenSpawn.Spawn(stack, cell, map);
                    stack.SetForbidden(false, false);
                    meat.Add(stack.GetUniqueLoadID());
                }
                var stove = Spawn(stoveDef, At(9, 3), Rot4.North);
                var stoveFuel = stove.TryGetComp<CompRefuelable>();
                stoveFuel?.Refuel(stoveFuel.Props.fuelCapacity);
                var campfire = Spawn(campfireDef, At(21, 3), Rot4.North);
                var campfireFuel = campfire.TryGetComp<CompRefuelable>();
                campfireFuel?.Refuel(campfireFuel.Props.fuelCapacity);
                // Wood for torches (20 each) and bench refuelling, with margin.
                foreach (var x in new[] { 2, 4 })
                {
                    var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    wood.stackCount = 75;
                    GenPlace.TryPlaceThing(wood, At(x, 0), map, ThingPlaceMode.Direct);
                }
                var construction = builder.skills?.GetSkill(SkillDefOf.Construction);
                if (construction != null && construction.Level < 4) { construction.Level = 4; construction.xpSinceLastLevel = 0f; }
                foreach (var pawn in colonists)
                {
                    if (pawn.workSettings == null) continue;
                    if (!pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && pawn.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0)
                        pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                    var cooking = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking");
                    if (cooking != null && !pawn.WorkTypeIsDisabled(cooking) && pawn.workSettings.GetPriority(cooking) == 0)
                        pawn.workSettings.SetPriority(cooking, 1);
                }

                // The room is warm whatever the season: one heat wave already
                // ramped in (the refrigeration fixture's trick) and the room
                // set hot, so the meat is at-risk stock for the whole run.
                var heatWaves = 0;
                if (heatDef != null && map.mapTemperature.OutdoorTemp < 16f)
                {
                    var wave = GameConditionMaker.MakeCondition(heatDef, 4 * 60000 + 12000);
                    map.gameConditionManager.RegisterCondition(wave);
                    wave.startTick = Find.TickManager.TicksGame - 12000;
                    heatWaves = 1;
                }
                var inside = At(1, 3).GetRoom(map);
                if (inside == null || inside.OpenRoofCount > 0 || inside.TouchesMapEdge || inside.PsychologicallyOutdoors)
                    return Refuse("Fixture room is not enclosed after construction.");
                inside.Temperature = 30f;

                // The larger gender is the drone's target; ties go to Female.
                var females = colonists.Count(p => p.gender == Gender.Female);
                var gender = females >= colonists.Count - females ? Gender.Female : Gender.Male;
                var flare = GameConditionMaker.MakeCondition(flareDef, flareTicks);
                map.gameConditionManager.RegisterCondition(flare);
                // The eclipse darkens the sky over a 200-tick transition and
                // RegisterCondition clamps startTick to now, so it is backdated
                // after registration and the sky glow it would settle on is
                // forced at once: the stove's cell reads dark on the first
                // census instead of after the next sky update.
                const int eclipseRamp = 6000;
                var eclipse = GameConditionMaker.MakeCondition(eclipseDef, eclipseTicks + eclipseRamp);
                map.gameConditionManager.RegisterCondition(eclipse);
                eclipse.startTick = Find.TickManager.TicksGame - eclipseRamp;
                map.skyManager.ForceSetCurSkyGlow(0f);
                var drone = GameConditionMaker.MakeCondition(droneDef, droneTicks);
                if (drone is GameCondition_PsychicEmanation emanation)
                {
                    emanation.gender = gender;
                    emanation.level = PsychicDroneLevel.BadHigh;
                }
                else return Refuse(DroneDef + " is not a psychic emanation condition in this ruleset.");
                map.gameConditionManager.RegisterCondition(drone);
                var pinned = PinMoods(colonists, DroneJoy);

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                var workCell = stove.InteractionCell;
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, builder = builder.GetUniqueLoadID(),
                    origin = new { x = origin.x, z = origin.z },
                    stove = stove.GetUniqueLoadID(), workCell = new { x = workCell.x, z = workCell.z }, stoveRoofed = workCell.Roofed(map),
                    glow = map.glowGrid.GroundGlowAt(workCell),
                    campfire = campfire.GetUniqueLoadID(),
                    roomId = inside.ID, roomTemperatureC = inside.Temperature, outdoorTemperatureC = map.mapTemperature.OutdoorTemp, heatWaves,
                    interior = new { minX = interior.minX, minZ = interior.minZ, maxX = interior.maxX, maxZ = interior.maxZ }, meat,
                    conditions = new { flare = flareTicks, eclipse = eclipseTicks, drone = droneTicks },
                    droneGender = gender.ToString(),
                    affected = colonists.Where(p => p.gender == gender).Select(p => p.GetUniqueLoadID()).ToList(),
                    unaffected = colonists.Where(p => p.gender != gender).Select(p => p.GetUniqueLoadID()).ToList(),
                    moods = pinned,
                    setup = "Test-only registered conditions, enclosed roofed stockpile room with warm raw meat, outdoor fuelled stove and campfire with wood, pinned moods; lighting, bills and mood relief remain the controller's.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/condition_end", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: end the solar flare, eclipse and psychic drone test/condition_prepare registered and re-pin every colonist's mood a tenth above their break threshold with joy high.")]
        public async Task<object> End(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || !Find.TickManager.Paused) return Refuse("A paused disposable colony map is required.");
                var ended = new List<string>();
                foreach (var name in new[] { FlareDef, EclipseDef, DroneDef })
                {
                    var def = DefDatabase<GameConditionDef>.GetNamedSilentFail(name);
                    var condition = def == null ? null : map.gameConditionManager.GetActiveCondition(def);
                    if (condition == null) continue;
                    condition.End();
                    ended.Add(name);
                }
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                var pinned = PinMoods(colonists, RecoveredJoy);
                return new { success = true, tick = Find.TickManager.TicksGame, ended, active = Active(map), moods = pinned,
                    setup = "Test-only condition end and mood re-pin; recovery reads remain the controller's." };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/condition_inspect", Description = "Private disposable fixture read: the conditions still active, the fixture stove's work cell glow and roofing, and the bills standing on the fixture benches.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string stove, string campfire)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) return Refuse("No current map.");
                var benches = map.listerThings.AllThings.OfType<Building_WorkTable>().ToList();
                var stoveTable = benches.FirstOrDefault(b => b.GetUniqueLoadID() == stove);
                var fireTable = benches.FirstOrDefault(b => b.GetUniqueLoadID() == campfire);
                if (stoveTable == null || fireTable == null) return Refuse("The fixture stove or campfire no longer stands.");
                var workCell = stoveTable.InteractionCell;
                var lamps = GenRadial.RadialCellsAround(workCell, 3, true).Where(c => c.InBounds(map))
                    .SelectMany(c => c.GetThingList(map)).Where(t => t is Building && t.TryGetComp<CompGlower>() != null && t != stoveTable)
                    .Select(t => new { id = t.GetUniqueLoadID(), defName = t.def.defName, x = t.Position.x, z = t.Position.z, lit = t.TryGetComp<CompGlower>().Glows }).ToList();
                return new {
                    success = true, tick = Find.TickManager.TicksGame, active = Active(map),
                    workCell = new { x = workCell.x, z = workCell.z }, roofed = workCell.Roofed(map), glow = map.glowGrid.GroundGlowAt(workCell),
                    lamps, stoveBills = Bills(stoveTable), campfireBills = Bills(fireTable),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        // PinMoods sets every colonist's mood a fixed tenth above their own
        // minor-break threshold with the other needs high and joy as given:
        // a drone bearer's widened entry (up to .15) opens on the mood, an
        // unaffected pawn's ordinary entry (at the threshold) does not, and
        // at .4 only the bearer's joy counts as a cause (limit .5 against
        // .3). The recovery re-pin lifts joy to .9 so no retained cause
        // holds the state open once the margin is back to .05.
        private static List<object> PinMoods(List<Pawn> colonists, float joy)
        {
            var pinned = new List<object>();
            foreach (var pawn in colonists)
            {
                if (pawn.needs?.mood == null || pawn.mindState?.mentalBreaker == null) continue;
                var threshold = pawn.mindState.mentalBreaker.BreakThresholdMinor;
                pawn.needs.mood.CurLevel = threshold + MoodAboveThreshold;
                if (pawn.needs.joy != null) pawn.needs.joy.CurLevelPercentage = joy;
                if (pawn.needs.food != null) pawn.needs.food.CurLevelPercentage = 0.9f;
                if (pawn.needs.rest != null) pawn.needs.rest.CurLevelPercentage = 0.9f;
                pinned.Add(new { id = pawn.GetUniqueLoadID(), gender = pawn.gender.ToString(), threshold, mood = pawn.needs.mood.CurLevel });
            }
            return pinned;
        }

        private static List<object> Active(Map map) => map.gameConditionManager.ActiveConditions
            .Select(c => (object)new { defName = c.def.defName, ticksLeft = c.Permanent ? -1 : c.TicksLeft, permanent = c.Permanent }).ToList();

        private static List<object> Bills(Building_WorkTable table) => table.BillStack.Bills
            .Select(b => (object)new { recipe = b.recipe.defName, suspended = b.suspended, repeatMode = (b as Bill_Production)?.repeatMode?.defName,
                targetCount = (b as Bill_Production)?.targetCount, repeatCount = (b as Bill_Production)?.repeatCount }).ToList();

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
