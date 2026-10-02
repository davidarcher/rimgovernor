#nullable enable
using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // RemoveRoofIntent on Actions/Apply (#1366): vanilla remove roof, the
    // NoRoof area (Designator_AreaNoRoof, which also clears BuildRoof), over
    // cells, unroofed ones included so nothing roofs them afterwards. A
    // cell already in the NoRoof area applies again; a fogged or out-of-bounds cell or a thick roof refuses
    // the whole intent. Applied means designated: pawns remove the roof.
    internal static class NativeRemoveRoof
    {
        private static bool installed;
        // Vanilla auto-roof (AutoBuildRoofAreaSetter) re-adds an enclosed
        // room's cells to the BuildRoof area without clearing NoRoof, so
        // pawns would rebuild the roof they are removing. The NoRoof
        // designator makes the two areas exclusive; this holds that rule
        // for roof building.
        private static void Install()
        {
            if (installed) return;
            new Harmony("rimgovernor.remove-roof").Patch(AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.HasJobOnCell)),
                postfix: new HarmonyMethod(typeof(NativeRemoveRoof), nameof(NoRoofHoldsBuild)));
            installed = true;
        }
        private static void NoRoofHoldsBuild(Pawn pawn, IntVec3 c, ref bool __result)
        { if (__result && pawn.Map != null && pawn.Map.areaManager.NoRoof[c]) __result = false; }

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
            Install();
            var refusal = Refusal(intent, context, out var map, out var cells);
            if (refusal != null) throw new InvalidOperationException("Remove roof prerequisites changed before apply: " + refusal);
            var effect = new Receipts.RemoveRoofEffect { Designated = 0, Adopted = 0, Unroofed = 0 };
            var noRoof = map!.areaManager.NoRoof;
            var designator = new Designator_AreaNoRoof();
            foreach (var cell in cells)
            {
                var unroofed = map.roofGrid.RoofAt(cell) == null;
                if (noRoof[cell]) { if (unroofed) effect.Unroofed++; else effect.Adopted++; continue; }
                // An unroofed cell joins the NoRoof area too: left out, it
                // stays in the BuildRoof area auto-roof gave it (a room's
                // walls) and pawns roof it after the intent applied.
                var accepted = designator.CanDesignateCell(cell);
                if (!accepted.Accepted) throw new InvalidOperationException($"Native remove-roof (NoRoof area) is not accepted at {cell}: {accepted.Reason}");
                designator.DesignateSingleCell(cell);
                if (!noRoof[cell]) throw new InvalidOperationException($"NoRoof area was not observed at {cell}.");
                if (unroofed) effect.Unroofed++; else effect.Designated++;
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
