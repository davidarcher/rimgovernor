using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Text labels over the colony layout overlay (#726): role names at room
    /// and module centres, so the plan reads without telling sixteen colors
    /// apart. Output only; the presentation_layout_plan tool rewrites them
    /// with the plans and nothing reads them back.
    /// </summary>
    public sealed class LayoutPlanLabels : MapComponent
    {
        public List<string> Texts = new List<string>();
        public List<IntVec3> Cells = new List<IntVec3>();
        public LayoutPlanLabels(Map map) : base(map) { }

        public void Replace(List<string> texts, List<IntVec3> cells)
        {
            Texts = texts;
            Cells = cells;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Texts, "rimgovernorLayoutLabelTexts", LookMode.Value);
            Scribe_Collections.Look(ref Cells, "rimgovernorLayoutLabelCells", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Texts ??= new List<string>();
                Cells ??= new List<IntVec3>();
                if (Texts.Count != Cells.Count) { Texts.Clear(); Cells.Clear(); }
            }
        }

        public override void MapComponentOnGUI()
        {
            if (Texts.Count == 0 || Find.CurrentMap != map || WorldRendererUtility.WorldSelected) return;
            var view = Find.CameraDriver.CurrentViewRect.ExpandedBy(2);
            for (var i = 0; i < Texts.Count; i++)
            {
                if (!view.Contains(Cells[i])) continue;
                GenMapUI.DrawThingLabel(GenMapUI.LabelDrawPosFor(Cells[i]), Texts[i], Color.white);
            }
        }
    }
}
