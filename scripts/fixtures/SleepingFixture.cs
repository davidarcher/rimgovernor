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
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                try {
                    var map = Find.CurrentMap;
                    var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState).OrderBy(p => p.thingIDNumber).ToList();
                    if (people.Count < 1 || people.Count > 8) throw new InvalidOperationException("Require 1..8 colonists.");
                    var p = people.First();
                    var prerequisites = ThingDefOf.Bed.researchPrerequisites;
                    if (prerequisites != null)
                        foreach (var project in prerequisites) Find.ResearchManager.FinishProject(project, false);
                    foreach (var bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>().ToList())
                        foreach (var owner in bed.OwnersForReading.ToList()) owner.ownership.UnclaimBed();
                    var center = new IntVec3((int)people.Average(x => x.Position.x), 0, (int)people.Average(x => x.Position.z));
                    const int size = 9;
                    var origin = GenRadial.RadialCellsAround(center, 18, true).Where(c => c.DistanceToSquared(center) >= 36).First(c =>
                        new CellRect(c.x, c.z, size, size).Cells.All(v => v.InBounds(map)
                            && !v.Fogged(map) && v.Standable(map) && v.GetEdifice(map) == null
                            && map.zoneManager.ZoneAt(v) == null && v.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
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
                    // Beds for everyone but the fixture pawn: vertical 1x2 beds in
                    // columns 1,3,5,7 and rows 1 and 4 of the 7x7 interior, which
                    // leaves free 1x2 sites for the controller's bed.
                    var owned = new System.Collections.Generic.List<object>();
                    var slots = new[] { 1, 3, 5, 7 }.SelectMany(x => new[] { 1, 4 }.Select(z => new IntVec3(origin.x + x, 0, origin.z + z))).ToList();
                    foreach (var (pawn, index) in people.Skip(1).Select((pawn, index) => (pawn, index))) {
                        var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                        bed.SetFaction(Faction.OfPlayerSilentFail);
                        GenSpawn.Spawn(bed, slots[index], map, Rot4.North);
                        bed.SetForbidden(false, false);
                        if (!pawn.ownership.ClaimBedIfNonMedical(bed) || pawn.ownership.OwnedBed != bed) throw new InvalidOperationException("Colonist could not claim a fixture bed.");
                        owned.Add(new { pawn = pawn.GetUniqueLoadID(), bed = bed.GetUniqueLoadID(), x = bed.Position.x, z = bed.Position.z });
                    }
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
                        room = new { x = origin.x, z = origin.z, width = size, height = size } };
                } catch (Exception error) { return new { success = false, error = error.ToString() }; }
            }, cancellationToken).ConfigureAwait(false);
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
