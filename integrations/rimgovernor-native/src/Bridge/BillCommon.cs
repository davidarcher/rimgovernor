#nullable enable

using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>Bill facts the typed production and hunt reads share.</summary>
    internal static class BillCommon
    {
        /// <summary>
        /// How many of the product the colony holds, for a TargetCount bill only,
        /// or null. Read through the game's own RecipeWorkerCounter so the number
        /// matches the one the bill's "N/M" label shows. It needs a bill that is
        /// attached to a bench (bill.Map), so a clone or an unadded bill answers
        /// null rather than a wrong number.
        /// </summary>
        internal static int? ProductCount(Bill_Production? production)
        {
            if (production == null)
                return null;
            if (production.repeatMode != BillRepeatModeDefOf.TargetCount)
                return null;
            // CountProducts reads bill.Map, so an unattached bill has no count.
            if (production.billStack?.billGiver?.Map == null)
                return null;
            if (!production.recipe.WorkerCounter.CanCountProducts(production))
                return null;
            return production.recipe.WorkerCounter.CountProducts(production);
        }

        /// <summary>A RepeatCount bill with no repetitions left.</summary>
        internal static bool IsFinished(Bill_Production? production)
        {
            if (production == null)
                return false;
            return production.repeatMode == BillRepeatModeDefOf.RepeatCount
                && production.repeatCount <= 0;
        }
    }
}
