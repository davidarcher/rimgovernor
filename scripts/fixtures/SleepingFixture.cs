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
        [Tool("test/sleeping_setup", Description = "Prepare a disposable roofed warm room with one bed fewer than colonists (bedsForAll: one each), and construction wood. couple: the first two colonists become lovers, both bedless, beside one vacant double bed.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken, bool connectedRooms = false, bool bedsForAll = false, bool couple = false, bool greedy = false, bool jealous_ascetic = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                try {
                    var map = Find.CurrentMap;
                    var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState).OrderBy(p => p.thingIDNumber).ToList();
                    if (people.Count < (connectedRooms || couple || jealous_ascetic ? 2 : 1) || people.Count > 8) throw new InvalidOperationException("Insufficient colonists or more than eight.");
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
                        new CellRect(c.Value.x, c.Value.z, connectedRooms || greedy || jealous_ascetic ? 19 : size, jealous_ascetic ? 11 : size).Cells.All(v => v.InBounds(map)
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
                    // Beds for everyone but the fixture pawn (everyone under bedsForAll): vertical 1x2 beds in
                    // columns 1,3,5,7 and rows 1 and 4 of the 7x7 interior, which
                    // leaves free 1x2 sites for the controller's bed.
                    var owned = new System.Collections.Generic.List<object>();
                    var slots = new[] { 1, 3, 5, 7 }.SelectMany(x => new[] { 1, 4 }.Select(z => new IntVec3(origin.x + x, 0, origin.z + z))).ToList();
                    foreach (var (pawn, index) in people.Skip(couple || jealous_ascetic ? 2 : bedsForAll ? 0 : connectedRooms ? 2 : 1).Select((pawn, index) => (pawn, index))) {
                        var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                        bed.SetFaction(Faction.OfPlayerSilentFail);
                        GenSpawn.Spawn(bed, slots[index], map, Rot4.North);
                        bed.SetForbidden(false, false);
                        if (!pawn.ownership.ClaimBedIfNonMedical(bed) || pawn.ownership.OwnedBed != bed) throw new InvalidOperationException("Colonist could not claim a fixture bed.");
                        owned.Add(new { pawn = pawn.GetUniqueLoadID(), bed = bed.GetUniqueLoadID(), x = bed.Position.x, z = bed.Position.z });
                    }
                    // The bedless pair sleep on claimed spots: indoor capacity meets
                    // the initial shelter, which otherwise sites SleepingSpots in both
                    // chambers first on a bare map (#763), while a spot still owes a Bed.
                    if (connectedRooms)
                        foreach (var (pawn, index) in people.Take(2).Select((pawn, index) => (pawn, index))) {
                            var spot = (Building_Bed)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("SleepingSpot"));
                            spot.SetFaction(Faction.OfPlayerSilentFail);
                            GenSpawn.Spawn(spot, slots[owned.Count + index], map, Rot4.North);
                            if (!pawn.ownership.ClaimBedIfNonMedical(spot) || pawn.ownership.OwnedBed != spot) throw new InvalidOperationException("Colonist could not claim a fixture sleeping spot.");
                        }
                    // Couple (#812): the first two colonists are lovers with no
                    // bed; one vacant 2x2 double bed stands in the top-left
                    // corner (columns 1-2, rows 6-7). Only the controller's
                    // AssignBed may put both in it.
                    string doubleBed = null;
                    if (couple) {
                        if (people[0].relations.DirectRelationExists(PawnRelationDefOf.Lover, people[1]) == false)
                            people[0].relations.AddDirectRelation(PawnRelationDefOf.Lover, people[1]);
                        var dbl = (Building_Bed)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DoubleBed"), ThingDefOf.WoodLog);
                        dbl.SetFaction(Faction.OfPlayerSilentFail);
                        GenSpawn.Spawn(dbl, origin + new IntVec3(1, 0, 6), map, Rot4.North);
                        dbl.SetForbidden(false, false);
                        doubleBed = dbl.GetUniqueLoadID();
                    }
                    if (connectedRooms) PrepareHomeRooms(map, origin, rect);
                    string greedyBed = null;
                    if (greedy) greedyBed = PrepareGreedyBedroom(map, origin, p);
                    string jealousBed = null, asceticBed = null;
                    if (jealous_ascetic) (jealousBed, asceticBed) = PrepareJealousAscetic(map, origin, p, people[1]);
                    foreach (var worker in people) {
                        worker.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                        worker.needs.rest.CurLevelPercentage = .95f;
                        worker.needs.food.CurLevelPercentage = .95f;
                        for (int i = 0; i < 24; i++) worker.timetable.SetAssignment(i, TimeAssignmentDefOf.Anything);
                        if (!worker.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) worker.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                        worker.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                    return new { success = true, pawn = p.GetUniqueLoadID(), partner = couple ? people[1].GetUniqueLoadID() : null, doubleBed, greedyBed, jealousBed, asceticBed, ascetic = jealous_ascetic ? people[1].GetUniqueLoadID() : null, colonists = people.Count, ownedBeds = owned, research = prerequisites?.Select(r => r.defName).ToArray(),
                        x = floorCell.x, z = floorCell.z, roomTemperatureC = floorCell.GetRoom(map).Temperature,
                        room = new { x = origin.x, z = origin.z, width = size, height = size },
                        corridor = new { x = origin.x + 12, z = origin.z + 3 },
                        outside = new { x = origin.x + 18, z = origin.z + 6 } };
                } catch (Exception error) { return new { success = false, error = error.ToString() }; }
            }, cancellationToken).ConfigureAwait(false);
        }

        // Two 1x3 bed chambers have only one legal Bed footprint each (the
        // cell beside the door is a doorway aisle, #763). The
        // roofed corridor joins them through doors, independently of the
        // existing dormitory. No controller-owned facility is fixture-spawned.
        private static void PrepareHomeRooms(Map map, IntVec3 origin, CellRect dormitory)
        {
            foreach (var c in dormitory.ContractedBy(1).Cells.Where(c => c.GetEdifice(map) == null).ToList()) {
                var chair = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog);
                chair.SetFaction(Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(chair, c, map);
            }
            var footprint = new CellRect(origin.x + 9, origin.z + 2, 9, 5);
            foreach (var c in footprint) {
                foreach (var thing in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();
                int x = c.x - origin.x, z = c.z - origin.z;
                bool chamber = (x == 10 || x == 16) && z >= 3 && z <= 5;
                bool corridor = x >= 12 && x <= 14 && z == 3;
                bool door = (x == 11 || x == 15) && z == 3 || x == 13 && z == 2;
                if (!chamber && !corridor) {
                    var wall = ThingMaker.MakeThing(door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog);
                    wall.SetFaction(Faction.OfPlayerSilentFail);
                    GenSpawn.Spawn(wall, c, map);
                    // Like the dormitory ring, standing walls are already Home: every
                    // colonist building is a coverage target, so only the chambers
                    // and corridor are left for the controller (#763).
                    map.areaManager.Home[c] = true;
                }
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            foreach (var c in footprint)
                if (c.GetRoom(map) is Room room && !room.TouchesMapEdge) room.Temperature = 21f;
        }

        // Greedy bedroom (#814): the fixture pawn turns Greedy and owns a
        // wooden bed in a bare 5x4 room east of the dormitory (door south),
        // with the end table, dresser and lamp research done and wood and
        // steel beside it. Only the controller's upgrades raise the room.
        private static string PrepareGreedyBedroom(Map map, IntVec3 origin, Pawn pawn) =>
            PrepareBedroom(map, origin, pawn, TraitDefOf.Greedy, 0, true).GetUniqueLoadID();

        // Jealous and ascetic (#839): the fixture pawn turns Jealous and owns
        // the bare greedy-bedroom site; the second colonist turns Ascetic and
        // owns the 5x4 room above it (shared wall, door north) with a wooden
        // end table already standing, so the ascetic room starts the more
        // impressive. Only upgrading the jealous room clears BedroomJealous;
        // the ascetic room must never rise.
        private static (string, string) PrepareJealousAscetic(Map map, IntVec3 origin, Pawn jealous, Pawn ascetic)
        {
            foreach (var (pawn, def) in new[] { (ascetic, TraitDefOf.Greedy), (ascetic, DefDatabase<TraitDef>.GetNamed("Jealous")), (jealous, DefDatabase<TraitDef>.GetNamed("Ascetic")) })
                if (pawn.story.traits.GetTrait(def) is Trait conflicting) pawn.story.traits.RemoveTrait(conflicting);
            var jealousBed = PrepareBedroom(map, origin, jealous, DefDatabase<TraitDef>.GetNamed("Jealous"), 0, true);
            var asceticBed = PrepareBedroom(map, origin, ascetic, DefDatabase<TraitDef>.GetNamed("Ascetic"), 5, false);
            var table = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("EndTable"), ThingDefOf.WoodLog);
            table.SetFaction(Faction.OfPlayerSilentFail);
            GenSpawn.Spawn(table, new IntVec3(origin.x + 12, 0, origin.z + 6), map);
            return (jealousBed.GetUniqueLoadID(), asceticBed.GetUniqueLoadID());
        }

        // A bare 5x4 bedroom in the 7x6 rect at (origin.x+10, origin.z+dz),
        // door at column 13 on the south (stock beside it) or north edge,
        // owned by pawn, who gains trait. Standing walls (a shared wall) stay.
        private static Building_Bed PrepareBedroom(Map map, IntVec3 origin, Pawn pawn, TraitDef trait, int dz, bool doorSouth)
        {
            if (!pawn.story.traits.HasTrait(trait)) pawn.story.traits.GainTrait(new Trait(trait));
            foreach (var def in new[] { "EndTable", "Dresser", "StandingLamp" })
                foreach (var project in DefDatabase<ThingDef>.GetNamed(def).researchPrerequisites ?? new System.Collections.Generic.List<ResearchProjectDef>())
                    Find.ResearchManager.FinishProject(project, false);
            var outer = new CellRect(origin.x + 10, origin.z + dz, 7, 6);
            foreach (var c in outer) {
                foreach (var thing in c.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();
                map.areaManager.Home[c] = true;
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            var door = new IntVec3(origin.x + 13, 0, doorSouth ? outer.minZ : outer.maxZ);
            foreach (var c in outer.EdgeCells) {
                if (c.GetEdifice(map) != null) continue;
                var wall = ThingMaker.MakeThing(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog);
                wall.SetFaction(Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(wall, c, map);
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
            bed.SetFaction(Faction.OfPlayerSilentFail);
            if (doorSouth) GenSpawn.Spawn(bed, new IntVec3(origin.x + 13, 0, outer.maxZ - 1), map, Rot4.South);
            else GenSpawn.Spawn(bed, new IntVec3(origin.x + 13, 0, outer.minZ + 1), map, Rot4.North);
            if (!pawn.ownership.ClaimBedIfNonMedical(bed) || pawn.ownership.OwnedBed != bed) throw new InvalidOperationException(trait.defName + " colonist could not claim the bedroom bed.");
            if (doorSouth)
                foreach (var def in new[] { ThingDefOf.WoodLog, ThingDefOf.Steel }) {
                    var stock = ThingMaker.MakeThing(def);
                    stock.stackCount = def.stackLimit;
                    GenPlace.TryPlaceThing(stock, new IntVec3(origin.x + 13, 0, origin.z - 2), map, ThingPlaceMode.Near);
                    stock.SetForbidden(false, false);
                }
            var room = bed.GetRoom();
            if (room != null) room.Temperature = 21f;
            return bed;
        }

        [Tool("test/sleeping_greedy_status", Description = "Report the impressiveness of an exact test colonist's bedroom, whether their Greedy thought is active, and whether the named situational thought is active.")]
        public async Task<object> GreedyStatus(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact fixture pawn ID.")] string pawn,
            [ToolParameter(Description = "Situational ThoughtDef reported as thoughtActive, e.g. BedroomJealous or Ascetic.")] string thought = "Greedy")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var p = Find.CurrentMap.mapPawns.FreeColonistsSpawned.Single(x => x.GetUniqueLoadID() == pawn);
                var room = p.ownership.OwnedRoom;
                var greedy = DefDatabase<ThoughtDef>.GetNamed("Greedy");
                var named = DefDatabase<ThoughtDef>.GetNamed(thought);
                return new { success = true, room = room?.ID, impressiveness = room?.GetStat(RoomStatDefOf.Impressiveness) ?? 0f, greedyThought = greedy.Worker.CurrentState(p).Active, thought, thoughtActive = named.Worker.CurrentState(p).Active };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
