using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Builds the sealed stockpile
    // routeaccept needs to exercise MaintainRoutes (issue #6 slice 5): a
    // roofed 3x3 room walled on every side with no door, holding a stockpile
    // zone, with wood outside for a door. The native reachability census must
    // read the stockpile unreachable by every colonist (the game's own
    // pathing, not a flood fill), the controller must cut a door into one of
    // the listed breach walls, and the next measured census must read it
    // reachable with a measured path.
    //
    // Nothing here orders, builds or places anything on the controller's
    // behalf.
    public sealed class RoutesFixture
    {
        [Tool("test/routes_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: build one sealed roofed 3x3 room (walls, no door) holding a stockpile zone and spawn wood outside for a door.")]
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
                if (wallDef == null || doorDef == null) return Refuse("Wall or Door unavailable in this ruleset.");
                if (!doorDef.BuildableByPlayer || doorDef.researchPrerequisites != null && doorDef.researchPrerequisites.Any(r => !r.IsFinished))
                    return Refuse("Door is not buildable in this ruleset.");

                // 9x9 clearing on concrete: the room at (2..6, 2..6) with
                // interior (3..5, 3..5); the wood pile at (0,0); every wall
                // has a standable outer neighbour.
                const int width = 9, height = 9;
                var origin = GenRadial.RadialCellsAround(builder.Position, 75, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && !map.roofGrid.Roofed(cell)
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building))
                    && builder.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable 9x9 area for the fixture room.");
                var clearing = new CellRect(origin.x, origin.z, width, height);
                var room = new CellRect(origin.x + 2, origin.z + 2, 5, 5);
                var interior = room.ContractedBy(1);
                foreach (var cell in clearing.Cells)
                {
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth).ToList()) thing.Destroy();
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
                    map.areaManager.Home[cell] = true;
                }
                IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);
                foreach (var cell in room.Cells)
                {
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    var edge = cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ;
                    if (!edge) continue;
                    var wall = ThingMaker.MakeThing(wallDef, ThingDefOf.WoodLog);
                    wall.SetFaction(player);
                    GenSpawn.Spawn(wall, cell, map, Rot4.North);
                }
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                foreach (var cell in interior.Cells) zone.AddCell(cell);
                // Wood for a door (25) with margin, outside the room.
                var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                wood.stackCount = 75;
                GenPlace.TryPlaceThing(wood, At(0, 0), map, ThingPlaceMode.Direct);
                var construction = builder.skills?.GetSkill(SkillDefOf.Construction);
                if (construction != null && construction.Level < 4) { construction.Level = 4; construction.xpSinceLastLevel = 0f; }
                // A ranked project only runs while no emergency is active, so
                // a random starting injury must not hold the door behind
                // CriticalMedical; every capable colonist may build.
                foreach (var pawn in colonists)
                {
                    foreach (var h in pawn.health.hediffSet.hediffs.Where(h => h.def.isBad).ToList()) pawn.health.RemoveHediff(h);
                    if (pawn.workSettings != null && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && pawn.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0)
                        pawn.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var sealedRoom = At(4, 4).GetRoom(map);
                if (sealedRoom == null || sealedRoom.OpenRoofCount > 0 || sealedRoom.TouchesMapEdge || sealedRoom.PsychologicallyOutdoors)
                    return Refuse("Fixture room is not enclosed after construction.");
                if (colonists.Any(p => p.CanReach(At(4, 4), Verse.AI.PathEndMode.OnCell, Danger.Some)))
                    return Refuse("A colonist can still reach the sealed interior.");
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, builder = builder.GetUniqueLoadID(),
                    facility = "zone-" + zone.ID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    roomId = sealedRoom.ID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    room = new { minX = room.minX, minZ = room.minZ, maxX = room.maxX, maxZ = room.maxZ },
                    interior = new { minX = interior.minX, minZ = interior.minZ, maxX = interior.maxX, maxZ = interior.maxZ },
                    colonists = colonists.Count,
                    setup = "Test-only sealed roofed stockpile room, wood outside; every breach choice, placement and latch remains the controller's.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
