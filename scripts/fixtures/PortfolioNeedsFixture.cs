#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Fx = RimGovernor.Fixtures.Contracts;

namespace HomeBridge.BridgeTools;

public sealed partial class PawnControlFixture
{
    private static bool portfolioPatched, portfolioActive, portfolioRebound, portfolioAte, portfolioSlept, portfolioWoke, portfolioPlayed;
    private static int portfolioLastEatingTick;
    private static float portfolioFood, portfolioRestAtSleep;
    private static readonly HashSet<string> portfolioMethods = new(StringComparer.Ordinal);

    [Tool("test/portfolio_needs_prepare", Description="UNSAFE FOR MODEL EXECUTION. Bind the staged worker and ripe rice and leave them the colony's only food: no loose, carried or other growing food. Install no chosen task, bill, Method or assignment.")]
    public async Task<object> PortfolioNeedsPrepare(IRimBridgeContext ctx, CancellationToken ct,
        [ToolParameter(Description="The worker's load id.")] string worker = "",
        [ToolParameter(Description="The ripe rice's load id.")] string crop = "")
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            portfolioActive = false;
            var map = FixtureKit.PausedMap();
            PawnControlFixture.worker = FixtureKit.Spawned<Pawn>(map, worker);
            PawnControlFixture.crop = FixtureKit.Spawned<Plant>(map, crop);
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToArray())
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                foreach (var item in pawn.inventory.innerContainer.Where(t => t.def.IsNutritionGivingIngestible).ToArray()) item.Destroy();
            }
            foreach (var thing in map.listerThings.AllThings.Where(t => t.Spawned && t.def.IsNutritionGivingIngestible && t is not Plant).ToArray()) thing.Destroy();
            foreach (var plant in map.listerThings.AllThings.OfType<Plant>().Where(p => p != PawnControlFixture.crop
                && p.def.plant.harvestedThingDef?.IsNutritionGivingIngestible == true).ToArray()) plant.Destroy();
            portfolioMethods.Clear();
            portfolioRebound = portfolioAte = portfolioSlept = portfolioWoke = portfolioPlayed = false;
            portfolioLastEatingTick = -1000;
            portfolioFood = PawnControlFixture.worker.needs.food.CurLevelPercentage; portfolioRestAtSleep = -1;
            if (!portfolioPatched)
            {
                var harmony = new Harmony("rimgovernor.fixture.portfolio-needs");
                harmony.Patch(AccessTools.Method(typeof(NativePawnActivities), "Apply"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(PortfolioApplied)));
                harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(PortfolioTick)));
                portfolioPatched = true;
            }
            portfolioActive = true;
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

    private static void PortfolioApplied(PawnActivities batch)
    {
        if (!portfolioActive) return;
        foreach (var order in batch.Orders.Where(o => !o.Release && o.Method.StartsWith("coupled-", StringComparison.Ordinal)))
        {
            portfolioMethods.Add(order.Method);
            foreach (var step in order.Steps.Where(s => s.Kind is PawnActivityKind.Deliver or PawnActivityKind.Eat))
            {
                var actual = RefIndex.Thing(Find.CurrentMap, step.Target);
                if (actual?.def == ThingDef.Named("RawRice") && (crop!.Destroyed || ((Plant)crop).Growth < 1))
                    portfolioRebound = true;
            }
        }
    }
    private static void PortfolioTick(Pawn ___pawn)
    {
        if (!portfolioActive || ___pawn != worker) return;
        var tick = Find.TickManager.TicksGame;
        var execution = NativePawnActivities.Read(___pawn);
        var owned = execution?.Method.StartsWith("coupled-", StringComparison.Ordinal) == true;
        if (owned && ___pawn.CurJobDef == JobDefOf.Ingest) portfolioLastEatingTick = tick;
        if (tick - portfolioLastEatingTick <= 2 && ___pawn.needs.food.CurLevelPercentage > portfolioFood + .01f) portfolioAte = true;
        portfolioFood = ___pawn.needs.food.CurLevelPercentage;
        if (owned && ___pawn.CurJobDef == JobDefOf.LayDown && !___pawn.Awake())
        {
            if (portfolioRestAtSleep < 0) portfolioRestAtSleep = ___pawn.needs.rest.CurLevelPercentage;
            if (___pawn.needs.rest.CurLevelPercentage > portfolioRestAtSleep + .05f) portfolioSlept = true;
        }
        if (portfolioSlept && ___pawn.Awake()) portfolioWoke = true;
        if (owned && ___pawn.CurJobDef?.defName == "Play_Horseshoes" && ___pawn.needs.joy.CurLevelPercentage > .2f) portfolioPlayed = true;
    }
    [Tool("test/portfolio_needs_repeat", Description="UNSAFE FOR MODEL EXECUTION. After completed first-cycle needs, introduce new hunger and independent observed rice lots for the crew. Install no selected task, Method or order.")]
    public async Task<object> PortfolioNeedsRepeat(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (!portfolioAte || !portfolioSlept || !portfolioWoke || !portfolioPlayed)
                throw new InvalidOperationException("First normal-planner needs cycle has not completed.");
            // This phase proves repeated needs execution with independent physical lots;
            // needs/shared-stack proves two Methods sharing one stack (#3194).
            var map = worker!.Map;
            var count = map.mapPawns.FreeColonistsSpawned.Count;
            var cells = GenRadial.RadialCellsAround(worker.Position, 12, true)
                .Where(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null
                    && !c.GetThingList(map).Any(t => t.def.IsNutritionGivingIngestible))
                .Take(count).ToArray();
            if (cells.Length != count) throw new InvalidOperationException("Repeat-cycle independent food lots need distinct legal cells.");
            foreach (var cell in cells)
            {
                var rice = ThingMaker.MakeThing(ThingDef.Named("RawRice")); rice.stackCount = 20;
                GenSpawn.Spawn(rice, cell, map);
            }
            worker.needs.food.CurLevelPercentage = .2f;
            portfolioFood = .2f; portfolioAte = false; portfolioLastEatingTick = -1000;
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);
    [Tool("test/portfolio_needs_read", Description="UNSAFE FOR MODEL EXECUTION. Observe actual normal-planner coupled batches, harvested output rebinding, ingestion and sleep/wake; writes no orders.")]
    public async Task<object> PortfolioNeedsRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() => FixtureKit.Reply(new Fx.PortfolioNeedsRead
        {
            Tick = Find.TickManager.TicksGame, Methods = portfolioMethods.OrderBy(x => x).ToArray(), Rebound = portfolioRebound,
            Ate = portfolioAte, Slept = portfolioSlept, Woke = portfolioWoke, Played = portfolioPlayed,
        }), ct).ConfigureAwait(false);
}
