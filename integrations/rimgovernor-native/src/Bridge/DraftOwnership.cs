#nullable enable
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Legacy labels remain separate from canonical owner/session/direction claims.
    internal static class DraftOwnership
    {
        private sealed class State { internal string? Owner; internal ulong Revision; internal bool Exhausted; }
        private static readonly ConditionalWeakTable<Pawn_DraftController, State> States = new ConditionalWeakTable<Pawn_DraftController, State>();
        private const string PatchOwner = "rimgovernor.draft-ownership";
        private static bool patched;
        internal static void Ensure()
        {
            if (patched) return;
            var setter = AccessTools.PropertySetter(typeof(Pawn_DraftController), "Drafted");
            if (setter == null) throw new MissingMethodException("Pawn_DraftController.Drafted");
            new Harmony(PatchOwner).Patch(setter, prefix: new HarmonyMethod(typeof(DraftOwnership), nameof(BeforeDraft)));
            patched = true;
        }
        internal static bool Healthy
        {
            get
            {
                if (!patched) return false;
                var setter = AccessTools.PropertySetter(typeof(Pawn_DraftController), "Drafted");
                return setter != null && Harmony.GetPatchInfo(setter)?.Prefixes.Any(p => p.owner == PatchOwner
                    && p.PatchMethod == AccessTools.Method(typeof(DraftOwnership), nameof(BeforeDraft))) == true;
            }
        }
        private static void BeforeDraft(Pawn_DraftController __instance)
        {
            var state = States.GetOrCreateValue(__instance);
            state.Owner = null;
            if (state.Revision == ulong.MaxValue) state.Exhausted = true; else state.Revision++;
        }
        internal static ulong? Revision(Pawn pawn)
        {
            if (pawn.drafter == null) return null;
            return States.TryGetValue(pawn.drafter, out var state) ? state.Exhausted ? (ulong?)null : state.Revision : 0;
        }
        internal static string? Owner(Pawn pawn)
        {
            Ensure();
            return pawn?.drafter != null && States.TryGetValue(pawn.drafter, out var state) ? state.Owner : null;
        }
        internal static void Acquire(Pawn pawn, string owner)
        {
            if (pawn?.drafter != null && pawn.Drafted && !string.IsNullOrEmpty(owner)) States.GetOrCreateValue(pawn.drafter).Owner = owner;
        }
    }
}
