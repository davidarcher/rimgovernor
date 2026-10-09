using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (#2519). A two-cell Steel stockpile is
    // filled to the brim, and one loose Steel stack larger than the store's
    // free room waits outside it, unforbidden. Exactly one colonist hauls
    // (every other colonist is held on a Wait job so opportunistic hauling
    // cannot take the stack). test/refill_hysteresis_take then draws Steel out
    // of the store, and test/refill_hysteresis_read reports the store and the
    // loose stack so the case can see whether the hauler refilled.
    public sealed class RefillHysteresisFixture
    {
        private const int Stack = 75;
        private const int LooseCount = 100;
        private static Map preparedMap;
        private static readonly List<IntVec3> storeCells = new List<IntVec3>();
        private static IntVec3 looseCell;
        private static Pawn hauler;

        [Tool("test/refill_hysteresis_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: a full two-cell Steel stockpile, a loose unforbidden Steel stack of 100 outside it, one enabled hauler and every other colonist held on Wait jobs.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || !Find.TickManager.Paused) return Refuse("A paused disposable map is required.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted)
                    .OrderBy(p => p.thingIDNumber).ToList();
                var chosen = people.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
                if (chosen == null) return Refuse("No colonist can haul.");
                bool Clear(IntVec3 c) => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null
                    && map.zoneManager.ZoneAt(c) == null && c.GetThingList(map).All(t => t is Plant || t is Pawn)
                    && chosen.CanReach(c, PathEndMode.Touch, Danger.None);
                var origin = chosen.Position;
                IntVec3 first = IntVec3.Invalid;
                foreach (var c in GenRadial.RadialCellsAround(origin, 30, true)) {
                    var next = c + IntVec3.East;
                    if (Clear(c) && Clear(next)) { first = c; break; }
                }
                if (!first.IsValid) return Refuse("No clear two-cell strip near the hauler.");
                var strip = new[] { first, first + IntVec3.East };
                var looseCells = GenRadial.RadialCellsAround(first, 12, true)
                    .Where(c => Clear(c) && c.DistanceTo(first) >= 4 && !strip.Contains(c)).Take(1).ToList();
                if (looseCells.Count == 0) return Refuse("No clear cell for the loose stack.");
                var loose = looseCells[0];
                foreach (var pawn in people) {
                    pawn.workSettings.SetPriority(WorkTypeDefOf.Hauling, pawn == chosen ? 1 : 0);
                    if (pawn != chosen) pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 120000), JobCondition.InterruptForced);
                }
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.GetStoreSettings().filter.SetDisallowAll();
                zone.GetStoreSettings().filter.SetAllow(ThingDefOf.Steel, true);
                zone.GetStoreSettings().Priority = StoragePriority.Normal;
                storeCells.Clear();
                foreach (var cell in strip) {
                    zone.AddCell(cell);
                    storeCells.Add(cell);
                    var full = ThingMaker.MakeThing(ThingDefOf.Steel);
                    full.stackCount = Stack;
                    GenSpawn.Spawn(full, cell, map);
                }
                var stack = ThingMaker.MakeThing(ThingDefOf.Steel);
                stack.stackCount = LooseCount;
                GenSpawn.Spawn(stack, loose, map);
                stack.SetForbidden(false, false);
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                preparedMap = map; looseCell = loose; hauler = chosen;
                return new { success = true, stackLimit = Stack, loose = LooseCount, store = Stack * 2,
                    haulerId = chosen.GetUniqueLoadID() };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/refill_hysteresis_take", Description = "UNSAFE FOR MODEL EXECUTION. Private prepared-fixture control: remove count Steel from the first store cell that holds any, as a pawn drawing from the store would.")]
        public async Task<object> Take(IRimBridgeContext ctx, CancellationToken cancellationToken, int count)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || map != preparedMap) return Refuse("Prepared map changed.");
                var left = count;
                foreach (var cell in storeCells)
                    foreach (var thing in cell.GetThingList(map).Where(t => t.def == ThingDefOf.Steel).ToList()) {
                        if (left <= 0) break;
                        var n = System.Math.Min(left, thing.stackCount);
                        if (n == thing.stackCount) thing.Destroy(DestroyMode.Vanish); else thing.stackCount -= n;
                        left -= n;
                    }
                return new { success = left == 0, removed = count - left };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/refill_hysteresis_read", Description = "UNSAFE FOR MODEL EXECUTION. Private prepared-fixture read: Steel in the store cells and in the loose stack's cell, and the hauler's current job.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || map != preparedMap) return Refuse("Prepared map changed.");
                int Steel(IEnumerable<IntVec3> cells) => cells.Sum(c => c.GetThingList(map).Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount));
                return new { success = true, store = Steel(storeCells), loose = Steel(new[] { looseCell }),
                    haulerJob = hauler?.CurJobDef?.defName, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
