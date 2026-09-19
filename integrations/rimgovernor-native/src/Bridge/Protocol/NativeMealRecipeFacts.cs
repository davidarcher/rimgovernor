#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools {
    // Definition facts only: mood excludes pawn-specific ideology/trait effects.
    internal static class NativeMealRecipeFacts {
        internal static void Fill(Obs.RecipeState row, ThingDef bench, RecipeDef recipe) {
            row.NeedsPower = bench.GetCompProperties<CompProperties_Power>() != null;
            if (recipe.products.Count != 1 || recipe.products[0].thingDef.ingestible == null) return;
            var product = recipe.products[0];
            var nutrition = product.thingDef.GetStatValueAbstract(StatDefOf.Nutrition) * product.count;
            if (!Positive(nutrition)) return;
            var thought = product.thingDef.ingestible.tasteThought;
            if (thought == null) row.Mood = 0;
            else if (thought.stages.Count == 1 && Finite(thought.stages[0].baseMoodEffect)) row.Mood = thought.stages[0].baseMoodEffect;
            var work = recipe.workAmount;
            if (work < 0) { try { work = recipe.WorkAmountTotal(null); } catch (Exception) { } }
            if (Finite(work) && work >= 0) row.WorkPerNutrition = work / nutrition;
            if (!(recipe.IngredientValueGetter is IngredientValueGetter_Nutrition) || recipe.ingredients.Count == 0) return;
            var classes = new Obs.RecipeIngredientClasses();
            double input = 0;
            foreach (var ingredient in recipe.ingredients) {
                if (!Positive(ingredient.GetBaseCount())) return;
                input += ingredient.GetBaseCount();
                var slot = new Obs.FoodIngredientSlot();
                foreach (var def in ingredient.filter.AllowedThingDefs.Where(d => recipe.fixedIngredientFilter.Allows(d) && recipe.defaultIngredientFilter.Allows(d))) {
                    if (def.ingestible == null) return;
                    // Milk has the Fluid food-type flag, but belongs to the
                    // AnimalProductRaw filter category. Recipe categories,
                    // including parent categories for eggs, own this contract.
                    var kind = InCategory(def, "MeatRaw") ? Obs.FoodIngredientClass.Meat
                        : InCategory(def, "PlantFoodRaw") ? Obs.FoodIngredientClass.Vegetable
                        : InCategory(def, "AnimalProductRaw") ? Obs.FoodIngredientClass.AnimalProduct
                        : Obs.FoodIngredientClass.Unspecified;
                    // An unclassified mod ingredient must not broaden a slot to "any".
                    if (kind == Obs.FoodIngredientClass.Unspecified) return;
                    if (!slot.Alternatives.Contains(kind)) slot.Alternatives.Add(kind);
                }
                if (slot.Alternatives.Count == 0) return;
                var ordered = slot.Alternatives.OrderBy(c => (int)c).ToArray();
                slot.Alternatives.Clear();
                if (ordered.Length == 3) slot.Alternatives.Add(Obs.FoodIngredientClass.Any);
                else slot.Alternatives.Add(ordered);
                classes.Slots.Add(slot);
            }
            if (!Positive(input)) return;
            row.IngredientClasses = classes;
            row.NutrientEfficiency = nutrition / input;
            foreach (var skill in recipe.skillRequirements ?? new System.Collections.Generic.List<SkillRequirement>())
                if (skill.skill == SkillDefOf.Cooking && !row.Skills.Any(s => s.DefName == skill.skill.defName))
                    row.Skills.Add(new Obs.SkillRequirement { DefName = skill.skill.defName, Minimum = skill.minLevel });
        }
        private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        private static bool Positive(double n) => Finite(n) && n > 0;
        private static bool InCategory(ThingDef def, string name) {
            var category = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(name);
            return category != null && def.IsWithinCategory(category);
        }
    }
}
