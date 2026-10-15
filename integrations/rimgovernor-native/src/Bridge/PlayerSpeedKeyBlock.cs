#nullable enable

using System;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>While the bot holds the clock (native control authority active, set by
    /// the player Resume and withdrawn by Pause), the player's speed and pause input is
    /// swallowed: space, 1-4 and the time-control buttons all run inside
    /// <c>TimeControls.DoTimeControlsGUI</c>, so a change made while that call is on the
    /// stack is the player's. The key event is still consumed by the game; the clock is
    /// simply not touched, and a rate-limited message says why. Forced pauses from
    /// letters and dialogs, and the supervisor's own assignments (PlayerSpeedHook.Owned),
    /// are not in that scope and pass.</summary>
    internal static class PlayerSpeedKeyBlock
    {
        private const float MessageGapSeconds = 3f;
        private static int _patched;
        private static int _inPlayerInput;
        private static float _lastMessage = float.NegativeInfinity;

        /// <summary>Install once, from the first main-thread hop (ProtoBoundary).</summary>
        internal static void Ensure()
        {
            if (Interlocked.CompareExchange(ref _patched, 1, 0) != 0) return;
            try
            {
                var harmony = new Harmony("rimgovernor.player-speed-key-block");
                var gui = AccessTools.Method(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI));
                var setter = AccessTools.PropertySetter(typeof(TickManager), "CurTimeSpeed");
                var toggle = AccessTools.Method(typeof(TickManager), "TogglePaused");
                if (gui == null || setter == null || toggle == null) return;
                harmony.Patch(gui, prefix: new HarmonyMethod(typeof(PlayerSpeedKeyBlock), nameof(EnterInput)),
                    finalizer: new HarmonyMethod(typeof(PlayerSpeedKeyBlock), nameof(LeaveInput)));
                harmony.Patch(setter, prefix: new HarmonyMethod(typeof(PlayerSpeedKeyBlock), nameof(BeforeChange)));
                harmony.Patch(toggle, prefix: new HarmonyMethod(typeof(PlayerSpeedKeyBlock), nameof(BeforeChange)));
            }
            catch (Exception) { }
        }

        /// <summary>True while a player clock change would be swallowed. PlayerSpeedHook
        /// uses it to not record the swallowed change as the player's speed.</summary>
        internal static bool Blocking() => SpeedKeyBlockPolicy.Swallow(_inPlayerInput > 0, BotHoldsClock(), PlayerSpeedHook.IsOwned);

        private static bool BotHoldsClock()
        {
            var game = Current.Game;
            return game != null && NativeControlAuthority.TryGetForGame(game, out var authority) && authority != null && authority.Status().Active;
        }

        private static void EnterInput() => _inPlayerInput++;
        private static void LeaveInput() => _inPlayerInput = Math.Max(0, _inPlayerInput - 1);

        // False skips the original: the clock is left exactly as it was.
        private static bool BeforeChange()
        {
            if (!Blocking()) return true;
            var now = Time.realtimeSinceStartup;
            if (now - _lastMessage >= MessageGapSeconds)
            {
                _lastMessage = now;
                Messages.Message("The bot is running the clock, so speed keys are off. Press Pause in the RimGovernor launcher to take over.", MessageTypeDefOf.RejectInput, false);
            }
            return false;
        }
    }
}
