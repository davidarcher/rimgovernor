#nullable enable
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // A saved, explicitly requested reserve bill. Only its stock accounting
    // differs: brewing, hauling and fermentation remain ordinary pawn work.
    public sealed class SocialBeerBill : Bill_Production
    {
        public SocialBeerBill() { }
        public SocialBeerBill(RecipeDef recipe) : base(recipe, null) { }
        private static bool installed;
        internal static void Install()
        {
            if (installed) return;
            new Harmony("rimgovernor.beer-reserve").Patch(AccessTools.Method(typeof(RecipeWorkerCounter), nameof(RecipeWorkerCounter.CountProducts)),
                postfix: new HarmonyMethod(typeof(SocialBeerBill), nameof(Count)));
            installed = true;
        }
        private static void Count(Bill_Production bill, ref int __result)
        {
            if (!(bill is SocialBeerBill) || bill.Map == null) return;
            var beer = DefDatabase<ThingDef>.GetNamedSilentFail("Beer");
            if (beer != null) __result += bill.Map.resourceCounter.GetCount(beer);
            foreach (var barrel in bill.Map.listerBuildings.allBuildingsColonist.OfType<Building_FermentingBarrel>())
                __result += (int)AccessTools.Field(typeof(Building_FermentingBarrel), "wortCount").GetValue(barrel);
        }
    }
}
