#nullable enable
using RimGovernor.Host.Sdk;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Gives ModLog (the rimgovernor.log helper, #2058) the game tick, so a mod
    // line lines up with the controller's rows. Read off any thread; -1 when
    // no game is loaded.
    internal static class ModLogTick
    {
        private static bool wired;

        internal static void Ensure()
        {
            if (wired) return;
            wired = true;
            ModLog.TickSource = Read;
        }

        private static long Read() => Current.Game == null ? -1 : (Find.TickManager?.TicksGame ?? -1);
    }
}
