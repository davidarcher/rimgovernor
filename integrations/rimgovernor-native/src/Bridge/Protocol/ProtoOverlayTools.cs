#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using UnityEngine;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Presentation = RimGovernor.Protocol.Presentation;

namespace HomeBridge.BridgeTools
{
    // The controller's map overlay (#817): one named layer per call, drawn by
    // GovernorOverlay. Output only; nothing reads it back.
    public sealed class ProtoOverlayTools
    {
        private const string ToolName = "rimgovernor/presentation_overlay";
        private const int MaxCells = 250000;
        private const int MaxLabels = 4096;
        private const int MaxLayerId = 64;

        [Tool(ToolName, Title = "Draw a map overlay layer",
            Description = "Replace one named overlay layer with the given shapes and labels. enabled=false removes it.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.presentation.v1.OverlayReply.", Always = true)]
        public async Task<object> Draw(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON OverlayRequest string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Presentation.OverlayRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Presentation.OverlayReply { Failure = failure });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => Apply(parsed), cancellationToken).ConfigureAwait(false);
        }

        // Call only on the game thread.
        internal static Presentation.OverlayReply Apply(Presentation.OverlayRequest request)
        {
            if (!ProtoBoundary.ValidateIdentity(request.Identity, out var map, out var context, out var invalid))
                return new Presentation.OverlayReply { Failure = invalid };
            var id = request.HasLayerId ? request.LayerId : "";
            if (id.Length == 0 || id.Length > MaxLayerId || id.Any(ch => ch < 0x21 || ch > 0x7e))
                return Invalid($"layer_id must be 1-{MaxLayerId} printable ASCII characters.");
            var overlay = map.GetComponent<GovernorOverlay>();
            if (overlay == null)
                return new Presentation.OverlayReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Map has no overlay component.") };
            if (!request.Enabled)
            {
                overlay.Remove(id);
                return Applied(context, 0, overlay);
            }
            if (request.Labels.Count > MaxLabels) return Invalid($"More than {MaxLabels} labels.");

            var groups = new List<OverlayGroup>();
            var covered = new HashSet<IntVec3>();
            long budget = MaxCells;
            var bounds = CellRect.WholeMap(map);
            foreach (var shape in request.Shapes)
            {
                if (shape.Style == Presentation.OverlayStyle.Unspecified) return Invalid("Overlay shape needs a style.");
                var c = shape.Color;
                if (c == null) return Invalid("Overlay shape needs a color.");
                var group = new OverlayGroup
                {
                    Color = new Color(c.R, c.G, c.B, c.HasA ? c.A : 1f),
                    Outline = shape.Style == Presentation.OverlayStyle.Outline,
                };
                var rects = new List<CellRect>();
                foreach (var r in shape.Rects)
                {
                    if (r.MaxX < r.MinX || r.MaxZ < r.MinZ) return Invalid("Inverted overlay rectangle.");
                    rects.Add(CellRect.FromLimits(r.MinX, r.MinZ, r.MaxX, r.MaxZ));
                }
                foreach (var run in shape.Runs)
                {
                    if (run.Length <= 0) return Invalid("Overlay run needs a positive length.");
                    rects.Add(new CellRect(run.X, run.Z, run.Length, 1));
                }
                foreach (var rect in rects)
                {
                    if ((budget -= (long)rect.Width * rect.Height) < 0) return Invalid($"Overlay layer exceeds {MaxCells} cells.");
                    var clipped = rect.ClipInsideRect(bounds);
                    if (clipped.Width <= 0 || clipped.Height <= 0) continue;
                    group.Fills.Add(clipped);
                    foreach (var cell in clipped)
                    {
                        covered.Add(cell);
                        if (group.Outline) group.Cells.Add(cell);
                    }
                }
                if (group.Outline) group.Fills.Clear();
                groups.Add(group);
            }
            var labels = new List<(string, IntVec3)>();
            foreach (var label in request.Labels)
            {
                if (!label.HasText || label.Cell == null) continue;
                var cell = new IntVec3(label.Cell.X, 0, label.Cell.Z);
                if (cell.InBounds(map)) labels.Add((label.Text, cell));
            }
            overlay.Replace(id, groups, labels);
            return Applied(context, (uint)covered.Count, overlay);
        }

        private static Presentation.OverlayReply Applied(Common.ObservationContext context, uint cells, GovernorOverlay overlay) =>
            new Presentation.OverlayReply { Applied = new Presentation.OverlayApplied { Context = context, Cells = cells, Layers = (uint)overlay.Count } };

        private static Presentation.OverlayReply Invalid(string detail) =>
            new Presentation.OverlayReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, detail) };
    }
}
