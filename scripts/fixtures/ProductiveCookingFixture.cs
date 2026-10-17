#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

public sealed partial class PawnControlFixture
{
    private static bool productiveActive, productivePatched, productiveOutput;
    private static int productiveStart, productiveInitialRaw, productiveUrgeTick = -1, productiveUrgeCompletions;
    private static bool productiveSlept;
    private static int productiveCommitted;
    private static readonly HashSet<string> productiveTiredCooks = new(StringComparer.Ordinal), productiveRestedCooks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> productiveCompletions = new(StringComparer.Ordinal);
    private static readonly HashSet<string> productiveMethods = new(StringComparer.Ordinal);

    [Tool("test/productive_cooking_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepared demanded simple meals and near-next-level cooks, no selected worker or activity. Verify native counterfactual rates before normal planner starts.")]
    public async Task<object> ProductiveCookingPrepare(IRimBridgeContext ctx, CancellationToken ct)
    {
        await PrepareCooking(ctx, ct).ConfigureAwait(false);
        return await ctx.MainThread.InvokeAsync<object>(() =>
        {
            productiveActive = false;
            var map = worker!.Map;
            var originalTick = Find.TickManager.TicksGame;
            var hour = GenLocalDate.DayPercent(worker) * 24;
            Find.TickManager.DebugSetTicksGame(originalTick + (int)Math.Round(((12 - hour + 24) % 24) * GenDate.TicksPerHour));
            cookingBench.BillStack.Delete(otherBill);
            selectedBill.repeatCount = 6;
            firstIngredient.Destroy(); secondIngredient.Destroy();
            foreach (var food in map.listerThings.AllThings.Where(t => t.Spawned && t != unselectedIngredient && t.def.IsNutritionGivingIngestible).ToArray()) food.Destroy();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToArray())
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                foreach (var type in DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t => !pawn.WorkTypeIsDisabled(t))) pawn.workSettings.SetPriority(type, 0);
                pawn.needs.food.CurLevelPercentage = .35f; pawn.needs.rest.CurLevelPercentage = .95f; pawn.needs.joy.CurLevelPercentage = .95f;
                pawn.foodRestriction?.CurrentFoodPolicy?.filter.SetAllow(unselectedIngredient.def, false);
                foreach (var food in pawn.inventory.innerContainer.Where(t => t.def.IsNutritionGivingIngestible).ToArray()) food.Destroy();
                var skill = pawn.skills.GetSkill(SkillDefOf.Cooking);
                skill.levelInt = 5; skill.xpSinceLastLevel = SkillRecord.XpRequiredToLevelUpFrom(5) - 1;
                skill.xpSinceMidnight = 0;
                var before = (skill.levelInt, skill.xpSinceLastLevel, skill.xpSinceMidnight);
                var rates = NativeProductiveSkill.Read(pawn, selectedBill.recipe, cookingBench)
                    ?? throw new InvalidOperationException("Prepared native cooking rate pipeline is unsupported.");
                if (before != (skill.levelInt, skill.xpSinceLastLevel, skill.xpSinceMidnight)) throw new InvalidOperationException("Rate read mutated live skill.");
                try
                {
                    foreach (var rate in rates.Rates)
                    {
                        // Disposable fixture mutation only: independently evaluate actual native levels without stat caching.
                        skill.levelInt = rate.StoredLevel;
                        var actual = selectedBill.recipe.workSpeedStat.Worker.GetValue(StatRequest.For(pawn))
                            * cookingBench.GetStatValue(selectedBill.recipe.workTableSpeedStat);
                        if (!rate.WorkPerTick.IsKnown || Math.Abs(rate.WorkPerTick.Value - actual) > .00001)
                            throw new InvalidOperationException("Native productive rate differs at stored level " + rate.StoredLevel);
                    }
                }
                finally
                {
                    skill.levelInt = before.levelInt;
                    skill.xpSinceLastLevel = before.xpSinceLastLevel;
                    skill.xpSinceMidnight = before.xpSinceMidnight;
                }
            }
            productiveCompletions.Clear(); productiveMethods.Clear(); productiveOutput = false;
            productiveUrgeTick = -1; productiveUrgeCompletions = 0; productiveSlept = false; productiveTiredCooks.Clear(); productiveRestedCooks.Clear();
            productiveStart = Find.TickManager.TicksGame; productiveInitialRaw = RawCount();
            SkillLearningVerifier.Start();
            if (!productivePatched)
            {
                var harmony = new Harmony("rimgovernor.fixture.productive-cooking");
                harmony.Patch(AccessTools.Method(typeof(SkillRecord), nameof(SkillRecord.Learn)), postfix:
                    new HarmonyMethod(typeof(PawnControlFixture), nameof(ProductiveLearned)));
                harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"), postfix:
                    new HarmonyMethod(typeof(PawnControlFixture), nameof(ProductiveOutput)));
                productivePatched = true;
            }
            productiveActive = true;
            return new { success = true, tick = productiveStart, nativeRatesVerified = true };
        }, ct).ConfigureAwait(false);
    }
    private static void ProductiveLearned(SkillRecord __instance, float xp)
    {
        if (!productiveActive || xp <= 0 || __instance.def != SkillDefOf.Cooking
            || __instance.Pawn.CurJob?.bill != selectedBill) return;
        var execution = NativePawnActivities.Read(__instance.Pawn);
        if (execution?.Method.StartsWith("coupled-", StringComparison.Ordinal) != true) return;
        productiveMethods.Add(execution.Method);
        if (productiveUrgeTick >= 0) (__instance.Pawn.needs.rest.CurLevel < .15f ? productiveTiredCooks : productiveRestedCooks).Add(__instance.Pawn.GetUniqueLoadID() + ":" + __instance.Pawn.CurJob.loadID);
        var id = __instance.Pawn.GetUniqueLoadID();
        productiveCompletions[id] = productiveCompletions.GetValueOrDefault(id) + 1;
    }
    private static void ProductiveOutput(Pawn ___pawn)
    {
        if (productiveActive && productiveUrgeTick >= 0 && ___pawn.CurJobDef == JobDefOf.LayDown && !___pawn.Awake()) productiveSlept = true;
        if (productiveActive && ___pawn == worker && ___pawn.Map.listerThings.AllThings.Any(t => t.def == selectedBill.recipe.products[0].thingDef && t.stackCount > 0))
            productiveOutput = true;
    }
    [Tool("test/productive_cooking_urge", Description="UNSAFE FOR MODEL EXECUTION. Make every colonist critically tired mid-run so an urgent rest obligation competes with prepared cooking development. Issues no work.")]
    public async Task<object> ProductiveCookingUrge(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            foreach (var pawn in worker!.Map.mapPawns.FreeColonistsSpawned) { pawn.needs.rest.CurLevelPercentage = .08f; pawn.needs.food.CurLevelPercentage = .9f; } // only rest is urgent, so eating cannot compete with the claim under test
            // The cooking lab has no bed; rest is only a servable need where a player bed exists.
            var map = worker!.Map;
            if (!map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Any())
            {
                var bedCell = GenRadial.RadialCellsAround(worker.Position, 10, false).First(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null && c.GetThingList(map).Count == 0 && c.DistanceTo(worker.Position) > 3);
                GenSpawn.Spawn(ThingDefOf.Bed, bedCell, map).SetFaction(Faction.OfPlayer);
            }
            productiveUrgeTick = Find.TickManager.TicksGame; productiveUrgeCompletions = productiveCompletions.Values.Sum(); productiveSlept = false; productiveTiredCooks.Clear(); productiveRestedCooks.Clear();
            productiveCommitted = worker!.Map.mapPawns.FreeColonistsSpawned.Count(p => NativePawnActivities.Read(p) != null); // tasks already handed out may finish
            selectedBill.repeatCount = Math.Min(selectedBill.repeatCount, 4); // finite demand: tasks already handed out may use up to 3, leaving one to resume and retire
            return new { success = true, tick = productiveUrgeTick, completions = productiveUrgeCompletions };
        }, ct).ConfigureAwait(false);
    [Tool("test/productive_cooking_recover", Description="UNSAFE FOR MODEL EXECUTION. Restore colonist rest after sleep was observed, standing in for hours of natural recovery so the case does not wait them out. Issues no work.")]
    public async Task<object> ProductiveCookingRecover(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            foreach (var pawn in worker!.Map.mapPawns.FreeColonistsSpawned) pawn.needs.rest.CurLevelPercentage = .9f;
            return new { success = true, tick = Find.TickManager.TicksGame };
        }, ct).ConfigureAwait(false);
    [Tool("test/productive_cooking_satisfy", Description="UNSAFE FOR MODEL EXECUTION. Withdraw the colony's remaining meal demand (the finite bill is deleted) after resumed development was observed, so retirement of the Method can be observed without waiting for the last repeat. Issues no work.")]
    public async Task<object> ProductiveCookingSatisfy(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (cookingBench.BillStack.Bills.Contains(selectedBill)) cookingBench.BillStack.Delete(selectedBill);
            return new { success = true, tick = Find.TickManager.TicksGame };
        }, ct).ConfigureAwait(false);
    [Tool("test/productive_cooking_read", Description="Read observed normal-planner recipe completions, food output, input consumption and actual Cooking skill. No work is issued.")]
    public async Task<object> ProductiveCookingRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() => new
        {
            success = true, tick = Find.TickManager.TicksGame, elapsed = Find.TickManager.TicksGame - productiveStart,
            methods = productiveMethods.OrderBy(m => m).ToArray(), completions = productiveCompletions.Values.Sum(),
            subsequentWork = productiveCompletions.Values.Any(count => count >= 2), output = productiveOutput,
            rawConsumed = productiveInitialRaw - RawCount(), required = firstCount + secondCount,
            skills = worker!.Map.mapPawns.FreeColonistsSpawned.Select(p => new { pawn = p.GetUniqueLoadID(), level = p.skills.GetSkill(SkillDefOf.Cooking).Level,
                xp = p.skills.GetSkill(SkillDefOf.Cooking).xpSinceLastLevel }).ToArray(),
            urgeTick = productiveUrgeTick, urgeCompletions = productiveUrgeCompletions, slept = productiveSlept, tiredCooks = productiveTiredCooks.Count, committedAtUrge = productiveCommitted, restedCooksAfterUrge = productiveRestedCooks.Count,
            minRest = worker!.Map.mapPawns.FreeColonistsSpawned.Min(p => p.needs.rest.CurLevel),
            billActive = cookingBench.BillStack.Bills.Contains(selectedBill), repeatLeft = selectedBill.repeatCount,
            owned = worker!.Map.mapPawns.FreeColonistsSpawned.Count(p => NativePawnActivities.Read(p) != null),
            cooking = worker!.Map.mapPawns.FreeColonistsSpawned.Count(p => p.CurJob?.bill == selectedBill),
            learningFailures = SkillLearningVerifier.Failures.ToArray()
        }, ct).ConfigureAwait(false);
}
