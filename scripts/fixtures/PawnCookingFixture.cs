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

namespace HomeBridge.BridgeTools
{
    public sealed partial class PawnControlFixture
    {
        private static Building_WorkTable cookingBench = null!;
        private static Bill_Production selectedBill = null!, otherBill = null!;
        private static Thing firstIngredient = null!, secondIngredient = null!, unselectedIngredient = null!;
        private static int firstCount, secondCount, expectedMeals, beforeRaw;
        private static IntVec3 firstIngredientCell;

        [Tool("test/pawn_cooking_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepare a fueled stove, competing bills and exact ingredient stacks.")]
        public async Task<object> PrepareCooking(IRimBridgeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap;
            worker = map.mapPawns.FreeColonistsSpawned.First(p => !p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Cooking")));
            foreach (var p in map.mapPawns.FreeColonistsSpawned) p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 120000), JobCondition.InterruptForced);
            worker.skills.GetSkill(SkillDefOf.Cooking).Level = 20;
            var center = GenRadial.RadialCellsAround(worker.Position, 12, false).First(c => c.InBounds(map)
                && c.DistanceTo(worker.Position) > 5 && GenAdj.OccupiedRect(c, Rot4.North, new IntVec2(5, 5)).Cells.All(x => x.InBounds(map) && x.Standable(map) && x.GetEdifice(map) == null));
            foreach (var thing in GenRadial.RadialCellsAround(center, 5, true).Where(c => c.InBounds(map))
                .SelectMany(c => c.GetThingList(map)).Where(t => t.def.category == ThingCategory.Item).Distinct().ToArray()) thing.Destroy();
            cookingBench = (Building_WorkTable)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("FueledStove"), center, map);
            cookingBench.SetFaction(Faction.OfPlayer);
            var fuel = cookingBench.GetComp<CompRefuelable>(); fuel.Refuel(fuel.Props.fuelCapacity);
            otherBill = (Bill_Production)DefDatabase<RecipeDef>.GetNamed("CookMealSimpleBulk").MakeNewBill();
            selectedBill = (Bill_Production)DefDatabase<RecipeDef>.GetNamed("CookMealSimple").MakeNewBill();
            foreach (var bill in new[] { otherBill, selectedBill })
            { bill.repeatMode = BillRepeatModeDefOf.RepeatCount; bill.repeatCount = 2; bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor, null); cookingBench.BillStack.AddBill(bill); }
            var raw = DefDatabase<ThingDef>.GetNamed("RawRice");
            var needed = selectedBill.recipe.ingredients.Sum(i => i.CountRequiredOfFor(raw, selectedBill.recipe, selectedBill));
            firstCount = needed / 2; secondCount = needed - firstCount;
            firstIngredientCell = center + new IntVec3(3, 0, 0);
            firstIngredient = GenSpawn.Spawn(raw, firstIngredientCell, map); firstIngredient.stackCount = firstCount + 2;
            secondIngredient = GenSpawn.Spawn(raw, center + new IntVec3(-3, 0, 0), map); secondIngredient.stackCount = secondCount + 2;
            unselectedIngredient = GenSpawn.Spawn(raw, center + new IntVec3(0, 0, 2), map); unselectedIngredient.stackCount = 60;
            expectedMeals = selectedBill.recipe.products.Single().count;
            foreach (var meal in map.listerThings.AllThings.Where(t => t.def == selectedBill.recipe.products[0].thingDef).ToArray()) meal.Destroy();
            deadline = Find.TickManager.TicksGame + 6000;
            var destination = GenRadial.RadialCellsAround(center, 8, false).First(c => c.InBounds(map) && c.Standable(map)
                && c.DistanceTo(center) > 5 && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item));
            var storage = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(storage);
            storage.GetStoreSettings().filter.SetDisallowAll();
            storage.GetStoreSettings().filter.SetAllow(selectedBill.recipe.products[0].thingDef, true);
            storage.AddCell(destination);
            return new { success = true };
        }, ct).ConfigureAwait(false);

        [Tool("test/pawn_cooking_select", Description="UNSAFE FOR MODEL EXECUTION. Install exact cooking, inject missing ingredient or blocked bench, then explicitly revise.")]
        public async Task<object> SelectCooking(IRimBridgeContext ctx, CancellationToken ct,
            [ToolParameter(Description="1 removes selected ingredient after installation; 2 drains bench fuel; 3 runs the explicit revised assignment.")] int attempt = 1) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (attempt is < 1 or > 3) throw new InvalidOperationException("Unknown fixture attempt.");
            if (attempt == 1)
            {
                var map = worker!.Map;
                var center = cookingBench.Position;
                var raw = unselectedIngredient.def;
                var needed = firstCount + secondCount;
                var destination = map.haulDestinationManager.AllGroupsListInPriorityOrder.SelectMany(g => g.CellsList).Single();
                var observations = new List<RimGovernor.Contracts.Wire.Observations.FoodActivityOption>();
                var budget = 128; var truncated = false;
                var obligation = new PawnActivityOrder(worker.GetUniqueLoadID(), "candidate-probe", "food-method", 0, 1, false,
                    new[] { new PawnActivityStep(PawnActivityKind.Work, "observed-harvest", "harvest", "Harvest", 0, deadline) })
                    { OutputDelivery = new(raw.defName, new(center.x, center.z), new(destination.x, destination.z), 60, deadline, 3) };
                NativeFoodCooking.Add(worker, unselectedIngredient, 60, 0, obligation, observations, ref budget, ref truncated);
                if (!observations.Any(o => o.Cooking?.Continuation.Bill == selectedBill.GetUniqueLoadID()
                    && o.Cooking.IngredientCount == needed && o.Cooking.CookTicks > 0 && o.Cooking.ProductNutrition.IsKnown))
                {
                    var probeGiver = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(g => g.workType == DefDatabase<WorkTypeDef>.GetNamed("Cooking")
                        && g.Worker is WorkGiver_DoBill scanner && scanner.ThingIsUsableBillGiver(cookingBench));
                    NativeActivityRecipe.ResolveRecipe(worker, cookingBench, probeGiver.defName, selectedBill.GetUniqueLoadID(), selectedBill.recipe.defName, out _, out var reason);
                    using var toBench = map.pathFinder.FindPathNow(unselectedIngredient.Position, cookingBench.InteractionCell, TraverseParms.For(worker, Danger.None), peMode: PathEndMode.OnCell);
                    using var toStore = map.pathFinder.FindPathNow(cookingBench.InteractionCell, destination, TraverseParms.For(worker, Danger.None), peMode: PathEndMode.OnCell);
                    throw new InvalidOperationException($"Native cooking candidate read omitted the eligible exact recipe: {reason}; options={observations.Count}; budget={budget}; work={selectedBill.recipe.WorkAmountTotal(null)}; benchRoute={toBench.Found}; storeRoute={toStore.Found}; interaction={cookingBench.InteractionCell}; destination={destination}.");
                }
                }
            if (attempt > 1 && NativePawnActivities.Read(worker!)?.State != "failed") throw new InvalidOperationException("Expected failed prior assignment.");
            if (attempt == 2)
            { firstIngredient = GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("RawRice"), firstIngredientCell, worker!.Map); firstIngredient.stackCount = firstCount + 2; }
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
            var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(g => g.workType == DefDatabase<WorkTypeDef>.GetNamed("Cooking") && g.Worker is WorkGiver_DoBill scanner && scanner.ThingIsUsableBillGiver(cookingBench));
            var step = new PawnActivityStep(PawnActivityKind.Recipe, cookingBench.GetUniqueLoadID(), giver.defName, "", 0, deadline)
                { Recipe = new PawnActivityRecipe(selectedBill.GetUniqueLoadID(), selectedBill.recipe.defName,
                    EquatableArray.From(selected)) };
            var batch = new PawnActivities(Context().NativeGeneration!.Value, new[] {
                new PawnActivityOrder(worker!.GetUniqueLoadID(), "cooking", "food-method", attempt - 1, attempt, false, new[] { step }) });
            var before = NativePawnActivities.Read(worker);
            var invalid = batch with { Orders = new[] { batch.Orders[0] with { Steps = new[] { step with { Recipe = step.Recipe! with { Ingredients = EquatableArray.From(new[] { new PawnActivityIngredient(firstIngredient.GetUniqueLoadID(), 1) }) } } } } } };
            if (NativePawnActivities.Validate(invalid, Context()) == null || NativePawnActivities.Read(worker) != before)
                throw new InvalidOperationException("Wrong ingredient quantity was accepted or changed ownership.");
            NativePawnActivities.Apply(batch, Context());
            NativePawnActivities.Apply(batch, Context());
            if (attempt == 1) firstIngredient.Destroy();
            if (attempt == 2) fuel.ConsumeFuel(fuel.Fuel);
            beforeRaw = RawCount();
            return new { success = true };
        }, ct).ConfigureAwait(false);

        private static int RawCount() => worker!.Map.listerThings.AllThings.Where(t => t.def == unselectedIngredient.def).Sum(t => t.stackCount);

        [Tool("test/pawn_cooking_read", Description="UNSAFE FOR MODEL EXECUTION. Read physical ingredients, edible products and both bills' remaining counts.")]
        public async Task<object> ReadRecipe(IRimBridgeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() => new
        {
            success = true, execution = NativePawnActivities.Read(worker!), raw = RawCount(), beforeRaw,
            required = firstCount + secondCount, unselected = unselectedIngredient.stackCount,
            meals = worker!.Map.listerThings.AllThings.Where(t => t.def == selectedBill.recipe.products[0].thingDef && t.IngestibleNow).Sum(t => t.stackCount),
            expectedMeals, selectedRemaining = selectedBill.repeatCount, otherRemaining = otherBill.repeatCount,
            benchUsable = NativeProductionBills.Usable(cookingBench),
            ingredientStacks = worker.Map.listerThings.AllThings.Where(t => t.def == unselectedIngredient.def).Select(t => new { id = t.GetUniqueLoadID(), count = t.stackCount, cell = t.Position.ToString() }).ToArray(),
            forced = worker.CurJob?.playerForced == true,
        }, ct).ConfigureAwait(false);
    }
}
