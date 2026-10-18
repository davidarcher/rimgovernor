#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Presentation = RimGovernor.Contracts.Wire.Presentation;

namespace HomeBridge.BridgeTools
{
    // The controller's map overlay: one named layer per call, drawn by
    // GovernorOverlay. Output only; nothing reads it back.
    internal static class ProtoOverlayTools
    {
        // Input bounds on a draw request; kept (docs/developers/contracts/kept-constants.md).
        private const int MaxCells = 250000;
        private const int MaxLabels = 4096;
        private const int MaxLines = 4096;
        private const int MaxLayerId = 64;

        // Call only on the game thread.
        internal static Presentation.OverlayReply Apply(Presentation.OverlayRequest request)
        {
            if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out _, out var invalid))
                return new Presentation.OverlayReply { Outcome = invalid };
            var id = request.LayerId is not null ? (request.LayerId ?? "") : "";
            if (id.Length == 0 || id.Length > MaxLayerId || id.Any(ch => ch < 0x21 || ch > 0x7e))
                return Invalid($"layer_id must be 1-{MaxLayerId} printable ASCII characters.");
            var overlay = map.GetComponent<GovernorOverlay>();
            if (overlay == null)
                return new Presentation.OverlayReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Map has no overlay component.") };
            if (!request.Enabled.GetValueOrDefault())
            {
                overlay.Remove(id);
                return Applied();
            }
            if (request.Labels.Count > MaxLabels) return Invalid($"More than {MaxLabels} labels.");

            var groups = new List<OverlayGroup>();
            long budget = MaxCells;
            var lineBudget = MaxLines;
            var bounds = CellRect.WholeMap(map);
            foreach (var shape in request.Shapes)
            {
                if (shape.Style == Presentation.OverlayStyle.Unspecified) return Invalid("Overlay shape needs a style.");
                var c = shape.Color;
                if (c == null) return Invalid("Overlay shape needs a color.");
                var group = new OverlayGroup
                {
                    Color = new Color(c.R.GetValueOrDefault(), c.G.GetValueOrDefault(), c.B.GetValueOrDefault(), c.A is not null ? c.A.GetValueOrDefault() : 1f),
                    Outline = shape.Style == Presentation.OverlayStyle.Outline,
                };
                var rects = new List<CellRect>();
                foreach (var r in shape.Rects)
                {
                    if (r.MaxX.GetValueOrDefault() < r.MinX.GetValueOrDefault() || r.MaxZ.GetValueOrDefault() < r.MinZ.GetValueOrDefault()) return Invalid("Inverted overlay rectangle.");
                    rects.Add(CellRect.FromLimits(r.MinX.GetValueOrDefault(), r.MinZ.GetValueOrDefault(), r.MaxX.GetValueOrDefault(), r.MaxZ.GetValueOrDefault()));
                }
                foreach (var run in shape.Runs)
                {
                    if (run.Length.GetValueOrDefault() <= 0) return Invalid("Overlay run needs a positive length.");
                    rects.Add(new CellRect(run.X.GetValueOrDefault(), run.Z.GetValueOrDefault(), run.Length.GetValueOrDefault(), 1));
                }
                foreach (var line in shape.Lines)
                {
                    if (line.From == null || line.To == null) return Invalid("Overlay line needs both ends.");
                    if (--lineBudget < 0) return Invalid($"More than {MaxLines} overlay lines.");
                    group.Lines.Add((new Vector2(line.From.X + 0.5f, line.From.Z + 0.5f), new Vector2(line.To.X + 0.5f, line.To.Z + 0.5f), line.Arrow.GetValueOrDefault()));
                }
                foreach (var rect in rects)
                {
                    if ((budget -= (long)rect.Width * rect.Height) < 0) return Invalid($"Overlay layer exceeds {MaxCells} cells.");
                    var clipped = rect.ClipInsideRect(bounds);
                    if (clipped.Width <= 0 || clipped.Height <= 0) continue;
                    group.Fills.Add(clipped);
                    if (group.Outline)
                    {
                        foreach (var cell in clipped) group.Cells.Add(cell);
                    }
                }
                if (group.Outline) group.Fills.Clear();
                groups.Add(group);
            }
            var labels = new List<(string, IntVec3)>();
            foreach (var label in request.Labels)
            {
                if (label.Text is null || label.Cell == null) continue;
                var cell = new IntVec3(label.Cell.X, 0, label.Cell.Z);
                if (cell.InBounds(map)) labels.Add(((label.Text ?? ""), cell));
            }
            overlay.Replace(id, groups, labels);
            return Applied();
        }

        private static Presentation.OverlayReply Applied() =>
            new Presentation.OverlayReply { Outcome = new Presentation.OverlayReplyOutcome.AppliedCase() };

        private static Presentation.OverlayReply Invalid(string detail) =>
            new Presentation.OverlayReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, detail) };
    }
}
