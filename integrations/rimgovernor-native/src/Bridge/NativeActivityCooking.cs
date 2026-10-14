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
    internal static class NativeActivityCooking
    {
        // Validate the supplied choice. Never call JobOnThing or search for substitute ingredients/bills.
        internal static bool Resolve(Pawn pawn, Thing target, PawnActivityStep step,
            out Bill_Production? bill, out List<ThingCount> ingredients, out string reason)
        {
            bill = null; ingredients = new List<ThingCount>();
            reason = "cooking_bench_unavailable";
            if (step.Cooking is not { } choice || target is not Building_WorkTable bench || !NativeProductionBills.Usable(bench)
                || pawn.carryTracker.CarriedThing != null || target.IsForbidden(pawn)
                || !pawn.CanReserveAndReach(target, PathEndMode.InteractionCell, Danger.Some)
                || target.def.hasInteractionCell && !pawn.CanReserveSittableOrSpot(target.InteractionCell, target)) return false;
            var giver = DefDatabase<WorkGiverDef>.GetNamedSilentFail(step.WorkGiver);
            reason = "cooking_worker_unavailable";
            if (giver?.Worker is not WorkGiver_DoBill scanner || !scanner.ThingIsUsableBillGiver(target)
                || giver.workType != DefDatabase<WorkTypeDef>.GetNamed("Cooking") || pawn.WorkTagIsDisabled(giver.workTags)
                || pawn.WorkTypeIsDisabled(giver.workType) || scanner.MissingRequiredCapacity(pawn) != null) return false;
            bill = bench.BillStack.Bills.OfType<Bill_Production>().SingleOrDefault(b => b.GetUniqueLoadID() == choice.Bill);
            reason = "cooking_bill_unavailable";
            if (bill == null || bill is Bill_ProductionWithUft || bill.recipe.defName != choice.Recipe
                || !bill.ShouldDoNow() || !bill.PawnAllowedToStartAnew(pawn) || bill.GetStoreMode() != BillStoreModeDefOf.DropOnFloor) return false;
            var recipe = bill.recipe;
            reason = "cooking_recipe_unsupported";
            if (!NativeProductionBills.Recipe(bench, recipe) || recipe.workSkill != SkillDefOf.Cooking
                || recipe.UsesUnfinishedThing || recipe.Worker.GetType() != typeof(RecipeWorker)
                || recipe.specialProducts != null || recipe.products.Count != 1 || !recipe.products[0].thingDef.IsNutritionGivingIngestible
                || recipe.ingredients.Count == 0 || recipe.FirstSkillRequirementPawnDoesntSatisfy(pawn) != null
                || recipe.requiredGiverWorkType != null && recipe.requiredGiverWorkType != giver.workType) return false;
            ThingDef? ingredientDef = null;
            reason = "cooking_ingredient_unavailable";
            foreach (var selected in choice.Ingredients)
            {
                var thing = pawn.Map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == selected.Thing);
                if (thing == null || !thing.Spawned || thing.def.category != ThingCategory.Item || thing.stackCount < selected.Count
                    || thing.IsForbidden(pawn) || thing.IsBurning() || !bill.IsFixedOrAllowedIngredient(thing)
                    || !pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, Danger.Some, stackCount: selected.Count)
                    || thing.Position.DistanceTo(bench.Position) > bill.ingredientSearchRadius
                    || ingredientDef != null && thing.def != ingredientDef) return false;
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
    }

    /// <summary>Exact queued ingredients and one native recipe iteration. Product delivery is a separate assignment.</summary>
    public sealed class JobDriver_GovernorCook : JobDriver_DoBill
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
            var ingredientDef = job.targetQueueB[0].Thing.def;
            var required = job.countQueue.Sum(n => (long)n);
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.A);
            this.FailOn(() => !NativePawnActivities.Owned(pawn) || job.bill.DeletedOrDereferenced || job.bill.recipe != recipe || !job.bill.ShouldDoNow()
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
                if (job.placedThings == null || job.placedThings.Any(i => i.thing.Destroyed || i.thing.def != ingredientDef || i.thing.stackCount < i.Count)
                    || job.placedThings.Sum(i => (long)i.Count) != required) EndJobWith(JobCondition.Incompletable);
            });
            yield return VerifyIngredients();
            yield return Toils_Recipe.DoRecipeWork().FailOnDespawnedNullOrForbiddenPlacedThings(TargetIndex.A)
                .FailOnCannotTouch(TargetIndex.A, PathEndMode.InteractionCell);
            yield return Toils_Recipe.CheckIfRecipeCanFinishNow();
            yield return VerifyIngredients();
            yield return Toils_Recipe.FinishRecipeAndStartStoringProduct(TargetIndex.None);
        }
    }
}
