using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // BuildRoofIntent on Actions/Apply: vanilla build roof, the BuildRoof
    // area (Designator_AreaBuildRoof, which also clears NoRoof), over cells.
    // The mirror of NativeRemoveRoof: a cell already in the BuildRoof area
    // applies again, a fogged or out-of-bounds cell or a thick roof refuses
    // the whole intent. Applied means designated: pawns build the roof on
    // the cells that are unroofed and within reach of a roof support.
    internal static class NativeBuildRoof
    {
        private static string? Refusal(RimGovernor.Brain.GameOrders.Operations.BuildRoofIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Map? map, out List<IntVec3> cells)
        {
            map = null; cells = new List<IntVec3>();
            if (intent == null || intent.Cells.Count == 0) return "Build roof requires cells.";
            map = GameThread.ResolveMap(context);
            if (map == null) return "Current map required.";
            var seen = new HashSet<IntVec3>();
            foreach (var c in intent.Cells)
            {
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

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.BuildRoofIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var refusal = Refusal(intent, context, out _, out _);
            return refusal == null ? null : GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, refusal);
        }

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.BuildRoofIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var refusal = Refusal(intent, context, out var map, out var cells);
            if (refusal != null) throw new InvalidOperationException("Build roof prerequisites changed before apply: " + refusal);
            var roofedCount = 0;
            var buildRoof = (map ?? throw new System.InvalidOperationException("Required native value is unavailable: map.")).areaManager.BuildRoof;
            var designator = new Designator_AreaBuildRoof();
            foreach (var cell in cells)
            {
                var roofed = map.roofGrid.RoofAt(cell) != null;
                if (buildRoof[cell]) { if (roofed) roofedCount++; continue; }
                var accepted = designator.CanDesignateCell(cell);
                if (!accepted.Accepted) throw new InvalidOperationException($"Native build-roof (BuildRoof area) is not accepted at {cell}: {accepted.Reason}");
                designator.DesignateSingleCell(cell);
                if (!buildRoof[cell]) throw new InvalidOperationException($"BuildRoof area was not observed at {cell}.");
                if (roofed) roofedCount++;
            }
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect.BuildRoofCase() };
        }
    }

    internal sealed class BuildRoofActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeBuildRoof.Validate(action.Intent?.BuildRoof, context);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeBuildRoof.Apply(action.Intent?.BuildRoof, context);
    }
}
