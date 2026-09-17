using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Builds the unfloored kitchen
    // flooraccept needs to exercise MaintainFlooring (issue #6 slice 4): an
    // enclosed roofed room holding a fuelled stove whose interior stands on
    // bare soil (terrain cleanliness -1), with enough wood outside for a plank
    // floor over every interior cell. The controller must choose a floor it
    // can afford, order it on the deficient cells, and the measured census
    // must then read every cell non-natural with cleanliness >= 0.
    //
    // Nothing here orders, builds or places anything on the controller's
    // behalf.
    public sealed class FlooringFixture
    {
        [Tool("test/flooring_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: build one enclosed roofed kitchen (fuelled stove) whose interior cells stand on bare soil and spawn wood outside for a plank floor.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
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
                var soil = DefDatabase<TerrainDef>.GetNamedSilentFail("Soil");
                var plank = DefDatabase<TerrainDef>.GetNamedSilentFail("WoodPlankFloor");
                if (wallDef == null || doorDef == null || stoveDef == null || soil == null || plank == null)
                    return Refuse("Wall, Door, FueledStove, Soil or WoodPlankFloor unavailable in this ruleset.");
                if (!plank.BuildableByPlayer || plank.researchPrerequisites != null && plank.researchPrerequisites.Any(r => !r.IsFinished))
                    return Refuse("WoodPlankFloor is not buildable in this ruleset.");

                // 10x9 clearing: the room at (0..7, 2..6) with interior
                // (1..6, 3..5) on soil; concrete on the walls and the open rows
                // 0/1 for the wood pile and a spare far cell at (9,0) for the
                // harness's player building plan.
                const int width = 10, height = 9;
                var origin = GenRadial.RadialCellsAround(builder.Position, 75, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && !map.roofGrid.Roofed(cell)
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building))
                    && builder.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable 10x9 area for the fixture room.");
                var clearing = new CellRect(origin.x, origin.z, width, height);
                var room = new CellRect(origin.x, origin.z + 2, 8, 5);
                var interior = room.ContractedBy(1);
                foreach (var cell in clearing.Cells)
                {
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth).ToList()) thing.Destroy();
                    map.terrainGrid.SetTerrain(cell, interior.Contains(cell) ? soil : TerrainDefOf.Concrete);
                    map.areaManager.Home[cell] = true;
                }
                IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);
                Thing Spawn(ThingDef def, IntVec3 cell, Rot4 rotation)
                {
                    var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    thing.SetFaction(player);
                    return GenSpawn.Spawn(thing, cell, map, rotation);
                }
                foreach (var cell in room.Cells)
                {
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    var edge = cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ;
                    if (!edge) continue;
                    if (cell == new IntVec3(room.minX, 0, room.minZ + 1)) Spawn(doorDef, cell, Rot4.North);
                    else Spawn(wallDef, cell, Rot4.North);
                }
                // FueledStove is 3x1 facing north: centred on (4,5) it occupies
                // (3..5, 5) against the north wall and makes the room a kitchen.
                var stove = Spawn(stoveDef, At(4, 5), Rot4.North);
                stove.TryGetComp<CompRefuelable>()?.Refuel(stove.TryGetComp<CompRefuelable>().Props.fuelCapacity);
                // Wood for 18 plank cells (3 each) with margin, outside the room.
                var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                wood.stackCount = 75;
                GenPlace.TryPlaceThing(wood, At(2, 0), map, ThingPlaceMode.Direct);
                var construction = builder.skills?.GetSkill(SkillDefOf.Construction);
                if (construction != null && construction.Level < 4) { construction.Level = 4; construction.xpSinceLastLevel = 0f; }
                // A ranked project only runs while no emergency is active, so
                // a random starting injury must not hold the floor behind
                // CriticalMedical; every capable colonist may build.
                foreach (var pawn in colonists)
                {
                    foreach (var h in pawn.health.hediffSet.hediffs.Where(h => h.def.isBad).ToList()) pawn.health.RemoveHediff(h);
                    if (pawn.workSettings != null && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && pawn.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0)
                        pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var kitchen = At(4, 4).GetRoom(map);
                if (kitchen == null || kitchen.OpenRoofCount > 0 || kitchen.TouchesMapEdge || kitchen.PsychologicallyOutdoors)
                    return Refuse("Fixture room is not enclosed after construction.");
                var soilCells = interior.Cells.Count(c => c.GetTerrain(map) == soil);
                if (soilCells != interior.Area) return Refuse("Interior is not entirely soil after preparation.");
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, builder = builder.GetUniqueLoadID(),
                    roomId = kitchen.ID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    role = kitchen.Role?.defName,
                    interior = new { minX = interior.minX, minZ = interior.minZ, maxX = interior.maxX, maxZ = interior.maxZ },
                    cells = interior.Area, stove = stove.GetUniqueLoadID(),
                    spareCell = new { x = At(9, 0).x, z = At(9, 0).z },
                    setup = "Test-only enclosed roofed kitchen whose interior stands on bare soil, wood outside; every floor choice, placement and latch remains the controller's.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
