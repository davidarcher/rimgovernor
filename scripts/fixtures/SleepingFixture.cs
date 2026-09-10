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
        [Tool("test/sleeping_setup", Description = "Prepare a disposable roofed warm room and construction wood.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                try {
                    var map = Find.CurrentMap;
                    var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState).ToList();
                    var p = people.First();
                    var prerequisites = ThingDefOf.Bed.researchPrerequisites;
                    if (prerequisites != null)
                        foreach (var project in prerequisites) Find.ResearchManager.FinishProject(project, false);
                    var origin = GenRadial.RadialCellsAround(p.Position, 30, true).First(c =>
                        CellRect.FromLimits(c, c + new IntVec3(5, 0, 5)).Cells.All(v => v.InBounds(map)
                            && !v.Fogged(map) && v.Standable(map) && v.GetEdifice(map) == null
                            && map.zoneManager.ZoneAt(v) == null && v.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                    var rect = CellRect.FromLimits(origin, origin + new IntVec3(5, 0, 5));
                    foreach (var cell in rect) {
                        foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();
                        map.areaManager.Home[cell] = true;
                        map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    }
                    foreach (var cell in rect.EdgeCells) {
                        var wall = ThingMaker.MakeThing(cell == origin + new IntVec3(2, 0, 0) ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog);
                        wall.SetFaction(Faction.OfPlayerSilentFail);
                        GenSpawn.Spawn(wall, cell, map);
                    }
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                    var floorCell = origin + new IntVec3(1, 0, 1);
                    floorCell.GetRoom(map).Temperature = 21f;
                    var heater = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Campfire"));
                    heater.SetFaction(Faction.OfPlayerSilentFail);
                    GenSpawn.Spawn(heater, origin + new IntVec3(4, 0, 4), map);
                    var fuel = heater.TryGetComp<CompRefuelable>();
                    fuel.Refuel(fuel.Props.fuelCapacity);
                    var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    wood.stackCount = wood.def.stackLimit;
                    GenPlace.TryPlaceThing(wood, origin + new IntVec3(3, 0, 3), map, ThingPlaceMode.Near);
                    wood.SetForbidden(false, false);
                    foreach (var worker in people) {
                        worker.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                        worker.needs.rest.CurLevelPercentage = .95f;
                        worker.needs.food.CurLevelPercentage = .95f;
                        for (int i = 0; i < 24; i++) worker.timetable.SetAssignment(i, TimeAssignmentDefOf.Anything);
                        if (!worker.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) worker.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                        worker.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                    return new { success = true, pawn = p.GetUniqueLoadID(), research = prerequisites?.Select(r => r.defName).ToArray(),
                        x = floorCell.x, z = floorCell.z, room = new { x = origin.x, z = origin.z, width = 6, height = 6 } };
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
