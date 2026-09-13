// Fake HomeBridge.BridgeTools.NativeAuthorityHooks, standing in for the real production class
// (integrations/rimgovernor-native/src/Runtime/Control/NativeAuthorityHooks.cs) that
// NativeAuthorityControlTools.Control() calls unconditionally. The real class needs a compile-time
// Harmony reference; this fake lets native-authority-control build and run without one, exactly as
// its original standalone project (native-authority-control/HookStub.cs) did.
//
// This file is EXCLUDED whenever $(HarmonyAssembly) is supplied, because the merged project then
// compiles the REAL NativeAuthorityHooks.cs instead (see NativeContractProbes.csproj) -- which has
// a different API shape (Health.Ready/Health.VerifiedTargets, not this fake's flat
// Ready/Initializations). native-authority-control's own test code assumes this fake's shape, so
// it is only verified in the default (no $(HarmonyAssembly)) build; building it together with a
// real Harmony reference remains an open follow-up, matching native-authority-hooks's own
// pre-existing "needs a licensed Harmony assembly to build at all" constraint.
namespace HomeBridge.BridgeTools
{
    internal static class NativeAuthorityHooks
    {
        public static bool Ready;
        public static int Initializations;
        public static NativeControlSnapshot InitializeForCurrentGame()
        {
            Initializations++;
            return Verse.Current.Game == null ? null : NativeControlAuthority.ForGame(Verse.Current.Game).SetHookHealth(Ready);
        }
    }
}
