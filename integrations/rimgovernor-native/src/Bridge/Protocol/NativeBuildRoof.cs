#nullable enable
using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    // BuildRoofIntent on Actions/Apply: vanilla build roof, the BuildRoof
    // area (Designator_AreaBuildRoof, which also clears NoRoof), over cells.
    // The mirror of NativeRemoveRoof: a cell already in the BuildRoof area
    // applies again, a fogged or out-of-bounds cell or a thick roof refuses
    // the whole intent. Applied means designated: pawns build the roof on
    // the cells that are unroofed and within reach of a roof support.
    internal static class NativeBuildRoof
    {
        private static string? Refusal(Operations.BuildRoofIntent? intent, Common.ObservationContext context, out Map? map, out List<IntVec3> cells)
        {
            map = null; cells = new List<IntVec3>();
            if (intent == null || intent.Cells.Count == 0) return "Build roof requires cells.";
            map = ProtoBoundary.ResolveMap(context);
            if (map == null) return "Current map required.";
            var seen = new HashSet<IntVec3>();
            foreach (var c in intent.Cells)
            {
                if (c == null || c.X is null || c.Z is null) return "Build roof cell requires x and z.";
                var cell = new IntVec3(c.X.GetValueOrDefault(), 0, c.Z.GetValueOrDefault());
                if (!cell.InBounds(map)) return $"Cell {cell} is out of bounds.";
                if (cell.Fogged(map)) return $"Cell {cell} is fogged.";
                if (!seen.Add(cell)) return $"Cell {cell} is repeated.";
                var roof = map.roofGrid.RoofAt(cell);
                if (roof != null && roof.isThickRoof) return $"Cell {cell} has a thick roof.";
                cells.Add(cell);
            }
            return null;
        }

        internal static Common.Failure? Validate(Operations.BuildRoofIntent? intent, Common.ObservationContext context)
        {
            var refusal = Refusal(intent, context, out _, out _);
            return refusal == null ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, refusal);
        }

        internal static Receipts.EffectEvidence Apply(Operations.BuildRoofIntent? intent, Common.ObservationContext context)
        {
            var refusal = Refusal(intent, context, out var map, out var cells);
            if (refusal != null) throw new InvalidOperationException("Build roof prerequisites changed before apply: " + refusal);
            int designatedCount = 0, adoptedCount = 0, roofedCount = 0;
            var buildRoof = map!.areaManager.BuildRoof;
            var designator = new Designator_AreaBuildRoof();
            foreach (var cell in cells)
            {
                var roofed = map.roofGrid.RoofAt(cell) != null;
                if (buildRoof[cell]) { if (roofed) roofedCount++; else adoptedCount++; continue; }
                var accepted = designator.CanDesignateCell(cell);
                if (!accepted.Accepted) throw new InvalidOperationException($"Native build-roof (BuildRoof area) is not accepted at {cell}: {accepted.Reason}");
                designator.DesignateSingleCell(cell);
                if (!buildRoof[cell]) throw new InvalidOperationException($"BuildRoof area was not observed at {cell}.");
                if (roofed) roofedCount++; else designatedCount++;
            }
            return new Receipts.EffectEvidence { Effect = new Receipts.BuildRoofEffect { Designated = designatedCount, Adopted = adoptedCount, Roofed = roofedCount } };
        }
    }

    internal sealed class BuildRoofActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeBuildRoof.Validate(action.Intent?.BuildRoof, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeBuildRoof.Apply(action.Intent?.BuildRoof, context);
    }
}
