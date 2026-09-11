using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class RecoveryAreaOwnership
    {
        private static bool patched;
        internal static void Ensure()
        {
            if (patched) return;
            new Harmony("rimgovernor.recovery-area-ownership").Patch(
                AccessTools.PropertySetter(typeof(Pawn_PlayerSettings), "AreaRestrictionInPawnCurrentMap"),
                prefix: new HarmonyMethod(typeof(RecoveryAreaOwnership), nameof(BeforeSetting)));
            patched = true;
        }
        private static void BeforeSetting(Pawn_PlayerSettings __instance)
        {
            var state = Current.Game?.GetComponent<RecoveryAreas>();
            if (state?.Claims == null) return;
            foreach (var claim in state.Claims.Where(c => c.Pawn?.playerSettings == __instance
                && c.Pawn.Map == c.Assigned?.Map).ToList())
            {
                state.Overrides.Add(claim.Pawn);
                state.Claims.Remove(claim);
            }
        }
    }
}
