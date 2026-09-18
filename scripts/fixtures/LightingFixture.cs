using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Builds the dark work room
    // lightaccept needs to exercise MaintainLighting (issue #6 slice 3):
    //
    //   dark   -- an enclosed roofed room holding a fuelled stove whose
    //             interaction cell is measured dark, no lamp anywhere in
    //             reach, and enough wood outside for a torch. The controller
    //             must place an affordable lamp beside the interaction cell
    //             and the measured glow must then read lit.
    //   outage -- the same room with a StandingLamp beside the stove that
    //             has no power network at all. The lamp reaches the cell but
    //             does not glow; the controller must hold for the power
    //             family rather than double up with a torch.
    //   partial -- a wider room with a lit, fuelled TorchLamp at the far end
    //             whose glow radius reaches the interaction cell but whose
    //             light has fallen off below lit by then. The bench is
    //             partially lit; the controller must place a lamp of its own
    //             beside the cell rather than defer to the far torch.
    //   fungus -- the dark room grows a cave plant (Glowstool: dies to
    //             light) on a soil cell. The room is protected: the census
    //             must mark the work cell light-sensitive and the controller
    //             must never latch it nor place a lamp.
    //
    // test/lighting_disrupt is the layout change of the repair case
    // (light/repair, issue #161): it removes the lamp standing on a cell
    // once the controller has lit the room, so the next census measures the
    // bench dark again and the controller must replace the lamp.
    //
    // Nothing here orders, builds or places anything on the controller's
    // behalf.
    public sealed class LightingFixture
    {
        [Tool("test/lighting_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: build one enclosed roofed room with a fuelled stove whose interaction cell is dark and spawn wood for a torch (dark), optionally with an unpowered StandingLamp in reach (outage), a lit torch at the far end of a wider room (partial) or a cave plant in the room (fungus).")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, string scenario = "dark")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (scenario != "dark" && scenario != "outage" && scenario != "partial" && scenario != "fungus") return Refuse("scenario must be dark, outage, partial or fungus.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                var builder = colonists.FirstOrDefault(p => !p.Drafted && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction));
                if (builder == null) return Refuse("No existing colonist able to construct.");
                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                var stoveDef = DefDatabase<ThingDef>.GetNamedSilentFail("FueledStove");
                var lampDef = DefDatabase<ThingDef>.GetNamedSilentFail("StandingLamp");
                var torchDef = DefDatabase<ThingDef>.GetNamedSilentFail("TorchLamp");
                var fungusDef = DefDatabase<ThingDef>.GetNamedSilentFail("Glowstool");
                if (wallDef == null || doorDef == null || stoveDef == null || lampDef == null || torchDef == null)
                    return Refuse("Wall, Door, FueledStove, StandingLamp or TorchLamp unavailable in this ruleset.");
                if (scenario == "fungus" && (fungusDef == null || fungusDef.plant == null || !fungusDef.plant.diesToLight))
                    return Refuse("Glowstool is not a plant that dies to light in this ruleset.");

                // 10x9 clearing: the room at (0..7, 2..6) with interior
                // (1..6, 3..5); open ground on row 0/1 for the wood pile and a
                // spare far cell at (9,0) for the harness's player building
                // plan. Every lamp already glowing within 16 cells of the
                // clearing is excluded so the interaction cell starts dark.
                // partial widens the room to (0..14, 2..6) so a torch on the
                // far interior column (13) sits 9 cells from the interaction
                // cell: inside its glow radius (10) but past where the light
                // still counts as lit.
                var roomWidth = scenario == "partial" ? 15 : 8;
                int width = roomWidth + 2, height = 9;
                var origin = GenRadial.RadialCellsAround(builder.Position, 75, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && !map.roofGrid.Roofed(cell)
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building))
                    && !GenRadial.RadialCellsAround(new IntVec3(c.x + 4, 0, c.z + 4), 16, true).Any(cell => cell.InBounds(map)
                        && cell.GetThingList(map).Any(t => t.TryGetComp<CompGlower>() != null))
                    && builder.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable " + width + "x" + height + " area without nearby lamps for the fixture room.");
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
                var room = new CellRect(origin.x, origin.z + 2, roomWidth, 5);
                foreach (var cell in room.Cells)
                {
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    var edge = cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ;
                    if (!edge) continue;
                    if (cell == new IntVec3(room.minX, 0, room.minZ + 1)) Spawn(doorDef, cell, Rot4.North);
                    else Spawn(wallDef, cell, Rot4.North);
                }
                // FueledStove is 3x1 facing north: centred on (4,5) it occupies
                // (3..5, 5) against the north wall with its interaction cell
                // at (4,4), inside the room.
                var stove = Spawn(stoveDef, At(4, 5), Rot4.North);
                stove.TryGetComp<CompRefuelable>()?.Refuel(stove.TryGetComp<CompRefuelable>().Props.fuelCapacity);
                Thing lamp = null;
                if (scenario == "outage")
                    lamp = Spawn(lampDef, At(2, 4), Rot4.North);
                if (scenario == "partial")
                {
                    lamp = Spawn(torchDef, At(roomWidth - 2, 4), Rot4.North);
                    var torchFuel = lamp.TryGetComp<CompRefuelable>();
                    torchFuel?.Refuel(torchFuel.Props.fuelCapacity);
                }
                Thing plant = null;
                if (scenario == "fungus")
                {
                    // Soil under the plant so it does not die of infertile
                    // ground during the hold; only light may kill it.
                    map.terrainGrid.SetTerrain(At(1, 3), TerrainDefOf.Soil);
                    plant = GenSpawn.Spawn(fungusDef, At(1, 3), map);
                    if (plant is Plant grown) grown.Growth = 1f;
                }
                // Wood for the torch (20) with margin, on the open row outside.
                var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                wood.stackCount = 75;
                GenPlace.TryPlaceThing(wood, At(2, 0), map, ThingPlaceMode.Direct);
                var construction = builder.skills?.GetSkill(SkillDefOf.Construction);
                if (construction != null && construction.Level < 4) { construction.Level = 4; construction.xpSinceLastLevel = 0f; }
                // A ranked project only runs while no emergency is active, so
                // a random starting injury must not hold the lamp behind
                // CriticalMedical; every capable colonist may build.
                foreach (var pawn in colonists)
                {
                    foreach (var h in pawn.health.hediffSet.hediffs.Where(h => h.def.isBad).ToList()) pawn.health.RemoveHediff(h);
                    if (pawn.workSettings != null && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && pawn.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0)
                        pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var workRoom = At(4, 4).GetRoom(map);
                if (workRoom == null || workRoom.OpenRoofCount > 0 || workRoom.TouchesMapEdge || workRoom.PsychologicallyOutdoors)
                    return Refuse("Fixture room is not enclosed after construction.");
                if (stove.InteractionCell != At(4, 4)) return Refuse("Stove interaction cell is not the expected interior cell.");
                var glow = map.glowGrid.GroundGlowAt(stove.InteractionCell);
                if (glow >= 0.3f) return Refuse("Stove interaction cell measures lit (" + glow.ToString("0.00") + ") before the controller acts.");
                if (lamp != null && scenario == "partial" && lamp.TryGetComp<CompGlower>()?.Glows != true) return Refuse("The far torch does not glow.");
                if (plant != null && plant.Position.GetRoom(map) != workRoom) return Refuse("The cave plant is not in the work room.");
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, builder = builder.GetUniqueLoadID(), scenario,
                    roomId = workRoom.ID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    interior = new { minX = room.minX + 1, minZ = room.minZ + 1, maxX = room.maxX - 1, maxZ = room.maxZ - 1 },
                    stove = stove.GetUniqueLoadID(), workCell = new { x = stove.InteractionCell.x, z = stove.InteractionCell.z }, glow,
                    lamp = lamp?.GetUniqueLoadID(), lampCell = lamp == null ? null : new { x = lamp.Position.x, z = lamp.Position.z },
                    lampGlowRadius = lamp?.TryGetComp<CompGlower>()?.Props.glowRadius,
                    plant = plant?.GetUniqueLoadID(), plantCell = plant == null ? null : new { x = plant.Position.x, z = plant.Position.z },
                    spareCell = new { x = At(9, 0).x, z = At(9, 0).z },
                    setup = "Test-only enclosed roofed room with a fuelled stove whose interaction cell is dark, wood outside, optionally an unpowered StandingLamp in reach; every lamp choice, placement and latch remains the controller's.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/lighting_inspect", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: report the plant standing on a cell (alive, growth, dying) and the measured ground glow there.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null) return Refuse("A disposable colony map is required.");
                var cell = new IntVec3(x, 0, z);
                if (!cell.InBounds(map)) return Refuse("Cell out of bounds.");
                var plant = cell.GetPlant(map);
                return new {
                    success = true, tick = Find.TickManager.TicksGame, glow = map.glowGrid.GroundGlowAt(cell),
                    plant = plant?.GetUniqueLoadID(), plantDef = plant?.def.defName, alive = plant != null && !plant.Destroyed,
                    growth = plant?.Growth, dying = plant?.Dying, diesToLight = plant?.def.plant?.diesToLight,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/lighting_disrupt", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: remove the lamp standing on a cell (a layout change after the controller lit the room) and report the glow left on a work cell.")]
        public async Task<object> Disrupt(IRimBridgeContext ctx, CancellationToken cancellationToken, int x, int z, int workX, int workZ)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || !Find.TickManager.Paused) return Refuse("A paused disposable colony map is required.");
                var cell = new IntVec3(x, 0, z);
                var workCell = new IntVec3(workX, 0, workZ);
                if (!cell.InBounds(map) || !workCell.InBounds(map)) return Refuse("Cell out of bounds.");
                var lamp = cell.GetThingList(map).FirstOrDefault(t => t is Building && t.TryGetComp<CompGlower>() != null);
                if (lamp == null) return Refuse("No lamp stands on the cell.");
                var removed = lamp.GetUniqueLoadID(); var removedDef = lamp.def.defName;
                // Vanish, not deconstruct: no refunded wood left on the room's
                // floor to take a placement cell from the replacement lamp.
                lamp.Destroy(DestroyMode.Vanish);
                return new {
                    success = true, tick = Find.TickManager.TicksGame, removed, removedDef,
                    cell = new { x = cell.x, z = cell.z }, glow = map.glowGrid.GroundGlowAt(workCell),
                    setup = "Test-only removal of one standing lamp; the replacement lamp, its placement and the latch remain the controller's.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
