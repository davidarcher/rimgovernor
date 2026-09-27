#nullable enable
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Native Dialog_FormCaravan calculation for NativeCaravanOperations'
    // FormCaravanIntent handler: the private CalculateAndRecacheTransferables
    // call without opening a window or taking camera ownership. Stateless:
    // every call recomputes from current native state, per
    // native-static-state.md.
    internal static class NativeCaravanCatalog
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        internal static object? Call(Dialog_FormCaravan dialog, string name, params object[] args) =>
            typeof(Dialog_FormCaravan).GetMethod(name, Private)!.Invoke(dialog, args);
        internal static void Set(Dialog_FormCaravan dialog, string name, object value) =>
            typeof(Dialog_FormCaravan).GetField(name, Private)!.SetValue(dialog, value);
        internal static (float days, float tillRot) FoodDays(Dialog_FormCaravan dialog) =>
            ((float, float))typeof(Dialog_FormCaravan).GetProperty("DaysWorthOfFood", Private)!.GetValue(dialog)!;

        internal static Dialog_FormCaravan BuildDialog(Map map)
        {
            var dialog = new Dialog_FormCaravan(map);
            Set(dialog, "autoSelectTravelSupplies", false);
            Call(dialog, "CalculateAndRecacheTransferables");
            foreach (var group in dialog.transferables) group.ForceToDestination(0);
            return dialog;
        }

        internal static bool PawnEligible(Pawn pawn) => pawn.IsFreeColonist && !pawn.Downed && !pawn.Dead
            && !pawn.Drafted && !pawn.InMentalState && pawn.GetLord() == null;
    }
}
