#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Host.Sdk;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Native
{
    // control/pawn-activity-cooking (#3398): the staged stove, its two bills and three raw stacks bound by load id; exact
    // recipe executions installed straight into the native execution table, with a missing ingredient and a drained bench
    // injected after admission.
    public sealed partial class PawnControlFixture
    {
        // Each selected stack holds its half of one iteration plus this many units the recipe must leave.
        private const int SpareUnits = 2;

        private static Building_WorkTable cookingBench = null!;
        private static Bill_Production selectedBill = null!, otherBill = null!;
        private static Thing firstIngredient = null!, secondIngredient = null!, unselectedIngredient = null!;
        private static int firstCount, secondCount, expectedMeals, beforeRaw;
        private static IntVec3 firstIngredientCell;

        private static Bill_Production StagedBill(Building_WorkTable bench, string id) =>
            bench.BillStack.Bills.OfType<Bill_Production>().FirstOrDefault(b => b.GetUniqueLoadID() == id) ?? throw new ArgumentException($"No staged bill {id}.");

        [Tool("test/pawn_cooking_prepare", Description="UNSAFE FOR MODEL EXECUTION. Bind the staged cook, stove, selected and competing bills and the three raw stacks; clear the product off the map; stand every colonist still.")]
        public async Task<object> PrepareCooking(INativeContext ctx, CancellationToken ct,
            [ToolParameter(Description="The cook's load id.")] string cook = "",
            [ToolParameter(Description="The stove's load id.")] string bench = "",
            [ToolParameter(Description="The selected bill's load id.")] string selected = "",
            [ToolParameter(Description="The competing bill's load id.")] string other = "",
            [ToolParameter(Description="The first selected stack's load id.")] string first = "",
            [ToolParameter(Description="The second selected stack's load id.")] string second = "",
            [ToolParameter(Description="The unselected stack's load id.")] string unselected = "") => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = FixtureKit.PausedMap();
            worker = FixtureKit.Spawned<Pawn>(map, cook);
            cookingBench = FixtureKit.Spawned<Building_WorkTable>(map, bench);
            selectedBill = StagedBill(cookingBench, selected);
            otherBill = StagedBill(cookingBench, other);
            firstIngredient = FixtureKit.Spawned<Thing>(map, first);
            secondIngredient = FixtureKit.Spawned<Thing>(map, second);
            unselectedIngredient = FixtureKit.Spawned<Thing>(map, unselected);
            Still(map);
            var needed = selectedBill.recipe.ingredients.Sum(i => i.CountRequiredOfFor(unselectedIngredient.def, selectedBill.recipe, selectedBill));
            firstCount = firstIngredient.stackCount - SpareUnits; secondCount = secondIngredient.stackCount - SpareUnits;
            if (firstCount + secondCount != needed) throw new ArgumentException($"The selected stacks hold {firstCount}+{secondCount} usable units; one iteration needs {needed}.");
            firstIngredientCell = firstIngredient.Position;
            expectedMeals = selectedBill.recipe.products.Single().count;
            foreach (var meal in map.listerThings.AllThings.Where(t => t.def == selectedBill.recipe.products[0].thingDef).ToArray()) meal.Destroy();
            deadline = Find.TickManager.TicksGame + 6000;
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

        [Tool("test/pawn_cooking_select", Description="UNSAFE FOR MODEL EXECUTION. Install exact cooking, inject missing ingredient or blocked bench, then explicitly revise.")]
        public async Task<object> SelectCooking(INativeContext ctx, CancellationToken ct,
            [ToolParameter(Description="1 removes selected ingredient after installation; 2 drains bench fuel; 3 runs the explicit revised assignment.")] int attempt = 1) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (attempt is < 1 or > 3) throw new InvalidOperationException("Unknown fixture attempt.");
            if (attempt > 1 && NativePawnActivities.Read(worker!)?.State != "failed") throw new InvalidOperationException("Expected failed prior assignment.");
            if (attempt == 2)
            { firstIngredient = GenSpawn.Spawn(unselectedIngredient.def, firstIngredientCell, worker!.Map); firstIngredient.stackCount = firstCount + SpareUnits; }
            var fuel = cookingBench.GetComp<CompRefuelable>();
            if (attempt == 3) fuel.Refuel(fuel.Props.fuelCapacity);
            var selected = new List<PawnActivityIngredient> { new(firstIngredient.GetUniqueLoadID(), firstCount), new(secondIngredient.GetUniqueLoadID(), secondCount) };
            if (attempt == 3)
            {
                // Failure is not a rollback: bind current loose stocks, including any inputs placed by the prior job.
                selected.Clear();
                var remaining = firstCount + secondCount;
                foreach (var stack in worker!.Map.listerThings.AllThings.Where(t => t.def == unselectedIngredient.def && t != unselectedIngredient)
                    .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
                {
                    var count = Math.Min(remaining, stack.stackCount);
                    if (count > 0) selected.Add(new(stack.GetUniqueLoadID(), count));
                    remaining -= count;
                }
                if (remaining != 0) throw new InvalidOperationException("Selected inputs were lost, not merely moved.");
            }
            var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(g => g.workType == ExtraWorkTypeDefOf.Cooking && g.Worker is WorkGiver_DoBill scanner && scanner.ThingIsUsableBillGiver(cookingBench));
            var step = new PawnActivityStep(PawnActivityKind.Recipe, cookingBench.GetUniqueLoadID(), giver.defName, "", 0, deadline)
                { Recipe = new PawnActivityRecipe(selectedBill.GetUniqueLoadID(), selectedBill.recipe.defName,
                    EquatableArray.From(selected)) };
            var batch = new PawnActivities(((RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase)Context().NativeGeneration).Value, new[] {
                new PawnActivityOrder(worker!.GetUniqueLoadID(), "cooking", "food-method", attempt - 1, attempt, false, new[] { step }) });
            var before = NativePawnActivities.Read(worker);
            var invalid = batch with { Orders = new[] { batch.Orders[0] with { Steps = new[] { step with { Recipe = step.Recipe! with { Ingredients = EquatableArray.From(new[] { new PawnActivityIngredient(firstIngredient.GetUniqueLoadID(), 1) }) } } } } } };
            if (NativePawnActivities.Validate(invalid, Context()) == null || NativePawnActivities.Read(worker) != before)
                throw new InvalidOperationException("Wrong ingredient quantity was accepted or changed ownership.");
            NativePawnActivities.Apply(batch, Context());
            NativePawnActivities.Apply(batch, Context());
            // The idle colonists run vanilla with their work priorities off (#3669).
            if (attempt == 1) IdleColonists.Apply();
            if (attempt == 1) firstIngredient.Destroy();
            if (attempt == 2) fuel.ConsumeFuel(fuel.Fuel);
            beforeRaw = RawCount();
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

        private static int RawCount() => worker!.Map.listerThings.AllThings.Where(t => t.def == unselectedIngredient.def).Sum(t => t.stackCount);

        [Tool("test/pawn_cooking_read", Description="UNSAFE FOR MODEL EXECUTION. Read physical ingredients, edible products and both bills' remaining counts.")]
        public async Task<object> ReadRecipe(INativeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() => FixtureKit.Reply(new Fx.CookingRead
        {
            Execution = Execution(NativePawnActivities.Read(worker!)), Raw = RawCount(), BeforeRaw = beforeRaw,
            Required = firstCount + secondCount, Unselected = unselectedIngredient.stackCount,
            Meals = worker!.Map.listerThings.AllThings.Where(t => t.def == selectedBill.recipe.products[0].thingDef && t.IngestibleNow).Sum(t => t.stackCount),
            ExpectedMeals = expectedMeals, SelectedRemaining = selectedBill.repeatCount, OtherRemaining = otherBill.repeatCount,
            BenchUsable = NativeProductionBills.Usable(cookingBench),
            Forced = worker.CurJob?.playerForced == true,
        }), ct).ConfigureAwait(false);
    }
}
