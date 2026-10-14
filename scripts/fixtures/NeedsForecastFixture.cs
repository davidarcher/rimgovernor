#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Host.Sdk;
using RimGovernor.Brain.Domain;

namespace HomeBridge.BridgeTools;

public sealed partial class PawnControlFixture
{
    private static Thing? needsPin;
    [Tool("test/needs_forecast_select", Description="UNSAFE FOR MODEL EXECUTION. Select one prepared need activity for measuring native prediction residuals.")]
    public async Task<object> NeedsSelect(IRimBridgeContext ctx, CancellationToken ct,
        [ToolParameter(Description="1 sleep, 2 horseshoes, 3 selected meal.")] int stage = 1)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var p = worker ?? throw new InvalidOperationException("Prepare pawn_control first.");
            if (stage < 1 || stage > 3) throw new InvalidOperationException("Invalid stage.");
            p.needs.food.CurLevelPercentage = .4f; p.needs.rest.CurLevelPercentage = .4f; p.needs.joy.CurLevelPercentage = .4f;
            for (var hour = 0; hour < 24; hour++) p.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
            if (stage == 2)
            {
                var cell = GenRadial.RadialCellsAround(p.Position, 8, false).First(c => c.InBounds(p.Map)
                    && c.Standable(p.Map) && c.GetEdifice(p.Map) == null && c.DistanceTo(p.Position) > 3);
                needsPin = GenSpawn.Spawn(ThingDef.Named("HorseshoesPin"), cell, p.Map);
            }
            var kind = stage == 1 ? PawnActivityKind.Sleep : stage == 2 ? PawnActivityKind.Recreation : PawnActivityKind.Eat;
            var target = stage == 1 ? bed! : stage == 2 ? needsPin! : food!;
            var now = Find.TickManager.TicksGame;
            var observed = NativeNeedsForecast.Read(p.Map, p.Map.mapPawns.FreeColonistsSpawned.ToList());
            var row = observed.Pawns.Single(n => n.Pawn == p.GetUniqueLoadID());
            var selected = row.Targets.SingleOrDefault(t => t.Id == target.GetUniqueLoadID())
                ?? throw new InvalidOperationException("Prepared target missing from native need census.");
            var context = Context();
            var batch = new PawnActivities(context.NativeGeneration!.Value,
                [new(p.GetUniqueLoadID(), "needs-probe", "needs-probe", stage - 1, stage, false,
                    [new(kind, target.GetUniqueLoadID(), "", "", stage == 3 ? 1 : 0, now + 1100),
                     new(PawnActivityKind.Hold, "", "", "", 0, now + 5000)])]);
            var invalid = NativePawnActivities.Validate(batch, context);
            if (invalid != null) throw new InvalidOperationException(invalid.Detail);
            NativePawnActivities.Apply(batch, context);
            var sample = PawnActivityTracker.ReadSample(p);
            var need = row.Needs.Single(n => n.Kind == selected.Kind);
            return new { success = true, stage, pawn = sample.Pawn, world = sample.World, start = sample.Tick,
                kind = selected.Kind, initial = need.Level.Value, fall = need.FallPerTick.Value,
                gain = selected.GainPerTick.IsKnown ? selected.GainPerTick.Value : selected.GainPerUnit.Value,
                travel = selected.TravelTicks.Value, target = selected.Id, truncated = observed.Truncated };
        }, ct).ConfigureAwait(false);

    [Tool("test/needs_forecast_read", Description="UNSAFE FOR MODEL EXECUTION. Read needs and terminal execution for the prepared forecast residual scene.")]
    public async Task<object> NeedsRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() => new { success = true, tick = Find.TickManager.TicksGame,
            food = worker!.needs.food.CurLevelPercentage, rest = worker.needs.rest.CurLevelPercentage,
            joy = worker.needs.joy.CurLevelPercentage, execution = NativePawnActivities.Read(worker) }, ct).ConfigureAwait(false);
}
