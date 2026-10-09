#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Spoilage preference (#2520): when the supervisor is active, the colony uses
    // its oldest stock first. Both patches only reorder or re-weigh candidates
    // vanilla already accepts (allowed, reachable, unforbidden, policy and filter
    // satisfied); neither makes a pawn use something vanilla would reject.
    // Non-perishables carry no rot comp and keep vanilla's order.
    internal static class SpoilagePreference
    {
        // A fresh stack at the rot window or beyond gets no bonus; one about to rot
        // gets the full MaxBonus. Vanilla scores 300 minus Manhattan distance and
        // already adds 12 to fresh food under 30000 ticks from rotting.
        internal const int RotWindowTicks = 60000;
        internal const float MaxBonus = 24f;
        // The acceptance fixture swaps this to exercise the patches without a running supervisor.
        internal static Func<bool> Active = () => Supervisor.IsActive;
        private static bool patched;

        internal static void Install()
        {
            if (patched) return;
            var harmony = new Harmony("rimgovernor.spoilage-preference");
            harmony.Patch(AccessTools.Method(typeof(WorkGiver_DoBill), "TryFindBestIngredientsInSet_NoMixHelper"),
                prefix: new HarmonyMethod(typeof(SpoilagePreference), nameof(SortIngredients)));
            harmony.Patch(AccessTools.Method(typeof(FoodUtility), nameof(FoodUtility.FoodOptimality)),
                postfix: new HarmonyMethod(typeof(SpoilagePreference), nameof(WeighFood)));
            patched = true;
        }

        // Ticks until the stack rots at its current temperature; int.MaxValue when it does not rot.
        internal static int TicksUntilRot(Thing t)
        {
            var rot = t.TryGetComp<CompRottable>();
            return rot == null ? int.MaxValue : rot.TicksUntilRotAtCurrentTemp;
        }

        internal static float RotBonus(int ticksUntilRot)
        {
            if (ticksUntilRot >= RotWindowTicks) return 0f;
            return MaxBonus * (1f - (ticksUntilRot < 0 ? 0 : ticksUntilRot) / (float)RotWindowTicks);
        }

        // Vanilla sorts the candidates by distance only; sort by least rot time
        // first and by distance within equal rot time, then mark them sorted.
        private static void SortIngredients(List<Thing> availableThings, IntVec3 rootCell, ref bool alreadySorted)
        {
            if (alreadySorted || !Active()) return;
            var ordered = availableThings.OrderBy(TicksUntilRot).ThenBy(t => (t.PositionHeld - rootCell).LengthHorizontalSquared).ToList();
            availableThings.Clear();
            availableThings.AddRange(ordered);
            alreadySorted = true;
        }

        private static void WeighFood(Thing foodSource, bool takingToInventory, ref float __result)
        {
            if (takingToInventory || !Active() || __result <= -9000000f) return;
            var rot = foodSource.TryGetComp<CompRottable>();
            if (rot == null || rot.Stage != RotStage.Fresh) return;
            __result += RotBonus(rot.TicksUntilRotAtCurrentTemp);
        }
    }
}
