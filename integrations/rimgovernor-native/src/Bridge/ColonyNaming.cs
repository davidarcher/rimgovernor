#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class ColonyNamingTools
    {
        internal static Dialog_NamePlayerFactionAndSettlement? Pending()
        {
            // The topmost force-pausing window, not the only one: in live play
            // the naming prompt can open over (or under) another modal, and
            // an exact-one rule left it unobserved, classified as a generic
            // force pause nothing answers. A dialog stacked above it is
            // answered first through its own path, then this one is on top.
            var top = Find.WindowStack?.Windows.LastOrDefault(w => w != null && w.forcePause);
            return top as Dialog_NamePlayerFactionAndSettlement;
        }

        internal static string? Name(Dialog_GiveName dialog, string field) =>
            (AccessTools.Field(typeof(Dialog_GiveName), field)?.GetValue(dialog) as string)?.Trim();

        // The letter is the mod's to answer, not the controller's: when the
        // initial naming dialog is on top, accept the game's own suggestions
        // through the native validators. A refused suggestion leaves the
        // dialog open for the player rather than guessing another name.
        internal static void ConfirmSuggested()
        {
            var dialog = Pending();
            if (dialog == null) return;
            var faction = Name(dialog, "curName");
            var settlement = Name(dialog, "curSecondName");
            if (string.IsNullOrEmpty(faction) || string.IsNullOrEmpty(settlement)) return;
            var type = typeof(Dialog_NamePlayerFactionAndSettlement);
            if (!(bool)AccessTools.Method(type, "IsValidName").Invoke(dialog, new object[] { faction! })
                || !(bool)AccessTools.Method(type, "IsValidSecondName").Invoke(dialog, new object[] { settlement! }))
                return;
            Confirm(dialog, faction!, settlement!);
        }

        // Names the player faction and settlement through the dialog's own
        // callbacks and closes it (ConfirmSuggested and the new-colony start).
        internal static void Confirm(Dialog_NamePlayerFactionAndSettlement dialog, string factionName, string settlementName)
        {
            var type = typeof(Dialog_NamePlayerFactionAndSettlement);
            AccessTools.Method(type, "Named").Invoke(dialog, new object[] { factionName });
            AccessTools.Method(type, "NamedSecond").Invoke(dialog, new object[] { settlementName });
            Messages.Message("PlayerFactionAndBaseGainsName".Translate(factionName, settlementName), MessageTypeDefOf.TaskCompletion, historical: false);
            Find.WindowStack.TryRemove(dialog);
        }
    }
}
