#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

// Shared-stack remainder (#3194): one four-meal stack, one pawn hungry now whose Method owes later bites of it, then a
// second pawn made hungry while that Method still owns the stack. It must eat from the same stack under its own Method.
// Only the world is staged and observed; no task, Method or order is installed.
public sealed partial class PawnControlFixture
{
    private static bool remainderPatched, remainderActive, remainderSharedWhileOwned;
    private static Thing? remainderStack;
    private static Pawn? remainderFirst;
    private static readonly Dictionary<string, int> remainderOwed = new(StringComparer.Ordinal);
    private static readonly HashSet<string> remainderEaters = new(StringComparer.Ordinal);

    [Tool("test/shared_stack_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepare one hungry pawn, one sated pawn and one four-meal simple-meal stack, with no other food. Install no chosen task, Method or assignment.")]
    public async Task<object> SharedStackPrepare(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap;
            var pawns = map.mapPawns.FreeColonistsSpawned.ToArray();
            if (pawns.Length < 2) throw new InvalidOperationException("Shared-stack fixture needs two colonists.");
            CapacityQuiet(map, .95f, .95f);
            remainderActive = false; remainderSharedWhileOwned = false;
            remainderOwed.Clear(); remainderEaters.Clear();
            remainderFirst = pawns[0];
            remainderFirst.needs.food.CurLevelPercentage = .2f;
            var anchor = remainderFirst.Position;
            var cell = GenRadial.RadialCellsAround(anchor, 6, false).First(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null && c.DistanceTo(anchor) > 2);
            remainderStack = ThingMaker.MakeThing(ThingDefOf.MealSimple); remainderStack.stackCount = 4;
            GenSpawn.Spawn(remainderStack, cell, map);
            if (!remainderPatched)
            {
                var harmony = new Harmony("rimgovernor.fixture.shared-stack");
                harmony.Patch(AccessTools.Method(typeof(NativePawnActivities), "Apply"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(SharedStackApplied)));
                harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(SharedStackTick)));
                remainderPatched = true;
            }
            remainderActive = true;
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

    [Tool("test/shared_stack_hunger", Description="UNSAFE FOR MODEL EXECUTION. Once a Method owes bites of the stack, make the second pawn hungry. Install no chosen task, Method or assignment.")]
    public async Task<object> SharedStackHunger(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (!remainderActive || remainderOwed.Count == 0) throw new InvalidOperationException("No Method owes bites of the stack yet.");
            var second = Find.CurrentMap.mapPawns.FreeColonistsSpawned.First(p => p != remainderFirst);
            second.needs.food.CurLevelPercentage = .2f;
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

    private static void SharedStackApplied(PawnActivities batch)
    {
        if (!remainderActive || remainderStack == null) return;
        foreach (var method in batch.Orders.Where(o => !o.Release && o.Method.StartsWith("coupled-", StringComparison.Ordinal)).Select(o => o.Method).Distinct(StringComparer.Ordinal))
        {
            var owed = batch.Tasks.Where(t => t.Step.Kind == PawnActivityKind.Eat && RefIndex.Thing(Find.CurrentMap, t.Step.Target) == remainderStack).Sum(t => t.Step.Count);
            if (owed == 0) continue;
            // The second Method's bite applied while the first still owned the pawn whose Method owes bites of the stack.
            var first = remainderFirst == null ? null : NativePawnActivities.Read(remainderFirst)?.Method;
            if (first != null && first != method && remainderOwed.TryGetValue(first, out var firstOwed) && firstOwed > 0) remainderSharedWhileOwned = true;
            remainderOwed[method] = owed;
        }
    }

    private static void SharedStackTick(Pawn ___pawn)
    {
        if (!remainderActive || remainderStack == null || ___pawn.Map == null || !___pawn.IsColonist) return;
        var owned = NativePawnActivities.Read(___pawn)?.Method.StartsWith("coupled-", StringComparison.Ordinal) == true;
        if (owned && ___pawn.CurJobDef == JobDefOf.Ingest && ___pawn.CurJob.targetA.Thing == remainderStack) remainderEaters.Add(___pawn.GetUniqueLoadID());
    }

    [Tool("test/shared_stack_read", Description="UNSAFE FOR MODEL EXECUTION. Observe the Methods eating from the staged stack and its remaining units; writes no orders.")]
    public async Task<object> SharedStackRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() => FixtureKit.Reply(new Fx.SharedStackRead
        {
            Tick = Find.TickManager.TicksGame,
            Units = remainderStack is { Destroyed: false, Spawned: true } ? remainderStack.stackCount : 0,
            Methods = remainderOwed.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Eaters = remainderEaters.Count, SharedWhileOwned = remainderSharedWhileOwned,
        }), ct).ConfigureAwait(false);
}
