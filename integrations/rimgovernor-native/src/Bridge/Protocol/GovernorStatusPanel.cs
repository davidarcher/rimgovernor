#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    public enum StatusStripSeverity { Info, Warning, Critical }

    public readonly struct StatusStripRow
    {
        public readonly string Key;
        public readonly string Text;
        public readonly StatusStripSeverity Severity;
        public readonly IntVec3? Target;
        public readonly bool Detail;

        public StatusStripRow(string key, string text, StatusStripSeverity severity, IntVec3? target, bool detail)
        {
            Key = key; Text = text; Severity = severity; Target = target; Detail = detail;
        }
    }

    /// <summary>
    /// The controller's in-game status panel (#951, replacing the #823 top
    /// strip): a small box in the top-right corner, which vanilla leaves
    /// empty. The header names the control mode and toggles the body; the
    /// body is the native control and clock lines, the controller's headline
    /// rows, then its per-goal rows. Text wraps; a body taller than the free
    /// column scrolls. Session-only static state bound to one map; starts
    /// expanded. A future button row (#957) goes between header and body.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class GovernorStatusPanel
    {
        private const float Width = 244f;
        private const float Margin = 8f;
        private const float Pad = 6f;
        private const float HeaderHeight = 24f;
        private const float RowGap = 2f;

        // Vanilla's top-right neighbours (Assembly-CSharp 1.6): the colonist
        // bar is at most UI.screenWidth - 520 wide and centred, so it leaves
        // 260 px each side (ColonistBarDrawLocsFinder.MaxColonistBarWidth);
        // the learning helper is 200 px at (screenWidth - 208, 8)
        // (LearningReadout.LearningReadoutOnGUI); the dev-mode buttons are a
        // centred row 25 px tall at y 3 (DebugWindowsOpener); letters and
        // alerts stack up the right edge from LetterStack.LastTopY. UI.screen*
        // are already divided by Prefs.UIScale.
        private static readonly AccessTools.FieldRef<LearningReadout, Rect> LearningRect = AccessTools.FieldRefAccess<LearningReadout, Rect>("windowRect");
        private static readonly AccessTools.FieldRef<DebugWindowsOpener, float> DevRowWidth = AccessTools.FieldRefAccess<DebugWindowsOpener, float>("widgetRowFinalX");

        private static List<StatusStripRow> rows = new List<StatusStripRow>();
        private static Map? map;
        private static bool expanded = true;
        private static Vector2 scroll;

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
                Log.Error("[RimGovernor] Status panel installation failed: " + ex);
            }
        }

        public static int Count => rows.Count;

        // Call only on the game thread. Every call replaces every row.
        public static void Replace(Map target, List<StatusStripRow> next)
        {
            map = target;
            rows = next;
        }

        public static void Clear()
        {
            rows = new List<StatusStripRow>();
            map = null;
        }

        private static Color Tint(StatusStripSeverity severity) => severity switch
        {
            StatusStripSeverity.Critical => new Color(1f, 0.35f, 0.3f),
            StatusStripSeverity.Warning => new Color(1f, 0.85f, 0.3f),
            _ => new Color(0.85f, 0.85f, 0.85f),
        };

        private readonly struct Line
        {
            public readonly string Text;
            public readonly StatusStripSeverity Severity;
            public readonly IntVec3? Target;
            public readonly bool Heading;
            public Line(string text, StatusStripSeverity severity, IntVec3? target = null, bool heading = false)
            { Text = text; Severity = severity; Target = target; Heading = heading; }
        }

        private static void Draw()
        {
            if (map == null || Find.CurrentMap != map || WorldRendererUtility.WorldSelected || TutorSystem.TutorialMode) return;
            if (Event.current.type == EventType.Layout) return;
            var font = Text.Font;
            try
            {
                Text.Font = GameFont.Small;
                Text.WordWrap = true;
                var (word, control, controlSeverity) = Control();
                var x = UI.screenWidth - Margin - Width;
                var top = Top(x);
                var bottom = Bottom();
                if (bottom - top < HeaderHeight) return;

                var lines = expanded ? Body(control, controlSeverity) : new List<Line>();
                var inner = Width - 2 * Pad;
                var heights = lines.Select(l => Text.CalcHeight(l.Text, inner)).ToList();
                var bodyHeight = lines.Count == 0 ? 0f : heights.Sum() + RowGap * (lines.Count - 1) + Pad;
                var panel = new Rect(x, top, Width, Mathf.Min(HeaderHeight + bodyHeight, bottom - top));
                Widgets.DrawBoxSolid(panel, new Color(0f, 0f, 0f, 0.6f));

                var header = new Rect(panel.x + Pad, panel.y, panel.width - 2 * Pad, HeaderHeight);
                GUI.color = Tint(controlSeverity);
                Widgets.Label(new Rect(header.x, header.y + 2f, header.width, header.height), (expanded ? "[-] " : "[+] ") + "RimGovernor: " + word);
                GUI.color = Color.white;
                Widgets.DrawHighlightIfMouseover(header);
                TooltipHandler.TipRegion(header, expanded ? "Collapse the RimGovernor status." : control + "\n\nClick to show the RimGovernor status.");
                if (Widgets.ButtonInvisible(header)) expanded = !expanded;
                if (lines.Count == 0) return;

                var outer = new Rect(panel.x, header.yMax, panel.width, panel.height - HeaderHeight);
                var scrolls = HeaderHeight + bodyHeight > bottom - top;
                var width = scrolls ? inner - 16f : inner;
                if (scrolls) heights = lines.Select(l => Text.CalcHeight(l.Text, width)).ToList();
                var view = new Rect(0f, 0f, width + Pad, heights.Sum() + RowGap * (lines.Count - 1) + Pad);
                Widgets.BeginScrollView(outer, ref scroll, view, showScrollbars: scrolls);
                var y = 0f;
                for (var i = 0; i < lines.Count; i++)
                {
                    DrawLine(lines[i], new Rect(Pad, y, width, heights[i]));
                    y += heights[i] + RowGap;
                }
                Widgets.EndScrollView();
            }
            finally
            {
                GUI.color = Color.white;
                Text.Font = font;
            }
        }

        // The body, top to bottom: control, clock, the controller's headline
        // rows (goal, pause, stocks, emergency, refusal) and its per-goal rows.
        private static List<Line> Body(string control, StatusStripSeverity controlSeverity)
        {
            var lines = new List<Line> { new Line(control, controlSeverity), new Line(ClockLine(), StatusStripSeverity.Info) };
            foreach (var row in rows.Where(r => !r.Detail)) lines.Add(new Line(Capitalize(row.Text), row.Severity, row.Target));
            var goals = rows.Where(r => r.Detail).ToList();
            if (goals.Count > 0)
            {
                lines.Add(new Line("Goals", StatusStripSeverity.Info, heading: true));
                foreach (var row in goals) lines.Add(new Line("  " + row.Text, row.Severity, row.Target));
            }
            return lines;
        }

        private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        // Below the learning helper and the dev-mode row when they share the
        // right-hand column.
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

        // Above the letters and the alerts stacked up the right edge.
        private static float Bottom()
        {
            var bottom = Find.LetterStack?.LastTopY ?? UI.screenHeight;
            if (Find.UIRoot is UIRoot_Play play && play.alerts != null) bottom -= play.alerts.AlertsHeight;
            return bottom - Margin;
        }

        private static void DrawLine(Line line, Rect rect)
        {
            GUI.color = line.Heading ? Color.gray : Tint(line.Severity);
            Widgets.Label(rect, line.Text);
            GUI.color = Color.white;
            if (line.Target is IntVec3 cell && map != null)
            {
                TooltipHandler.TipRegion(rect, "Click to jump there.");
                Widgets.DrawHighlightIfMouseover(rect);
                if (Widgets.ButtonInvisible(rect)) CameraJumper.TryJump(new GlobalTargetInfo(cell, map));
            }
        }

        private static NativeControlSnapshot? Authority()
        {
            var game = Current.Game;
            return game != null && NativeControlAuthority.TryGetForGame(game, out var authority) && authority != null ? authority.Status() : null;
        }

        // Auto, Manual, or held and why (the authority's revocation reason
        // and what the hook saw).
        private static (string Word, string Line, StatusStripSeverity Severity) Control()
        {
            var s = Authority();
            if (s == null) return ("no controller", "Control: no controller has connected", StatusStripSeverity.Info);
            if (s.Active && s.Available) return ("Auto", "Control: Auto, the governor plays", StatusStripSeverity.Info);
            if (s.Active) return ("held", "Control: held, native authority unavailable", StatusStripSeverity.Warning);
            var why = s.Reason switch
            {
                NativeControlRevocationReason.Manual => "",
                NativeControlRevocationReason.ExternalOrder => "a player order",
                NativeControlRevocationReason.PlayerControl => "the player took control",
                NativeControlRevocationReason.IdentityChanged => "the game or map changed",
                NativeControlRevocationReason.Disconnect => "the controller disconnected",
                NativeControlRevocationReason.Shutdown => "the game is shutting down",
                NativeControlRevocationReason.HooksUnavailable => "native hooks are not ready",
                NativeControlRevocationReason.GenerationExhausted => "authority generations exhausted",
                NativeControlRevocationReason.ClockUnavailable => "the clock is unavailable",
                _ => s.Reason.ToString(),
            };
            if (why.Length == 0) return ("Manual", "Control: Manual, the player plays", StatusStripSeverity.Info);
            if (!string.IsNullOrEmpty(s.Detail)) why += " (" + s.Detail + ")";
            return ("held", "Control: held, " + why, StatusStripSeverity.Warning);
        }

        private static string ClockLine() => "Clock: " + Supervisor.PanelWindowState() + ", game " + (Find.TickManager.Paused ? "paused" : Find.TickManager.CurTimeSpeed.ToString());
    }

    internal static partial class Supervisor
    {
        // The governor's clock window for the status panel (#951): running
        // to its tick deadline, stopping, or stopped and why.
        internal static string PanelWindowState()
        {
            lock (Gate)
            {
                var s = _state;
                if (s == null) return "no window yet";
                if (s.Active && s.PendingKind != null) return "window stopping (" + s.PendingKind + ")";
                if (s.Active)
                {
                    var left = s.TickDeadline.HasValue ? $", {Math.Max(0L, s.TickDeadline.Value - Find.TickManager.TicksGame)} ticks left" : "";
                    return "window running at " + s.RequestedSpeed + left;
                }
                return "window stopped (" + (s.StopReason ?? "unknown") + ")";
            }
        }
    }
}
