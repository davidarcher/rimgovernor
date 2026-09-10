using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Ephemeral native claims: every later draft setter relinquishes the claim,
    // including an undraft/redraft entirely between controller observations.
    internal static class DraftOwnership
    {
        private sealed class Claim { internal string Owner; }
        private static readonly ConditionalWeakTable<Pawn_DraftController, Claim> Claims =
            new ConditionalWeakTable<Pawn_DraftController, Claim>();
        private static bool patched;

        internal static void Ensure()
        {
            if (patched) return;
            var setter = AccessTools.PropertySetter(typeof(Pawn_DraftController), "Drafted");
            if (setter == null) throw new MissingMethodException("Pawn_DraftController.Drafted");
            new Harmony("rimbot.draft-ownership").Patch(setter,
                prefix: new HarmonyMethod(typeof(DraftOwnership), nameof(BeforeDraft)));
            patched = true;
        }

        private static void BeforeDraft(Pawn_DraftController __instance)
        {
            Claims.Remove(__instance);
        }

        internal static string Owner(Pawn pawn)
        {
            Ensure();
            Claim claim;
            return pawn?.drafter != null && Claims.TryGetValue(pawn.drafter, out claim)
                ? claim.Owner : null;
        }

        internal static void Acquire(Pawn pawn, string owner)
        {
            if (pawn?.drafter != null && pawn.Drafted && !string.IsNullOrEmpty(owner))
                Claims.GetOrCreateValue(pawn.drafter).Owner = owner;
        }
    }
}
