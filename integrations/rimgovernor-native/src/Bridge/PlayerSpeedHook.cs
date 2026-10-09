#nullable enable

using System;
using System.Threading;
using HarmonyLib;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>The speed the player last chose in the loaded game:
    /// every non-paused CurTimeSpeed assignment except the supervisor's own
    /// (Owned). The owner starts each clock window at it, so the bot plays at
    /// the speed the player picked before the owner paused between windows.
    /// RimWorld's prePauseTimeSpeed is not it: the owner pauses by assignment,
    /// which never records one, and a letter's Pause records the bot's speed.
    /// A new Game starts with none.</summary>
    internal static class PlayerSpeedHook
    {
        private static int _patched;
        private static int _owned;
        private static Game? _game;
        private static TimeSpeed _speed;

        /// Install once, from the first main-thread hop (ProtoBoundary): this
        /// assembly runs no startup constructor. A failure is left
        /// unrecorded: the owner then plays Ultrafast, as on a fresh load.
        internal static void Ensure()
        {
            if (Interlocked.CompareExchange(ref _patched, 1, 0) != 0) return;
            try
            {
                var setter = AccessTools.PropertySetter(typeof(TickManager), "CurTimeSpeed");
                if (setter == null) return;
                new Harmony("rimgovernor.player-speed").Patch(setter, postfix: new HarmonyMethod(typeof(PlayerSpeedHook), nameof(AfterSpeed)));
            }
            catch (Exception) { }
        }

        /// Runs a supervisor speed assignment, which is never the player's.
        internal static void Owned(Action assign)
        {
            _owned++;
            try { assign(); }
            finally { _owned--; }
        }

        private static void AfterSpeed(TickManager __instance)
        {
            if (_owned > 0) return;
            var speed = __instance.CurTimeSpeed;
            if (speed == TimeSpeed.Paused) return;
            _game = Current.Game;
            _speed = speed;
        }

        /// The player's speed for the loaded game, or null when none was chosen.
        internal static TimeSpeed? Chosen() => _game != null && ReferenceEquals(_game, Current.Game) ? _speed : (TimeSpeed?)null;
    }
}
