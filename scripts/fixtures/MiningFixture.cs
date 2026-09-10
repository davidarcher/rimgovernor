using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only. All extraction uses unchanged native pawn labor.
    public sealed class MiningFixture
    {
        [Tool("test/mining_fixture", Description = "Disposable surface deposit fixture; never installed for gameplay.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "setup", int x = 0, int z = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (action == "roof") { map.roofGrid.SetRoof(new IntVec3(x, 0, z), RoofDefOf.RoofConstructed); return new { success = true }; }
                if (action == "unroof") { map.roofGrid.SetRoof(new IntVec3(x, 0, z), null); return new { success = true }; }
                if (action == "cancel") {
                    var designation = map.designationManager.DesignationAt(new IntVec3(x, 0, z), DesignationDefOf.Mine);
                    designation?.Delete(); return new { success = true };
                }
                if (action == "hauling" || action == "mining") {
                    var selectedWork = action == "hauling" ? WorkTypeDefOf.Hauling : WorkTypeDefOf.Mining;
                    foreach (var worker in map.mapPawns.FreeColonistsSpawned)
                        foreach (var work in DefDatabase<WorkTypeDef>.AllDefs)
                            if (!worker.WorkTypeIsDisabled(work)) worker.workSettings.SetPriority(work, work == selectedWork ? 1 : 0);
                    return new { success = true };
                }
                var pawn = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining));
                var cells = GenRadial.RadialCellsAround(pawn.Position, 35, true).Where(c => c.InBounds(map) && c.Standable(map)
                    && GenRadial.RadialCellsAround(c, 8, true).All(q => q.InBounds(map)
                        && !(q.GetEdifice(map) is Building b && !(b is Mineable))
                        && map.zoneManager.ZoneAt(q) == null && !map.areaManager.Home[q])).Take(3).ToList();
                if (cells.Count != 3) return new { success = false, error = "Fixture requires three clear surface cells" };
                var cleared = cells.SelectMany(c => GenRadial.RadialCellsAround(c, 8, true)).Distinct().ToList();
                foreach (var cell in cleared) map.roofGrid.SetRoof(cell, null);
                foreach (var cell in cleared) {
                    map.fogGrid.Unfog(cell);
                    if (cell.GetEdifice(map) is Mineable rock) rock.Destroy(DestroyMode.Vanish);
                }
                var def = DefDatabase<ThingDef>.AllDefs.First(d => d.building?.mineableThing == ThingDefOf.Steel
                    && typeof(Mineable).IsAssignableFrom(d.thingClass));
                var targets = cells.Select(c => GenSpawn.Spawn(ThingMaker.MakeThing(def), c, map)).ToList();
                foreach (var worker in map.mapPawns.FreeColonistsSpawned)
                    if (!worker.WorkTypeIsDisabled(WorkTypeDefOf.Mining)) {
                        foreach (var work in DefDatabase<WorkTypeDef>.AllDefs)
                            if (!worker.WorkTypeIsDisabled(work)) worker.workSettings.SetPriority(work, work == WorkTypeDefOf.Mining ? 1 : 0);
                    }
                return new { success = true, pawn = pawn.ThingID, targets = targets.Select(t => new {
                    thingId = t.ThingID, x = t.Position.x, z = t.Position.z, hp = t.HitPoints,
                    resource = t.def.building.mineableThing.defName }).ToList() };
            }, cancellationToken);
        }
    }
}
