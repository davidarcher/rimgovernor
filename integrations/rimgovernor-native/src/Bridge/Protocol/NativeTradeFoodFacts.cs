#nullable enable
using System;
using RimWorld;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools {
    internal static class NativeTradeFoodFacts {
        internal static Obs.TradeFoodFacts? Read(ThingDef? def) {
            if (def == null || def.category != ThingCategory.Item || def.ingestible == null
                || !def.ingestible.HumanEdible || !def.IsNutritionGivingIngestible || def.IsDrug
                || def.IsMedicine || def.IsWeapon || def.IsApparel
                || (def.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) != 0) return null;
            var nutrition = def.GetStatValueAbstract(StatDefOf.Nutrition);
            if (float.IsNaN(nutrition) || float.IsInfinity(nutrition) || nutrition <= 0) return null;
            var kind = InCategory(def, "MeatRaw") ? Obs.FoodIngredientClass.Meat
                : InCategory(def, "PlantFoodRaw") ? Obs.FoodIngredientClass.Vegetable
                : InCategory(def, "AnimalProductRaw") ? Obs.FoodIngredientClass.AnimalProduct
                : (def.ingestible.foodType & FoodTypeFlags.Meal) != 0 ? Obs.FoodIngredientClass.Any
                : Obs.FoodIngredientClass.Unspecified;
            if (kind == Obs.FoodIngredientClass.Unspecified) return null;
            // Human meat is not a routine ingredient purchase.
            if (kind == Obs.FoodIngredientClass.Meat && def.ingestible.sourceDef?.race?.Humanlike == true) return null;
            return new Obs.TradeFoodFacts {
                Nutrition = nutrition, IngredientClass = kind,
                Prepared = kind == Obs.FoodIngredientClass.Any,
                NonPerishable = def.GetCompProperties<CompProperties_Rottable>() == null,
                Crop = kind == Obs.FoodIngredientClass.Vegetable,
            };
        }

        internal static bool Protein(ThingDef? def) {
            var food = Read(def);
            return food != null && !food.Prepared
                && (food.IngredientClass == Obs.FoodIngredientClass.Meat || food.IngredientClass == Obs.FoodIngredientClass.AnimalProduct);
        }

        internal static bool Crop(ThingDef? def) => Read(def)?.Crop == true;

        private static bool InCategory(ThingDef def, string name) {
            var category = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(name);
            return category != null && def.IsWithinCategory(category);
        }
    }
}
