#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;

namespace HomeBridge.BridgeTools
{
    internal static class NativeActivityRecipe
    {
        internal static void Cleanup(Bill? bill)
        {
            if (bill is Bill_GovernorRecipe owned && owned.BoundUft == null && !owned.DeletedOrDereferenced)
            {
                // Cleanup of this exact admitted intent is not a player bill edit.
                using var attribution = NativeControlAuthority.TryGetForGame(Current.Game, out var authority)
                    && authority?.Status().Active == true ? authority.Owned() : null;
                owned.billStack.Delete(owned);
            }
        }
        internal static bool ResolveRecipe(Pawn pawn, Thing target, string workGiver, string billId, string recipeId,
            out Bill_Production? bill, out string reason)
        {
            bill = null;
            reason = "cooking_bench_unavailable";
            if (target is not Building_WorkTable bench || !NativeProductionBills.Usable(bench)
                || pawn.carryTracker.CarriedThing != null || target.IsForbidden(pawn)
                || !pawn.CanReserveAndReach(target, PathEndMode.InteractionCell, Danger.Some)
                || target.def.hasInteractionCell && !pawn.CanReserveSittableOrSpot(target.InteractionCell, target)) return false;
            var giver = DefDatabase<WorkGiverDef>.GetNamedSilentFail(workGiver);
            reason = "cooking_worker_unavailable";
            if (giver?.Worker is not WorkGiver_DoBill scanner || !scanner.ThingIsUsableBillGiver(target)
                || giver.workType != ExtraWorkTypeDefOf.Cooking || pawn.WorkTagIsDisabled(giver.workTags)
                || pawn.WorkTypeIsDisabled(giver.workType) || scanner.MissingRequiredCapacity(pawn) != null) return false;
            bill = bench.BillStack.Bills.OfType<Bill_Production>().SingleOrDefault(b => b.GetUniqueLoadID() == billId);
            reason = "cooking_bill_unavailable";
            if (bill == null || bill is Bill_ProductionWithUft || bill.recipe.defName != recipeId
                || !bill.ShouldDoNow() || !bill.PawnAllowedToStartAnew(pawn) || bill.GetStoreMode() != BillStoreModeDefOf.DropOnFloor) return false;
            var recipe = bill.recipe;
            reason = "cooking_recipe_unsupported";
            if (!NativeProductionBills.Recipe(bench, recipe) || recipe.workSkill != SkillDefOf.Cooking
                || recipe.UsesUnfinishedThing || recipe.Worker.GetType() != typeof(RecipeWorker)
                || recipe.specialProducts != null || recipe.products.Count != 1 || !recipe.products[0].thingDef.IsNutritionGivingIngestible
                || recipe.ingredients.Count == 0 || recipe.FirstSkillRequirementPawnDoesntSatisfy(pawn) != null
                || recipe.requiredGiverWorkType != null && recipe.requiredGiverWorkType != giver.workType) return false;
            return true;
        }
        // Validate the supplied choice. Never call JobOnThing or search for substitute ingredients/bills.
        internal static bool Resolve(Pawn pawn, Thing target, PawnActivityStep step,
            out Bill_Production? bill, out List<ThingCount> ingredients, out string reason, bool create = false)
        {
            ingredients = new List<ThingCount>(); bill = null; reason = "cooking_choice_missing";
            if (step.Recipe is { Stuff.Length: > 0 }) return ResolveTailoring(pawn, target, step, out bill, out ingredients, out reason, create);
            if (step.Recipe is not { } choice || !ResolveRecipe(pawn, target, step.WorkGiver, choice.Bill, choice.Recipe, out bill, out reason)) return false;
            var bench = (Building_WorkTable)target;
            var recipe = bill!.recipe;
            ThingDef? ingredientDef = null;
            reason = "cooking_ingredient_unavailable";
            foreach (var selected in choice.Ingredients)
            {
                var thing = RefIndex.Thing(pawn.Map, selected.Thing);
                if (thing == null || !thing.Spawned || thing.def.category != ThingCategory.Item || thing.stackCount < selected.Count
                    || thing.IsForbidden(pawn) || thing.IsBurning() || !bill.IsFixedOrAllowedIngredient(thing)
                    || !pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, Danger.Some, stackCount: selected.Count)
                    || thing.Position.DistanceTo(bench.Position) > bill.ingredientSearchRadius
                    || ingredientDef != null && thing.def != ingredientDef
                    || choice.Resource.Length != 0 && thing.def.defName != choice.Resource) return false;
                ingredientDef = thing.def;
                ingredients.Add(new ThingCount(thing, selected.Count));
            }
            reason = "cooking_ingredient_type_unsupported";
            if (ingredientDef == null || recipe.ingredients.Any(slot => !slot.filter.Allows(ingredientDef))) return false;
            // Single-resource recipes only: native slot counts include nutrition-based requirements.
            var selectedBill = bill;
            var required = recipe.ingredients.Sum(slot => (long)slot.CountRequiredOfFor(ingredientDef, recipe, selectedBill));
            reason = "cooking_quantity_mismatch";
            return required > 0 && ingredients.Sum(i => (long)i.Count) == required;
        }

        internal static bool TailoringRecipe(Building_WorkTable bench, RecipeDef recipe) =>
            NativeProductionBills.Recipe(bench, recipe) && recipe.Worker.GetType() == typeof(RecipeWorker)
            && recipe.specialProducts == null && recipe.products.Count == 1 && recipe.products[0].count == 1
            && recipe.products[0].thingDef.IsApparel && recipe.products[0].thingDef.MadeFromStuff
            && recipe.ingredients.Count > 0 && recipe.workSkill == SkillDefOf.Crafting
            && recipe.products[0].thingDef.stuffCategories != null
            && recipe.products[0].thingDef.stuffCategories.All(c => c == StuffCategoryDefOf.Fabric || c == StuffCategoryDefOf.Leathery);

        private static bool ResolveTailoring(Pawn pawn, Thing target, PawnActivityStep step,
            out Bill_Production? bill, out List<ThingCount> ingredients, out string reason, bool create)
        {
            bill = null; ingredients = new(); reason = "recipe_bench_unavailable";
            var choice = step.Recipe!;
            if (target is not Building_WorkTable bench || !NativeProductionBills.Usable(bench) || target.IsForbidden(pawn)
                || pawn.carryTracker.CarriedThing != null || !pawn.CanReserveAndReach(target, PathEndMode.InteractionCell, Danger.None)) return false;
            var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(choice.Recipe);
            var stuff = DefDatabase<ThingDef>.GetNamedSilentFail(choice.Stuff);
            var giver = DefDatabase<WorkGiverDef>.GetNamedSilentFail(step.WorkGiver);
            reason = "recipe_mechanics_unsupported";
            if (recipe == null || stuff == null || !TailoringRecipe(bench, recipe) || !recipe.products[0].thingDef.stuffCategories.Any(c => stuff.stuffProps?.categories.Contains(c) == true)
                || recipe.ingredients.Any(i => !i.filter.Allows(stuff)) || recipe.FirstSkillRequirementPawnDoesntSatisfy(pawn) != null
                || giver?.Worker is not WorkGiver_DoBill scanner || !scanner.ThingIsUsableBillGiver(bench)
                || pawn.WorkTagIsDisabled(giver.workTags) || pawn.WorkTypeIsDisabled(giver.workType) || scanner.MissingRequiredCapacity(pawn) != null) return false;
            if (choice.Bill.Length != 0)
            {
                bill = bench.BillStack.Bills.OfType<Bill_Production>().SingleOrDefault(b => b.GetUniqueLoadID() == choice.Bill);
                reason = "recipe_bill_changed";
                if (bill == null || bill.recipe != recipe || !bill.PawnAllowedToStartAnew(pawn)
                    || bill.GetStoreMode() != BillStoreModeDefOf.DropOnFloor || !bill.IsFixedOrAllowedIngredient(stuff)
                    || choice.Unfinished.Length == 0 && !bill.ShouldDoNow()) return false;
            }
            reason = "recipe_inputs_changed";
            if (choice.Unfinished.Length != 0)
            {
                var uft = RefIndex.Thing<UnfinishedThing>(pawn.Map, choice.Unfinished);
                if (uft == null || uft.Recipe != recipe || uft.Stuff != stuff || uft.Creator != pawn || uft.BoundBill != bill
                    || uft.IsForbidden(pawn) || !pawn.CanReserveAndReach(uft, PathEndMode.ClosestTouch, Danger.None)) return false;
                ingredients.Add(new(uft, 1));
            }
            else
            {
                foreach (var selected in choice.Ingredients)
                {
                    var thing = RefIndex.Thing(pawn.Map, selected.Thing);
                    if (thing == null || !thing.Spawned || thing.def != stuff || thing.stackCount < selected.Count || thing.IsForbidden(pawn) || thing.IsBurning()
                        || bill != null && thing.Position.DistanceTo(bench.Position) > bill.ingredientSearchRadius
                        || !pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, Danger.None, stackCount: selected.Count)) return false;
                    ingredients.Add(new(thing, selected.Count));
                }
                var selectedBill = bill;
                if (ingredients.Sum(i => (long)i.Count) != recipe.ingredients.Sum(i => (long)i.CountRequiredOfFor(stuff, recipe, selectedBill))) return false;
            }
            if (create && bill == null)
            {
                bill = new Bill_GovernorRecipe(recipe);
                bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor);
                bill.ingredientFilter.SetDisallowAll(); bill.ingredientFilter.SetAllow(stuff, true);
                // This exact recipe was already journaled and installed by Hands. Provisioning
                // occurs when its native job starts, after the dispatch scope has ended.
                using var attribution = NativeControlAuthority.ForGame(Current.Game).Owned();
                bench.BillStack.AddBill(bill);
            }
            reason = ""; return true;
        }
    }

    /// <summary>Game-owned unfinished work anchor, never an autonomous production request.</summary>
    public sealed class Bill_GovernorRecipe : Bill_ProductionWithUft
    {
        public Bill_GovernorRecipe() { }
        public Bill_GovernorRecipe(RecipeDef recipe) : base(recipe) { }
        // Native recipe work refuses suspended bills. Keep priority scanning ineligible
        // without suspending the exact job already authorized by Hands.
        public override bool ShouldDoNow() => false;
    }

    /// <summary>Exact queued ingredients and one native recipe iteration. Product delivery is a separate assignment.</summary>
    public sealed class JobDriver_GovernorRecipe : JobDriver_DoBill
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!NativePawnActivities.Owned(pawn)) return false;
            if (!pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed)
                || job.targetA.Thing.def.hasInteractionCell && !pawn.ReserveSittableOrSpot(job.targetA.Thing.InteractionCell, job, errorOnFailed)) return false;
            for (var i = 0; i < job.targetQueueB.Count; i++)
                if (!pawn.Reserve(job.targetQueueB[i], job, 1, job.countQueue[i], null, errorOnFailed)) return false;
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // A restored native job has no restored assignment or ingredient queue authorization.
            if (!NativePawnActivities.Owned(pawn))
            { yield return Toils_General.DoAtomic(() => EndJobWith(JobCondition.Incompletable)); yield break; }
            var recipe = job.bill.recipe;
            var ingredientDef = job.targetQueueB[0].Thing is UnfinishedThing unfinished ? unfinished.Stuff : job.targetQueueB[0].Thing.def;
            var resuming = job.targetQueueB[0].Thing is UnfinishedThing;
            var tailoring = recipe.products[0].thingDef.IsApparel;
            AddFinishAction(_ =>
            {
                // Preserve only actual unfinished work; never delete a player-created bill.
                NativeActivityRecipe.Cleanup(job.bill);
            });
            var required = job.countQueue.Sum(n => (long)n);
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.A);
            this.FailOn(() => !NativePawnActivities.Owned(pawn) || job.bill.DeletedOrDereferenced || job.bill.recipe != recipe || !tailoring && !job.bill.ShouldDoNow()
                || job.bill.GetStoreMode() != BillStoreModeDefOf.DropOnFloor || !job.bill.PawnAllowedToStartAnew(pawn)
                || !job.bill.IsFixedOrAllowedIngredient(ingredientDef) || !NativeProductionBills.Usable(job.targetA.Thing));
            this.FailOn(() => job.placedThings != null && job.placedThings.Any(i => i.thing.Destroyed || i.thing.stackCount < i.Count));
            yield return Toils_General.DoAtomic(() => job.bill.Notify_DoBillStarted(pawn));
            // The stock placement toil records placedThings only for JobDefOf.DoBill.
            // Our exact job must supply that callback explicitly, without pretending to be a vanilla-selected job.
            var extract = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.B);
            yield return extract;
            var approach = Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
                .FailOnForbidden(TargetIndex.B).FailOnSomeonePhysicallyInteracting(TargetIndex.B);
            yield return approach;
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, putRemainderInQueue: true, failIfStackCountLessThanJobCount: true, reserve: false);
            yield return JumpToCollectNextIntoHandsForBill(approach, TargetIndex.B);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell).FailOnDestroyedOrNull(TargetIndex.B);
            yield return Toils_JobTransforms.SetTargetToIngredientPlaceCell(TargetIndex.A, TargetIndex.B, TargetIndex.C);
            yield return Toils_General.DoAtomic(() =>
            {
                if (pawn.carryTracker.CarriedThing == null || !pawn.carryTracker.TryDropCarriedThing(job.targetC.Cell, ThingPlaceMode.Direct,
                    out _, (thing, count) => HaulAIUtility.UpdateJobWithPlacedThings(job, thing, count)))
                { EndJobWith(JobCondition.Incompletable); return; }
                pawn.Map.physicalInteractionReservationManager.Reserve(pawn, job, job.GetTarget(TargetIndex.B));
            });
            yield return Toils_Jump.JumpIfHaveTargetInQueue(TargetIndex.B, extract);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            Toil VerifyIngredients() => Toils_General.DoAtomic(() =>
            {
                if (resuming || job.targetB.Thing is UnfinishedThing) return;
                if (job.placedThings == null || job.placedThings.Any(i => i.thing.Destroyed || i.thing.def != ingredientDef || i.thing.stackCount < i.Count)
                    || job.placedThings.Sum(i => (long)i.Count) != required) EndJobWith(JobCondition.Incompletable);
            });
            yield return VerifyIngredients();
            yield return Toils_Recipe.MakeUnfinishedThingIfNeeded();
            yield return Toils_Recipe.DoRecipeWork().FailOnDespawnedNullOrForbiddenPlacedThings(TargetIndex.A)
                .FailOnCannotTouch(TargetIndex.A, PathEndMode.InteractionCell);
            yield return Toils_Recipe.CheckIfRecipeCanFinishNow();
            yield return VerifyIngredients();
            yield return Toils_Recipe.FinishRecipeAndStartStoringProduct(TargetIndex.None);
        }
    }
}
