#nullable enable

using System;
using HarmonyLib;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Ends decorative presentation state when the game or the viewed map it
    /// was opened against goes away. Two things outlive their tool call:
    ///
    ///   - a <see cref="Watch.Session"/>, whose close runs from a Task.Delay
    ///     continuation. After a load, that close would run against the NEW
    ///     game's UI, and because MainButtonDefs are shared across games its
    ///     "only undo what we opened" check cannot tell the two apart. The
    ///     session is abandoned instead: nothing is touched, Current is cleared.
    ///   - a <see cref="PawnImageCapture"/> request, which otherwise waits for
    ///     its four-second deadline (Game.UpdatePlay stops running once there is
    ///     no game, so BeforeDraw can never fail it sooner). It is failed now.
    ///
    /// Both setters can run on the loading thread, so nothing here touches UI.
    /// Installed lazily by the first Watch or capture; idempotent.
    /// </summary>
    internal static class PresentationLifecycle
    {
        private const string Owner = "rimgovernor.presentation-lifecycle";
        private static readonly object Sync = new object();
        private static bool installed;

        internal static void EnsurePatched()
        {
            lock (Sync)
            {
                if (installed) return;
                installed = true;
                try
                {
                    var harmony = new Harmony(Owner);
                    harmony.Patch(AccessTools.PropertySetter(typeof(Current), "Game"),
                        prefix: new HarmonyMethod(typeof(PresentationLifecycle), nameof(BeforeGame)),
                        postfix: new HarmonyMethod(typeof(PresentationLifecycle), nameof(AfterGame)));
                    harmony.Patch(AccessTools.PropertySetter(typeof(Game), "CurrentMap"),
                        prefix: new HarmonyMethod(typeof(PresentationLifecycle), nameof(BeforeMap)),
                        postfix: new HarmonyMethod(typeof(PresentationLifecycle), nameof(AfterMap)));
                }
                catch (Exception)
                {
                    // Decorative cleanup only. Without the hook a stale watch
                    // closes by its own guards and a capture by its deadline.
                }
            }
        }

        private static void BeforeGame(out Game __state) => __state = Current.Game;
        private static void AfterGame(Game __state)
        {
            if (!ReferenceEquals(__state, Current.Game)) ContextLost("Loaded colony changed");
        }
        private static void BeforeMap(Game __instance, out Map __state) => __state = __instance.CurrentMap;
        private static void AfterMap(Game __instance, Map __state)
        {
            if (!ReferenceEquals(__state, __instance.CurrentMap)) ContextLost("Viewed map changed");
        }

        private static void ContextLost(string reason)
        {
            Watch.Abandon();
            PawnImageCapture.Abandon(reason);
        }
    }
}
