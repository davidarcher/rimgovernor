using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class StoreroomFixture
    {
        [Tool("test/storeroom_setup", Description = "Prepare disposable open ground, wood, exposed medicine and enabled builders. No room or roof is created.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState).ToList();
                var origin = GenRadial.RadialCellsAround(people.First().Position, 20, true).First(c =>
                    CellRect.FromLimits(c, c + new IntVec3(8, 0, 8)).Cells.All(v => v.InBounds(map)
                        && !v.Fogged(map) && v.Standable(map) && v.GetEdifice(map) == null && !v.Roofed(map)
                        && map.zoneManager.ZoneAt(v) == null && v.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                foreach (var cell in CellRect.FromLimits(origin, origin + new IntVec3(8, 0, 8)))
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList()) thing.Destroy();
                for (int i = 0; i < 4; i++) {
                    var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    wood.stackCount = wood.def.stackLimit;
                    GenPlace.TryPlaceThing(wood, origin + new IntVec3(8, 0, i), map, ThingPlaceMode.Near);
                    wood.SetForbidden(false, false);
                }
                var medicine = ThingMaker.MakeThing(ThingDefOf.MedicineHerbal);
                medicine.stackCount = 5;
                GenPlace.TryPlaceThing(medicine, origin + new IntVec3(8, 0, 7), map, ThingPlaceMode.Near);
                medicine.SetForbidden(false, false);
                foreach (var p in people) {
                    p.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                    p.needs.rest.CurLevelPercentage = .95f;
                    p.needs.food.CurLevelPercentage = .95f;
                    for (int i = 0; i < 24; i++) p.timetable.SetAssignment(i, TimeAssignmentDefOf.Anything);
                    foreach (var work in new[] { WorkTypeDefOf.Construction, WorkTypeDefOf.Hauling })
                        if (!p.WorkTypeIsDisabled(work)) p.workSettings.SetPriority(work, 1);
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                return new { success = true, medicine = medicine.GetUniqueLoadID(), origin = new { x = origin.x, z = origin.z } };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
