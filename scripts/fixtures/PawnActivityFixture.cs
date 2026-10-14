#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class PawnActivityFixture
    {
        private static Pawn? worker;
        private static Map? map;
        private static IntVec3 cropCell;
        private static ThingDef? product;
        private static int productBefore;

        [Tool("test/pawn_activity_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable paused lab: one colonist harvests one mature crop after walking; other colonists wait.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused lab required.");
                worker = map.mapPawns.FreeColonistsSpawned.First(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Growing));
                cropCell = GenRadial.RadialCellsAround(worker.Position, 12, false).First(c =>
                    c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null && c.DistanceTo(worker.Position) >= 8
                    && worker.CanReach(c, PathEndMode.Touch, Danger.None));
                foreach (var plant in cropCell.GetThingList(map).OfType<Plant>().ToArray()) plant.Destroy();
                var crop = DefDatabase<ThingDef>.AllDefsListForReading.First(d =>
                    d.plant != null && d.plant.Sowable && d.plant.harvestedThingDef != null && d.plant.harvestedThingDef.IsNutritionGivingIngestible);
                product = crop.plant.harvestedThingDef;
                productBefore = map.listerThings.ThingsOfDef(product).Sum(t => t.stackCount);
                var target = (Plant)GenSpawn.Spawn(crop, cropCell, map);
                target.Growth = 1;
                worker.skills.GetSkill(SkillDefOf.Plants).Level = 20;
                foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
                    pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 120000), JobCondition.InterruptForced);
                NativeDeliveryLedger.EnsureInstalled();
                worker.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Harvest, target), JobCondition.InterruptForced);
                var sample = PawnActivityTracker.ReadSample(worker);
                return new { success = true, world = sample.World, pawn = sample.Pawn, start = sample.Tick, resource = product.defName };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/pawn_activity_read", Description = "UNSAFE FOR MODEL EXECUTION. Read the prepared crop output and measure passive sampling on this same paused fixture.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                if (worker == null || map == null || map != Find.CurrentMap) throw new InvalidOperationException("Prepare first.");
                var observed = map.listerThings.ThingsOfDef(product).Sum(t => t.stackCount) - productBefore;
                const int iterations = 512;
                var timer = Stopwatch.StartNew();
                for (var i = 0; i < iterations; i++) _ = PawnActivityTracker.ReadSample(worker);
                var baselineMs = timer.Elapsed.TotalMilliseconds;
                var ledger = new PawnActivityLedger(_ => { });
                timer.Restart();
                for (var i = 0; i < iterations; i++) ledger.Observe(PawnActivityTracker.ReadSample(worker));
                var trackedMs = timer.Elapsed.TotalMilliseconds;
                return new { success = true, tick = Find.TickManager.TicksGame, produced = observed, iterations,
                    baseline_sample_ms = baselineMs, sample_and_accounting_ms = trackedMs,
                    note = "Same paused native fixture; steady-state microbenchmark, excludes disk export and does not claim whole-game TPS." };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
