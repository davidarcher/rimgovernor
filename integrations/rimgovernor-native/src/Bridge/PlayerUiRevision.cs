#nullable enable

using HarmonyLib;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Counts local player mouse and key input so a pending guarded wall
    // removal can tell that the player acted after it was admitted.
    internal static class PlayerUiRevision
    {
        static long uiRevision;
        internal static long Current => uiRevision;
        static bool observingUi;

        internal static void Observe()
        {
            if (observingUi) return;
            new Harmony("rimgovernor.player-frame-ui").Patch(AccessTools.Method(typeof(Root), "OnGUI"),
                prefix: new HarmonyMethod(typeof(PlayerUiRevision), nameof(BeforeGui)));
            observingUi = true;
        }

        static void BeforeGui()
        {
            var current = Event.current;
            if (current != null && (current.type == EventType.MouseDown || current.type == EventType.MouseUp
                || current.type == EventType.ScrollWheel || (current.type == EventType.KeyDown
                    && current.keyCode != KeyCode.LeftShift && current.keyCode != KeyCode.RightShift
                    && current.keyCode != KeyCode.LeftControl && current.keyCode != KeyCode.RightControl
                    && current.keyCode != KeyCode.LeftAlt && current.keyCode != KeyCode.RightAlt))) uiRevision++;
        }
    }
}
