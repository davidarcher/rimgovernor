#nullable enable
using Verse;
namespace HomeBridge.BridgeTools
{
    // The real invalidation hooks have a separate actual-Harmony suite. This seam
    // controls verified installation health without loading the game or patching it.
    public static class NativeAuthorityHooks
    {
        public static bool Ready;
        public static int Initializations;
        public static NativeControlSnapshot? InitializeForCurrentGame()
        {
            Initializations++;
            return Current.Game == null ? null : NativeControlAuthority.ForGame(Current.Game).SetHookHealth(Ready);
        }
    }
}
