using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Disposable inputs only; production builds exclude this class.
    public sealed class SleepingFixture
    {
        // The room is sited from the colonists' average position, inside the
        // native 22-cell planning radius, so the controller's bed placement
        // sees its cells. Every colonist but the fixture pawn already owns a
        // bed in it (a one-bed shortage): MaintainSleeping must build the
        // missing bed, ownership must follow, and the goal recovers only once
        // every colonist has been observed sleeping in their own bed.
        [Tool("test/sleeping_setup", Description = "Prepare a disposable roofed warm room with one bed fewer than colonists, and construction wood.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken, bool connectedRooms = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                try {
                    var map = Find.CurrentMap;
                    var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState).OrderBy(p => p.thingIDNumber).ToList();
                    if (people.Count < (connectedRooms ? 2 : 1) || people.Count > 8) throw new InvalidOperationException("Insufficient colonists or more than eight.");
                    if (connectedRooms) Find.PlaySettings.autoHomeArea = false;
                    var p = people.First();
                    var prerequisites = ThingDefOf.Bed.researchPrerequisites;
                    if (prerequisites != null)
                        foreach (var project in prerequisites) Find.ResearchManager.FinishProject(project, false);
                    foreach (var bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>().ToList())
                        foreach (var owner in bed.OwnersForReading.ToList()) owner.ownership.UnclaimBed();
                    var center = new IntVec3((int)people.Average(x => x.Position.x), 0, (int)people.Average(x => x.Position.z));
                    const int size = 9;
                    // Plants (trees included) are cleared below, so only
                    // edifices, zones, pawns and terrain disqualify a site;
                    // the radius covers a wooded or rocky landing.
                    var site = GenRadial.RadialCellsAround(center, 45, true).Where(c => c.DistanceToSquared(center) >= 36).Cast<IntVec3?>().FirstOrDefault(c =>
                        new CellRect(c.Value.x, c.Value.z, connectedRooms ? 19 : size, size).Cells.All(v => v.InBounds(map)
                            && !v.Fogged(map) && v.GetEdifice(map) == null
                            && v.GetThingList(map).All(t => t is Plant || t.def.category == ThingCategory.Item)
                            && map.zoneManager.ZoneAt(v) == null && v.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                    if (site == null) throw new InvalidOperationException("No clear 9x9 site within 45 cells of the colonists.");
                    var origin = site.Value;
                    var rect = new CellRect(origin.x, origin.z, size, size);
                    foreach (var cell in rect) {
                        foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();
                        map.areaManager.Home[cell] = true;
                        map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    }
                    foreach (var cell in rect.EdgeCells) {
                        var wall = ThingMaker.MakeThing(cell == origin + new IntVec3(size / 2, 0, 0) ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog);
                        wall.SetFaction(Faction.OfPlayerSilentFail);
                        GenSpawn.Spawn(wall, cell, map);
                    }
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                    var floorCell = origin + new IntVec3(1, 0, 1);
                    floorCell.GetRoom(map).Temperature = 21f;
                    var heater = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Campfire"));
                    heater.SetFaction(Faction.OfPlayerSilentFail);
                    GenSpawn.Spawn(heater, origin + new IntVec3(size - 2, 0, size - 2), map);
                    var fuel = heater.TryGetComp<CompRefuelable>();
                    fuel.Refuel(fuel.Props.fuelCapacity);
                    var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    wood.stackCount = wood.def.stackLimit;
                    GenPlace.TryPlaceThing(wood, origin + new IntVec3(size / 2, 0, size - 2), map, ThingPlaceMode.Near);
                    wood.SetForbidden(false, false);
                    if (connectedRooms) {
                        var extraWood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                        extraWood.stackCount = extraWood.def.stackLimit;
                        GenPlace.TryPlaceThing(extraWood, origin + new IntVec3(size / 2, 0, size - 2), map, ThingPlaceMode.Near);
                        extraWood.SetForbidden(false, false);
                    }
                    // Beds for everyone but the fixture pawn: vertical 1x2 beds in
                    // columns 1,3,5,7 and rows 1 and 4 of the 7x7 interior, which
                    // leaves free 1x2 sites for the controller's bed.
                    var owned = new System.Collections.Generic.List<object>();
                    var slots = new[] { 1, 3, 5, 7 }.SelectMany(x => new[] { 1, 4 }.Select(z => new IntVec3(origin.x + x, 0, origin.z + z))).ToList();
                    foreach (var (pawn, index) in people.Skip(connectedRooms ? 2 : 1).Select((pawn, index) => (pawn, index))) {
                        var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                        bed.SetFaction(Faction.OfPlayerSilentFail);
                        GenSpawn.Spawn(bed, slots[index], map, Rot4.North);
                        bed.SetForbidden(false, false);
                        if (!pawn.ownership.ClaimBedIfNonMedical(bed) || pawn.ownership.OwnedBed != bed) throw new InvalidOperationException("Colonist could not claim a fixture bed.");
                        owned.Add(new { pawn = pawn.GetUniqueLoadID(), bed = bed.GetUniqueLoadID(), x = bed.Position.x, z = bed.Position.z });
                    }
                    if (connectedRooms) PrepareHomeRooms(map, origin, rect);
                    foreach (var worker in people) {
                        worker.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                        worker.needs.rest.CurLevelPercentage = .95f;
                        worker.needs.food.CurLevelPercentage = .95f;
                        for (int i = 0; i < 24; i++) worker.timetable.SetAssignment(i, TimeAssignmentDefOf.Anything);
                        if (!worker.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) worker.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                        worker.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                    return new { success = true, pawn = p.GetUniqueLoadID(), colonists = people.Count, ownedBeds = owned, research = prerequisites?.Select(r => r.defName).ToArray(),
                        x = floorCell.x, z = floorCell.z, roomTemperatureC = floorCell.GetRoom(map).Temperature,
                        room = new { x = origin.x, z = origin.z, width = size, height = size },
                        corridor = new { x = origin.x + 12, z = origin.z + 3 },
                        outside = new { x = origin.x + 18, z = origin.z + 6 } };
                } catch (Exception error) { return new { success = false, error = error.ToString() }; }
            }, cancellationToken).ConfigureAwait(false);
        }

        // Two 1x2 bed chambers have only one legal Bed footprint each. The
        // roofed corridor joins them through doors, independently of the
        // existing dormitory. No controller-owned facility is fixture-spawned.
        private static void PrepareHomeRooms(Map map, IntVec3 origin, CellRect dormitory)
        {
            foreach (var c in dormitory.ContractedBy(1).Cells.Where(c => c.GetEdifice(map) == null).ToList()) {
                var chair = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog);
                chair.SetFaction(Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(chair, c, map);
            }
            var footprint = new CellRect(origin.x + 9, origin.z + 2, 9, 4);
            foreach (var c in footprint) {
                foreach (var thing in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();
                int x = c.x - origin.x, z = c.z - origin.z;
                bool chamber = (x == 10 || x == 16) && (z == 3 || z == 4);
                bool corridor = x >= 12 && x <= 14 && z == 3;
                bool door = (x == 11 || x == 15) && z == 3 || x == 13 && z == 2;
                if (!chamber && !corridor) {
                    var wall = ThingMaker.MakeThing(door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog);
                    wall.SetFaction(Faction.OfPlayerSilentFail);
                    GenSpawn.Spawn(wall, c, map);
                }
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            foreach (var c in footprint)
                if (c.GetRoom(map) is Room room && !room.TouchesMapEdge) room.Temperature = 21f;
        }

        [Tool("test/sleeping_need", Description = "Prepare low rest on an exact disposable test colonist.")]
        public async Task<object> Need(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact fixture pawn ID.")] string pawn)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var p = Find.CurrentMap.mapPawns.FreeColonistsSpawned.Single(x => x.GetUniqueLoadID() == pawn);
                p.needs.rest.CurLevelPercentage = .05f;
                p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                return new { success = true, rest = p.needs.rest.CurLevelPercentage };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
