#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace HomeBridge.BridgeTools
{
    internal static class ProtoIdentityTools
    {
        // On the game thread: the tick read, shared by the tool and the in-process facade.
        internal static Lifecycle.TickReply HandleReadTick(Lifecycle.TickRequest parsed)
        {
            if (!ProtoBoundary.TryReadContext(Find.CurrentMap, out var context, out var unavailable))
                return new Lifecycle.TickReply { Outcome = unavailable };
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
            return new Lifecycle.TickReply { Outcome = loaded };
        }

        // On the game thread: the identity read, shared by the tool and the in-process facade.
        internal static Lifecycle.IdentityReply HandleReadIdentity(Lifecycle.IdentityRequest parsed)
        {
            if (!ProtoBoundary.TryReadContext(Find.CurrentMap, out var context, out var unavailable))
            {
                ReadFailureLog.Note("lifecycle_read_identity", unavailable.Reason + ": " + unavailable.Detail);
                return new Lifecycle.IdentityReply();
            }
            return new Lifecycle.IdentityReply { Loaded = new Lifecycle.LoadedIdentity { Context = context, Paused = Find.TickManager.Paused } };
        }
    }
}
