#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
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
    }
}
