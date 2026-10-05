// Fake HomeBridge.BridgeTools.NativeAuthorityHooks, standing in for the real production class
// (integrations/rimgovernor-native/src/Runtime/Control/NativeAuthorityHooks.cs) that
// NativeAuthorityControlTools.Control() calls unconditionally. The real class needs a compile-time
// Harmony reference; this fake lets native-authority-control build and run without one, exactly as
// its original standalone project (native-authority-control/HookStub.cs) did.
//
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
        public static bool EnsureManualPriorities(Verse.Game game) => false;
    }
}
