#nullable enable

using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>Attribute a pause to the actual synchronous letter callback.
    /// Any subsequent non-letter speed assignment invalidates this attribution.</summary>
    internal static class LetterPauseHook
    {
        private static bool installed;
        private static int depth;
        private static Letter? current;
        private static Game? game;
        private static string? letterId;

        internal static void EnsurePatched()
        {
            if (installed) return;
            var harmony = new Harmony("rimgovernor.letter-pause-source");
            var receive = AccessTools.Method(typeof(LetterStack), "ReceiveLetter",
                new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) });
            var speed = AccessTools.PropertySetter(typeof(TickManager), "CurTimeSpeed");
            if (receive == null || speed == null) throw new InvalidOperationException("Native pause attribution methods unavailable");
            harmony.Patch(receive, prefix: new HarmonyMethod(typeof(LetterPauseHook), nameof(BeforeLetter)),
                finalizer: new HarmonyMethod(typeof(LetterPauseHook), nameof(AfterLetter)));
            harmony.Patch(speed, postfix: new HarmonyMethod(typeof(LetterPauseHook), nameof(AfterSpeed)));
            harmony.Patch(AccessTools.Method(typeof(TickManager), "TogglePaused"), postfix: new HarmonyMethod(typeof(LetterPauseHook), nameof(AfterSpeed)));
            harmony.Patch(AccessTools.Method(typeof(TickManager), "Pause"), postfix: new HarmonyMethod(typeof(LetterPauseHook), nameof(AfterSpeed)));
            installed = true;
        }

        private static void BeforeLetter(Letter let) { depth++; current = let; }
        private static void AfterLetter() { depth = Math.Max(0, depth - 1); if (depth == 0) current = null; }
        private static void AfterSpeed(TickManager __instance)
        {
            game = null; letterId = null;
            if (depth > 0 && current != null && __instance.CurTimeSpeed == TimeSpeed.Paused) {
                game = Current.Game;
                letterId = current.GetUniqueLoadID();
            }
        }

        internal static string? Consume()
        {
            var result = ReferenceEquals(game, Current.Game) ? letterId : null;
            game = null; letterId = null;
            return result;
        }
    }
}
