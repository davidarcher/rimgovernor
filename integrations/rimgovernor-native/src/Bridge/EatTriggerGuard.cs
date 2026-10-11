#nullable enable
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // A lower eating trigger per pawn (#2794). Vanilla JobGiver_GetFood.GetPriority
    // returns 9.5 once the food bar is under RaceProps.FoodLevelPercentageWantEat
    // (0.30); the postfix turns that into 0 while the bar is still at or above the
    // pawn's own trigger, so the pawn waits and wastes less of a meal. Emergency
    // categories (Starving) and everything else vanilla decides stay untouched, and
    // a trigger at or above 0.30 stores nothing: vanilla is restored.
    internal static class EatTriggerGuard
    {
        internal const float Vanilla = 0.30f;
        private const float WantEatPriority = 9.5f;
        private static bool installed;
        private static EatTriggerState? State => Current.Game?.GetComponent<EatTriggerState>();

        internal static float Effective(Pawn pawn) =>
            State != null && State.Triggers.TryGetValue(pawn.thingIDNumber, out var level) ? level : Vanilla;

        // Returns true when the effective trigger changed.
        internal static bool Set(Pawn pawn, float level)
        {
            var state = State ?? throw new System.InvalidOperationException("No game to hold the eat trigger.");
            var before = Effective(pawn);
            if (level >= Vanilla) state.Triggers.Remove(pawn.thingIDNumber);
            else state.Triggers[pawn.thingIDNumber] = level;
            return before != Effective(pawn);
        }

        internal static void Install()
        {
            if (installed) return;
            new Harmony("rimgovernor.eat-trigger").Patch(
                AccessTools.Method(typeof(JobGiver_GetFood), nameof(JobGiver_GetFood.GetPriority)),
                postfix: new HarmonyMethod(typeof(EatTriggerGuard), nameof(Defer)));
            installed = true;
        }

        private static void Defer(Pawn pawn, ref float __result)
        {
            if (__result != WantEatPriority || State == null || pawn.needs?.food == null) return;
            if (State.Triggers.TryGetValue(pawn.thingIDNumber, out var level) && pawn.needs.food.CurLevelPercentage >= level)
                __result = 0f;
        }
    }
}
