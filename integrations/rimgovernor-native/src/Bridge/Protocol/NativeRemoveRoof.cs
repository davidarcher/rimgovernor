#nullable enable
using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // RemoveRoofIntent on Actions/Apply (#1366): vanilla remove roof, the
    // NoRoof area (Designator_AreaNoRoof, which also clears BuildRoof), over
    // cells. A cell already unroofed or already in the NoRoof area
    // applies again; a fogged or out-of-bounds cell or a thick roof refuses
    // the whole intent. Applied means designated: pawns remove the roof.
    internal static class NativeRemoveRoof
    {
        private static string? Refusal(Operations.RemoveRoofIntent? intent, Common.ObservationContext context, out Map? map, out List<IntVec3> cells)
        {
            map = null; cells = new List<IntVec3>();
            if (intent == null || intent.Cells.Count == 0) return "Remove roof requires cells.";
            map = ProtoBoundary.ResolveMap(context);
            if (map == null) return "Current map required.";
            var seen = new HashSet<IntVec3>();
            foreach (var c in intent.Cells)
            {
                if (c == null || !c.HasX || !c.HasZ) return "Remove roof cell requires x and z.";
                var cell = new IntVec3(c.X, 0, c.Z);
                if (!cell.InBounds(map)) return $"Cell {cell} is out of bounds.";
                if (cell.Fogged(map)) return $"Cell {cell} is fogged.";
                if (!seen.Add(cell)) return $"Cell {cell} is repeated.";
                var roof = map.roofGrid.RoofAt(cell);
                if (roof != null && roof.isThickRoof) return $"Cell {cell} has a thick roof.";
                cells.Add(cell);
            }
            return null;
        }

        internal static Common.Failure? Validate(Operations.RemoveRoofIntent? intent, Common.ObservationContext context)
        {
            var refusal = Refusal(intent, context, out _, out _);
            return refusal == null ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, refusal);
        }

        internal static Receipts.EffectEvidence Apply(Operations.RemoveRoofIntent intent, Common.ObservationContext context)
        {
            var refusal = Refusal(intent, context, out var map, out var cells);
            if (refusal != null) throw new InvalidOperationException("Remove roof prerequisites changed before apply: " + refusal);
            var effect = new Receipts.RemoveRoofEffect { Designated = 0, Adopted = 0, Unroofed = 0 };
            var noRoof = map!.areaManager.NoRoof;
            var designator = new Designator_AreaNoRoof();
            foreach (var cell in cells)
            {
                if (map.roofGrid.RoofAt(cell) == null) { effect.Unroofed++; continue; }
                if (noRoof[cell]) { effect.Adopted++; continue; }
                var accepted = designator.CanDesignateCell(cell);
                if (!accepted.Accepted) throw new InvalidOperationException($"Native remove-roof (NoRoof area) is not accepted at {cell}: {accepted.Reason}");
                designator.DesignateSingleCell(cell);
                if (!noRoof[cell]) throw new InvalidOperationException($"NoRoof area was not observed at {cell}.");
                effect.Designated++;
            }
            return new Receipts.EffectEvidence { RemoveRoof = effect };
        }
    }

    internal sealed class RemoveRoofActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeRemoveRoof.Validate(action.RemoveRoof, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeRemoveRoof.Apply(action.RemoveRoof, context);
    }
}
