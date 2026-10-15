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

namespace HomeBridge.BridgeTools;

public sealed partial class PawnControlFixture
{
    private static bool portfolioPatched, portfolioActive, portfolioRebound, portfolioAte, portfolioSlept, portfolioWoke, portfolioPlayed;
    private static int portfolioStart, portfolioLastEatingTick;
    private static float portfolioFood, portfolioRestAtSleep;
    private static readonly HashSet<string> portfolioMethods = new(StringComparer.Ordinal);
    private static readonly HashSet<string> portfolioOutputIds = new(StringComparer.Ordinal);

    [Tool("test/portfolio_needs_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepare scarce food, ripe rice, a stockpile and tired pawn for normal shared selection. Install no chosen task, bill, Method or assignment.")]
    public async Task<object> PortfolioNeedsPrepare(IRimBridgeContext ctx, CancellationToken ct)
    {
        await Prepare(ctx, ct).ConfigureAwait(false);
        return await ctx.MainThread.InvokeAsync<object>(() =>
        {
            portfolioActive = false;
            var map = worker!.Map;
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToArray())
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                pawn.needs.food.CurLevelPercentage = pawn == worker ? .25f : .95f;
                pawn.needs.rest.CurLevelPercentage = pawn == worker ? .25f : .95f;
                pawn.needs.joy.CurLevelPercentage = pawn == worker ? .1f : .95f;
                for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                foreach (var type in DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t => !pawn.WorkTypeIsDisabled(t))) pawn.workSettings.SetPriority(type, 0);
                foreach (var item in pawn.inventory.innerContainer.Where(t => t.def.IsNutritionGivingIngestible).ToArray()) item.Destroy();
            }
            // Remove the base execution fixture's exact meal; this scenario starts without loose food.
            if (food is { Destroyed: false }) food.Destroy();
            foreach (var thing in map.listerThings.AllThings.Where(t => t.Spawned && t.def.IsNutritionGivingIngestible && t is not Plant).ToArray()) thing.Destroy();
            var plantCell = crop!.Position;
            crop.Destroy();
            foreach (var plant in map.listerThings.AllThings.OfType<Plant>().Where(p => p.def.plant.harvestedThingDef?.IsNutritionGivingIngestible == true).ToArray()) plant.Destroy();
            crop = GenSpawn.Spawn(ThingDef.Named("Plant_Rice"), plantCell, map);
            ((Plant)crop).Growth = 1;
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone); zone.GetStoreSettings().filter.SetDisallowAll();
            zone.GetStoreSettings().filter.SetAllow(ThingDef.Named("RawRice"), true);
            var destination = GenRadial.RadialCellsAround(plantCell, 6, false).First(c => c.InBounds(map) && c.Standable(map)
                && c.GetEdifice(map) == null && c.GetZone(map) == null && c.DistanceTo(plantCell) > 2);
            zone.AddCell(destination);
            bed!.SetFaction(Faction.OfPlayer);
            var pinCell = GenRadial.RadialCellsAround(worker.Position, 8, false).First(c => c.InBounds(map)
                && c.Standable(map) && c.GetEdifice(map) == null && c.DistanceTo(worker.Position) > 3);
            var pin = GenSpawn.Spawn(ThingDef.Named("HorseshoesPin"), pinCell, map); pin.SetFaction(Faction.OfPlayer);
            portfolioMethods.Clear(); portfolioOutputIds.Clear();
            portfolioRebound = portfolioAte = portfolioSlept = portfolioWoke = portfolioPlayed = false;
            portfolioStart = Find.TickManager.TicksGame; portfolioLastEatingTick = -1000;
            portfolioFood = worker.needs.food.CurLevelPercentage; portfolioRestAtSleep = -1;
            if (!portfolioPatched)
            {
                var harmony = new Harmony("rimgovernor.fixture.portfolio-needs");
                harmony.Patch(AccessTools.Method(typeof(NativePawnActivities), "Apply"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(PortfolioApplied)));
                harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(PortfolioTick)));
                portfolioPatched = true;
            }
            portfolioActive = true;
            return new { success = true, pawn = worker.GetUniqueLoadID(), tick = portfolioStart };
        }, ct).ConfigureAwait(false);
    }

    private static void PortfolioApplied(PawnActivities batch)
    {
        if (!portfolioActive) return;
        foreach (var order in batch.Orders.Where(o => !o.Release && o.Method.StartsWith("coupled-", StringComparison.Ordinal)))
        {
            portfolioMethods.Add(order.Method);
            foreach (var step in order.Steps.Where(s => s.Kind is PawnActivityKind.Deliver or PawnActivityKind.Eat))
            {
                var actual = Find.CurrentMap.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == step.Target);
                if (actual?.def == ThingDef.Named("RawRice") && (crop!.Destroyed || ((Plant)crop).Growth < 1))
                { portfolioRebound = true; portfolioOutputIds.Add(actual.GetUniqueLoadID()); }
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
            // This phase proves repeated needs execution with independent physical lots.
            // Scarce shared-lot redistribution is a separate ownership limitation (#3194).
            var map = worker!.Map;
            var count = map.mapPawns.FreeColonistsSpawned.Count;
            var cells = GenRadial.RadialCellsAround(worker.Position, 12, true)
                .Where(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null
                    && !c.GetThingList(map).Any(t => t.def.IsNutritionGivingIngestible))
                .Take(count).ToArray();
            if (cells.Length != count) throw new InvalidOperationException("Repeat-cycle independent food lots need distinct legal cells.");
            var foods = new List<string>();
            foreach (var cell in cells)
            {
                var rice = ThingMaker.MakeThing(ThingDef.Named("RawRice")); rice.stackCount = 20;
                GenSpawn.Spawn(rice, cell, map); foods.Add(rice.GetUniqueLoadID());
            }
            worker.needs.food.CurLevelPercentage = .2f;
            portfolioFood = .2f; portfolioAte = false; portfolioLastEatingTick = -1000;
            return new { success = true, tick = Find.TickManager.TicksGame, foods = foods.ToArray() };
        }, ct).ConfigureAwait(false);
    [Tool("test/portfolio_needs_read", Description="UNSAFE FOR MODEL EXECUTION. Observe actual normal-planner coupled batches, harvested output rebinding, ingestion and sleep/wake; writes no orders.")]
    public async Task<object> PortfolioNeedsRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() => new { success = true, tick = Find.TickManager.TicksGame,
            elapsed = Find.TickManager.TicksGame - portfolioStart, methods = portfolioMethods.OrderBy(x => x).ToArray(),
            outputIds = portfolioOutputIds.OrderBy(x => x).ToArray(), rebound = portfolioRebound, ate = portfolioAte,
            slept = portfolioSlept, woke = portfolioWoke, played = portfolioPlayed, food = worker!.needs.food.CurLevelPercentage,
            rest = worker.needs.rest.CurLevelPercentage, execution = NativePawnActivities.Read(worker),
            legacyWork = worker.timetable.GetAssignment(GenLocalDate.HourInteger(worker)) == TimeAssignmentDefOf.Work }, ct).ConfigureAwait(false);
}
