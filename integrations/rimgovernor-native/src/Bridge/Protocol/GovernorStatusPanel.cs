#nullable enable
using RimGovernor.Host.Sdk;
using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The in-game connection line: one label in the top-right corner,
    /// below vanilla's learning helper and dev-mode row when they share it.
    /// "Bot connected" means the bot holds native authority (Active and
    /// Available, or Manual for any reason but a disconnect), not that a
    /// transport is open; no authority, or a Disconnect revocation, reads
    /// "Waiting for the bot". The launcher is the control surface.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class GovernorStatusPanel
    {
        private const float Margin = 8f;
        private const float Pad = 6f;
        private const float Height = 24f;

        // Vanilla's top-right neighbours (Assembly-CSharp 1.6): the learning
        // helper is 200 px at (screenWidth - 208, 8) (LearningReadout); the
        // dev-mode buttons are a centred row 25 px tall at y 3
        // (DebugWindowsOpener). UI.screen* are already divided by Prefs.UIScale.
        private static readonly AccessTools.FieldRef<LearningReadout, Rect> LearningRect = AccessTools.FieldRefAccess<LearningReadout, Rect>("windowRect");
        private static readonly AccessTools.FieldRef<DebugWindowsOpener, float> DevRowWidth = AccessTools.FieldRefAccess<DebugWindowsOpener, float>("widgetRowFinalX");

        static GovernorStatusPanel()
        {
            if (Application.isBatchMode) return;
            try
            {
                new Harmony("rimgovernor.status.panel").Patch(
                    AccessTools.Method(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs))
                        ?? throw new MissingMethodException("MapInterface.MapInterfaceOnGUI_AfterMainTabs"),
                    postfix: new HarmonyMethod(typeof(GovernorStatusPanel), nameof(Draw)));
            }
            catch (Exception ex)
            {
                ModLog.Error("startup", "Status panel installation failed: " + ex);
            }
        }

        private static void Draw()
        {
            if (Find.CurrentMap == null || WorldRendererUtility.WorldSelected || TutorSystem.TutorialMode) return;
            if (Event.current.type == EventType.Layout) return;
            var font = Text.Font;
            try
            {
                Text.Font = GameFont.Small;
                var label = Connected() ? "Bot connected" : "Waiting for the bot";
                var width = Text.CalcSize(label).x + 2 * Pad;
                var x = UI.screenWidth - Margin - width;
                var panel = new Rect(x, Top(x), width, Height);
                Widgets.DrawBoxSolid(panel, new Color(0f, 0f, 0f, 0.6f));
                Widgets.Label(new Rect(panel.x + Pad, panel.y + 2f, panel.width - Pad, panel.height), label);
            }
            finally
            {
                GUI.color = Color.white;
                Text.Font = font;
            }
        }

        private static bool Connected()
        {
            var game = Current.Game;
            if (game == null || !NativeControlAuthority.TryGetForGame(game, out var authority) || authority == null) return false;
            return authority.Status().Reason != NativeControlRevocationReason.Disconnect;
        }

        private static float Top(float x)
        {
            var top = Margin;
            var tutor = Find.Tutor?.learningReadout;
            if (tutor != null && TutorSystem.AdaptiveTrainingEnabled && (Find.PlaySettings.showLearningHelper || tutor.ActiveConceptsCount != 0))
                top = Mathf.Max(top, LearningRect(tutor).yMax + Margin);
            var dev = Find.UIRoot?.debugWindowOpener;
            if (Prefs.DevMode && dev != null && UI.screenWidth * 0.5f + DevRowWidth(dev) * 0.5f > x - Margin)
                top = Mathf.Max(top, 3f + 25f + Margin);
            return top;
        }
    }
}
