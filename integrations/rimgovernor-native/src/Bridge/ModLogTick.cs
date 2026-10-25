using RimGovernor.Host.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Gives ModLog (the rimgovernor.log helper) the game tick, so a mod
    // line lines up with the controller's rows. Read off any thread; -1 when
    // no game is loaded.
    internal static class ModLogTick
    {
        internal static void Install() => ModLog.TickSource = Read;

        private static long Read() => Current.Game == null ? -1 : (Find.TickManager?.TicksGame ?? -1);
    }
}
