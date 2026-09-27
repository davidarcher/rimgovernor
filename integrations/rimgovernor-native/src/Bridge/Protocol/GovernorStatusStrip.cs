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
    /// The controller's in-game status strip (#823): headline rows inline at
    /// the top centre of the map view, detail rows in a toggled dropdown.
    /// Session-only static state bound to one map; shown whenever it has rows.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class GovernorStatusStrip
    {
        private const float RowHeight = 22f;
        private const float MaxWidth = 720f;
        private const float MaxInlineRow = 220f;
        private const float Gap = 14f;

        private static List<StatusStripRow> rows = new List<StatusStripRow>();
        private static Map? map;
        private static bool expanded;

        static GovernorStatusStrip()
        {
            if (Application.isBatchMode) return;
            try
            {
                new Harmony("rimgovernor.status.strip").Patch(
                    AccessTools.Method(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs))
                        ?? throw new MissingMethodException("MapInterface.MapInterfaceOnGUI_AfterMainTabs"),
                    postfix: new HarmonyMethod(typeof(GovernorStatusStrip), nameof(Draw)));
            }
            catch (Exception ex)
            {
                Log.Error("[RimGovernor] Status strip installation failed: " + ex);
            }
        }

        public static int Count => rows.Count;

        // Call only on the game thread. Every call replaces the whole strip.
        public static void Replace(Map target, List<StatusStripRow> next)
        {
            map = target;
            rows = next;
            if (!rows.Any(r => r.Detail)) expanded = false;
        }

        public static void Clear()
        {
            rows = new List<StatusStripRow>();
            map = null;
            expanded = false;
        }

        private static Color Tint(StatusStripSeverity severity) => severity switch
        {
            StatusStripSeverity.Critical => new Color(1f, 0.35f, 0.3f),
            StatusStripSeverity.Warning => new Color(1f, 0.85f, 0.3f),
            _ => new Color(0.85f, 0.85f, 0.85f),
        };

        private static void Draw()
        {
            if (rows.Count == 0 || map == null || Find.CurrentMap != map || WorldRendererUtility.WorldSelected) return;
            if (Event.current.type == EventType.Layout) return;
            var inline = rows.Where(r => !r.Detail).ToList();
            var detail = rows.Where(r => r.Detail).ToList();

            var font = Text.Font;
            try
            {
                Text.Font = GameFont.Small;
                var widths = inline.Select(r => Mathf.Min(Text.CalcSize(r.Text).x, MaxInlineRow)).ToList();
                var toggle = detail.Count > 0 ? RowHeight + Gap : 0f;
                var width = Mathf.Min(MaxWidth, widths.Sum() + Gap * Mathf.Max(0, widths.Count - 1) + toggle + 16f);
                // Top centre stays clear of the vanilla top-right readouts and the
                // left-hand alerts; width is capped and rows truncate.
                var strip = new Rect((UI.screenWidth - width) / 2f, 4f, width, RowHeight + 6f);
                Widgets.DrawBoxSolid(strip, new Color(0f, 0f, 0f, 0.55f));
                var x = strip.x + 8f;
                var right = strip.xMax - 8f - toggle;
                for (var i = 0; i < inline.Count && x < right; i++)
                {
                    var cell = new Rect(x, strip.y + 4f, Mathf.Min(widths[i], right - x), RowHeight);
                    DrawRow(inline[i], cell);
                    x = cell.xMax + Gap;
                }
                if (detail.Count > 0)
                {
                    var button = new Rect(strip.xMax - 8f - RowHeight, strip.y + 3f, RowHeight, RowHeight);
                    Widgets.Label(button, expanded ? " -" : " +");
                    Widgets.DrawHighlightIfMouseover(button);
                    if (Widgets.ButtonInvisible(button))
                        expanded = !expanded;
                    TooltipHandler.TipRegion(button, expanded ? "Hide details." : $"Show {detail.Count} detail rows.");
                    if (expanded)
                    {
                        var panel = new Rect(strip.x, strip.yMax + 2f, strip.width, detail.Count * RowHeight + 6f);
                        Widgets.DrawBoxSolid(panel, new Color(0f, 0f, 0f, 0.55f));
                        for (var i = 0; i < detail.Count; i++)
                            DrawRow(detail[i], new Rect(panel.x + 8f, panel.y + 3f + i * RowHeight, panel.width - 16f, RowHeight));
                    }
                }
            }
            finally
            {
                GUI.color = Color.white;
                Text.Font = font;
            }
        }

        private static void DrawRow(StatusStripRow row, Rect rect)
        {
            GUI.color = Tint(row.Severity);
            Widgets.Label(rect, row.Text.Truncate(rect.width));
            GUI.color = Color.white;
            TooltipHandler.TipRegion(rect, row.Target.HasValue ? row.Text + "\n\nClick to jump there." : row.Text);
            if (row.Target is IntVec3 cell && map != null)
            {
                Widgets.DrawHighlightIfMouseover(rect);
                if (Widgets.ButtonInvisible(rect)) CameraJumper.TryJump(new GlobalTargetInfo(cell, map));
            }
        }
    }
}
