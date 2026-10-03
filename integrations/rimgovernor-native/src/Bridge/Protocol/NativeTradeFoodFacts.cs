#nullable enable
using System;
using RimWorld;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools {
    internal static class NativeTradeFoodFacts {
        // The nutrition stat of an edible trade item; what kind of food it is
        // (raw class, meal, crop, rot) is read from the definition catalog.
        internal static Obs.TradeFoodFacts? Read(ThingDef? def) {
            if (def == null || def.category != ThingCategory.Item || def.ingestible == null
                || !def.ingestible.HumanEdible || !NativeFoodPolicy.IsFood(def)
                || def.IsMedicine || def.IsWeapon || def.IsApparel
                || (def.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) != 0) return null;
            var nutrition = def.GetStatValueAbstract(StatDefOf.Nutrition);
            if (float.IsNaN(nutrition) || float.IsInfinity(nutrition) || nutrition <= 0) return null;
            return new Obs.TradeFoodFacts { Nutrition = nutrition };
        }
    }
}
