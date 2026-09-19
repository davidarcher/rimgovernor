using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class DeepResourcesFixture
    {
        [Tool("test/deep_resources_seed", Description = "Disposable deep lump and scanner readback fixture; no drills or bills.")]
        public async Task<object> Seed(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var centre = map.Center;
                // Clear the small fixture window so generated deposits cannot join it.
                foreach (var c in CellRect.CenteredOn(centre, 5).ClipInsideMap(map)) map.deepResourceGrid.SetAt(c, null, 0);
                var resource = DefDatabase<ThingDef>.GetNamed("Plasteel");
                foreach (var offset in new[] { IntVec3.Zero, new IntVec3(-1,0,0), new IntVec3(1,0,0) })
                    map.deepResourceGrid.SetAt(centre + offset, resource, 100);
                // A separate same-def lump and a diagonally connected pair exercise grouping.
                map.deepResourceGrid.SetAt(centre + new IntVec3(4,0,0), resource, 25);
                map.deepResourceGrid.SetAt(centre + new IntVec3(4,0,4), ThingDefOf.Steel, 30);
                map.deepResourceGrid.SetAt(centre + new IntVec3(3,0,3), ThingDefOf.Steel, 40);
                var index = 0;
                foreach (var def in new[] { ThingDefOf.GroundPenetratingScanner, DefDatabase<ThingDef>.GetNamed("LongRangeMineralScanner") }) {
                    var cell = centre + new IntVec3(-12,0,index++ * 8);
                    foreach (var c in CellRect.CenteredOn(cell, 3).ClipInsideMap(map)) {
                        c.GetEdifice(map)?.Destroy(DestroyMode.Vanish);
                        map.roofGrid.SetRoof(c, null); map.fogGrid.Unfog(c);
                    }
                    var building = (Building)GenSpawn.Spawn(ThingMaker.MakeThing(def), cell, map);
                    building.SetFaction(Faction.OfPlayer);
                    var scanner = building.AllComps.OfType<CompScanner>().Single();
                    typeof(CompScanner).GetField("daysWorkingSinceLastFinding", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(scanner, scanner.Props.scanFindGuaranteedDays / 2);
                    typeof(CompScanner).GetField("lastUserSpeed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(scanner, 2f);
                    typeof(CompScanner).GetField("lastScanTick", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(scanner, -1f);
                }
                return new { success = true, x = centre.x, z = centre.z, count = 300, cellCount = 3 };
            }, cancellationToken);
        }
    }
}
