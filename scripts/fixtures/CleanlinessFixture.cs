using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Builds the rooms cleanaccept needs
    // to exercise MaintainCleanFacilities' bounded response and the
    // kitchen/butcher separation rule (issue #6 slice 2):
    //
    //   filthy     -- an enclosed roofed kitchen (fuelled stove) and an
    //                 enclosed roofed butchery (butcher spot), each with blood
    //                 filth on its floor. Every colonist has Cleaning at
    //                 priority 0 (timetables cannot do this: a rested pawn
    //                 in a Sleep or Joy slot still falls through to work), so
    //                 ordinary work coverage has failed outright: the
    //                 controller's latch, immediate response and direct
    //                 player-forced order are what gets tested, and the
    //                 butchery's filth must stay untouched as inherently
    //                 dirty.
    //   separation -- one enclosed kitchen holding both a fuelled stove and
    //                 a butcher spot, no colony food and an armed colonist,
    //                 so the food-supply family wants butchery and must admit
    //                 a fresh ButcherSpot outside the kitchen rather than
    //                 count the co-located one.
    //
    // Nothing here orders, cleans or places anything on the controller's
    // behalf.
    public sealed class CleanlinessFixture
    {
        [Tool("test/cleanliness_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: build a filthy kitchen and a filthy butchery with every colonist's Cleaning priority at 0 (filthy), or one kitchen sharing a stove and a butcher spot with no colony food and an armed colonist (separation).")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, string scenario = "filthy", int filthPerRoom = 3)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (scenario != "filthy" && scenario != "separation") return Refuse("scenario must be filthy or separation.");
                if (filthPerRoom < 1 || filthPerRoom > 6) return Refuse("filthPerRoom must be 1..6.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                var builder = colonists.FirstOrDefault(p => !p.Drafted && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction));
                if (builder == null) return Refuse("No existing colonist able to construct.");
                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                var stoveDef = DefDatabase<ThingDef>.GetNamedSilentFail("FueledStove");
                var butcherDef = DefDatabase<ThingDef>.GetNamedSilentFail("ButcherSpot");
                var bloodDef = DefDatabase<ThingDef>.GetNamedSilentFail("Filth_Blood");
                if (wallDef == null || doorDef == null || stoveDef == null || butcherDef == null || bloodDef == null)
                    return Refuse("Wall, Door, FueledStove, ButcherSpot or Filth_Blood unavailable in this ruleset.");

                // 14x8 clearing: kitchen at (0..5, 2..5), second room (filthy)
                // at (7..12, 2..5), open ground elsewhere for stock and for the
                // controller's own butcher spot; a spare far cell at (13,0) for
                // the harness's player building plan.
                const int width = 14, height = 8;
                var origin = GenRadial.RadialCellsAround(builder.Position, 75, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && !map.roofGrid.Roofed(cell)
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building))
                    && builder.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable 14x8 area for the fixture rooms.");
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
                // Walls, a west door on the second row and a constructed roof
                // over every cell including the door, so the room is enclosed.
                void BuildRoom(CellRect rect)
                {
                    foreach (var cell in rect.Cells)
                    {
                        map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                        var edge = cell.x == rect.minX || cell.x == rect.maxX || cell.z == rect.minZ || cell.z == rect.maxZ;
                        if (!edge) continue;
                        if (cell == new IntVec3(rect.minX, 0, rect.minZ + 1)) Spawn(doorDef, cell, Rot4.North);
                        else Spawn(wallDef, cell, Rot4.North);
                    }
                }
                List<string> MakeFilth(CellRect interior, int count)
                {
                    var ids = new List<string>();
                    foreach (var cell in interior.Cells.OrderBy(c => c.z).ThenBy(c => c.x).Take(count))
                    {
                        if (!FilthMaker.TryMakeFilth(cell, map, bloodDef, 1, FilthSourceFlags.None)) continue;
                        var filth = cell.GetThingList(map).OfType<Filth>().FirstOrDefault(f => f.def == bloodDef);
                        if (filth != null) ids.Add(filth.GetUniqueLoadID());
                    }
                    return ids;
                }
                object Cells(CellRect rect) => new { minX = rect.minX, minZ = rect.minZ, maxX = rect.maxX, maxZ = rect.maxZ };

                var kitchen = new CellRect(origin.x, origin.z + 2, 6, 4);
                BuildRoom(kitchen);
                // FueledStove is 3x1: centred on (3,3) it occupies (2..4, 3).
                var stove = Spawn(stoveDef, At(3, 3), Rot4.North);
                stove.TryGetComp<CompRefuelable>()?.Refuel(stove.TryGetComp<CompRefuelable>().Props.fuelCapacity);
                Thing sharedSpot = null, butcherSpot = null;
                CellRect butchery = default;
                var kitchenFilth = new List<string>();
                var butcheryFilth = new List<string>();
                if (scenario == "filthy")
                {
                    butchery = new CellRect(origin.x + 7, origin.z + 2, 6, 4);
                    BuildRoom(butchery);
                    butcherSpot = Spawn(butcherDef, At(10, 4), Rot4.North);
                }
                else
                {
                    sharedSpot = Spawn(butcherDef, At(1, 4), Rot4.North);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var kitchenRoom = At(2, 4).GetRoom(map);
                if (kitchenRoom == null || kitchenRoom.OpenRoofCount > 0 || kitchenRoom.TouchesMapEdge || kitchenRoom.PsychologicallyOutdoors)
                    return Refuse("Fixture kitchen is not enclosed after construction.");
                Room butcheryRoom = null;
                if (scenario == "filthy")
                {
                    butcheryRoom = At(9, 4).GetRoom(map);
                    if (butcheryRoom == null || butcheryRoom.OpenRoofCount > 0 || butcheryRoom.TouchesMapEdge || butcheryRoom.PsychologicallyOutdoors || butcheryRoom.ID == kitchenRoom.ID)
                        return Refuse("Fixture butchery is not a separate enclosed room after construction.");
                    // Filth on the interior floor cells the benches do not occupy:
                    // the kitchen's row 4 (x 1..4) and the butchery's row 3.
                    kitchenFilth = MakeFilth(new CellRect(origin.x + 1, origin.z + 4, 4, 1), filthPerRoom);
                    butcheryFilth = MakeFilth(new CellRect(origin.x + 8, origin.z + 3, 4, 1), filthPerRoom);
                    if (kitchenFilth.Count != filthPerRoom || butcheryFilth.Count != filthPerRoom)
                        return Refuse("Fixture filth did not spawn as requested.");
                    // Cleaning at priority 0 for everyone: no ordinary
                    // work coverage exists, while a direct player-forced
                    // order still runs (the Work tab priority is not a
                    // capability).
                    foreach (var pawn in colonists)
                        if (pawn.workSettings != null && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Cleaning))
                            pawn.workSettings.SetPriority(WorkTypeDefOf.Cleaning, 0);
                }
                else
                {
                    // No colony food: the food-supply family's butchery path
                    // gates on runway under the target and an armed hunter.
                    foreach (var thing in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible && !(t is Corpse)).ToList())
                        thing.Destroy();
                    if (builder.equipment != null && builder.equipment.Primary == null)
                    {
                        var bowDef = DefDatabase<ThingDef>.GetNamedSilentFail("Bow_Short");
                        if (bowDef == null) return Refuse("Bow_Short unavailable in this ruleset.");
                        builder.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(bowDef));
                    }
                    var construction = builder.skills?.GetSkill(SkillDefOf.Construction);
                    if (construction != null && construction.Level < 4) { construction.Level = 4; construction.xpSinceLastLevel = 0f; }
                    if (builder.workSettings != null && builder.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0)
                        builder.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                kitchenRoom = At(2, 4).GetRoom(map);
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, builder = builder.GetUniqueLoadID(), scenario,
                    kitchenRoomId = kitchenRoom.ID.ToString(System.Globalization.CultureInfo.InvariantCulture), kitchen = Cells(kitchen),
                    kitchenRole = kitchenRoom.Role?.defName, kitchenCleanliness = kitchenRoom.GetStat(RoomStatDefOf.Cleanliness),
                    butcheryRoomId = butcheryRoom?.ID.ToString(System.Globalization.CultureInfo.InvariantCulture), butchery = butcheryRoom != null ? Cells(butchery) : null,
                    stove = stove.GetUniqueLoadID(), butcherSpot = butcherSpot?.GetUniqueLoadID(), sharedButcherSpot = sharedSpot?.GetUniqueLoadID(),
                    kitchenFilth, butcheryFilth, colonists = colonists.Select(p => p.GetUniqueLoadID()).ToList(),
                    spareCell = new { x = At(13, 0).x, z = At(13, 0).z },
                    setup = "Test-only enclosed rooms with a fuelled stove, butcher spot and blood filth (filthy: Cleaning priority 0 for every colonist; separation: no colony food, armed colonist); every clean order, latch and placement remains the controller's.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
