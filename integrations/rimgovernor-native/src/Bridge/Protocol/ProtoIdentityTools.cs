using RimGovernor.Brain.Bridge.Contracts;
using Verse;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace HomeBridge.BridgeTools
{
    internal static class ProtoIdentityTools
    {
        // On the game thread: the tick read of the in-process facade. No colony map throws the unavailable exception.
        internal static Lifecycle.LoadedTick HandleReadTick(Lifecycle.TickRequest parsed)
        {
            if (!ProtoBoundary.TryReadContext(Find.CurrentMap, out var context, out var unavailable))
                throw NativeFaults.Unavailable(unavailable);
            var tickManager = Find.TickManager;
            var loaded = new Lifecycle.LoadedTick
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
                    loaded = loaded with { PausingWindows = [.. loaded.PausingWindows, new Lifecycle.PausingWindow
                    {
                        NativeType = window.GetType().FullName ?? "", Title = window.optionalTitle ?? ""
                    }] };
                }
            }
            return loaded;
        }

        // On the game thread: the identity read of the in-process facade. No colony map throws the unavailable exception.
        internal static Lifecycle.LoadedIdentity HandleReadIdentity(Lifecycle.IdentityRequest parsed)
        {
            if (!ProtoBoundary.TryReadContext(Find.CurrentMap, out var context, out var unavailable))
                throw NativeFaults.Unavailable(unavailable);
            return new Lifecycle.LoadedIdentity { Context = context, Paused = Find.TickManager.Paused };
        }
    }
}
