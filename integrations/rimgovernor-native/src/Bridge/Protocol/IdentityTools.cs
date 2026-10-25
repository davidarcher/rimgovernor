using RimGovernor.Brain.GameAccess;
using Verse;

namespace RimGovernor.Native
{
    internal static class IdentityTools
    {
        // On the game thread: the tick read of the in-process facade. No colony map throws the unavailable exception.
        internal static RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick HandleReadTick(RimGovernor.Brain.GameOrders.Lifecycle.TickRequest parsed)
        {
            if (!GameThread.TryReadContext(Find.CurrentMap, out var context, out var unavailable))
                throw NativeFaults.Unavailable(unavailable);
            var tickManager = Find.TickManager;
            var loaded = new RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick
            {
                Context = context, Paused = tickManager.Paused, ForcePaused = tickManager.ForcePaused,
                TimeSpeed = tickManager.CurTimeSpeed.ToString()
            };
            var windows = Find.WindowStack?.Windows;
            if (windows != null)
            {
                foreach (var window in windows)
                {
                    if (window == null || !window.forcePause) continue;
                    loaded = loaded with { PausingWindows = [.. loaded.PausingWindows, new RimGovernor.Brain.GameFacts.Lifecycle.PausingWindow
                    {
                        NativeType = window.GetType().FullName ?? "", Title = window.optionalTitle ?? ""
                    }] };
                }
            }
            return loaded;
        }

        // On the game thread: the identity read of the in-process facade. No colony map throws the unavailable exception.
        internal static RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity HandleReadIdentity(RimGovernor.Brain.GameOrders.Lifecycle.IdentityRequest parsed)
        {
            if (!GameThread.TryReadContext(Find.CurrentMap, out var context, out var unavailable))
                throw NativeFaults.Unavailable(unavailable);
            return new RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity { Context = context, Paused = Find.TickManager.Paused };
        }
    }
}
