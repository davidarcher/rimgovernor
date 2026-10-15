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
    private static int productiveStart, productiveInitialRaw;
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
        var id = __instance.Pawn.GetUniqueLoadID();
        productiveCompletions[id] = productiveCompletions.GetValueOrDefault(id) + 1;
    }
    private static void ProductiveOutput(Pawn ___pawn)
    {
        if (productiveActive && ___pawn == worker && ___pawn.Map.listerThings.AllThings.Any(t => t.def == selectedBill.recipe.products[0].thingDef && t.stackCount > 0))
            productiveOutput = true;
    }
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
            learningFailures = SkillLearningVerifier.Failures.ToArray()
        }, ct).ConfigureAwait(false);
}
