using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // RemoveRoofIntent on Actions/Apply: vanilla remove roof, the
    // NoRoof area (Designator_AreaNoRoof, which also clears BuildRoof), over
    // cells, unroofed ones included so nothing roofs them afterwards. A
    // cell already in the NoRoof area applies again; a fogged or out-of-bounds cell or a thick roof refuses
    // the whole intent. Applied means designated: pawns remove the roof.
    internal static class NativeRemoveRoof
    {
        private static int installed;
        // Vanilla auto-roof (AutoBuildRoofAreaSetter) re-adds an enclosed
        // room's cells to the BuildRoof area without clearing NoRoof, so
        // pawns would rebuild the roof they are removing. The NoRoof
        // designator makes the two areas exclusive; this holds that rule
        // for roof building.
        private static void Install()
        {
            if (System.Threading.Interlocked.CompareExchange(ref installed, 1, 0) != 0) return;
            new Harmony("rimgovernor.remove-roof").Patch(AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.HasJobOnCell)),
                postfix: new HarmonyMethod(typeof(NativeRemoveRoof), nameof(NoRoofHoldsBuild)));
        }
        private static void NoRoofHoldsBuild(Pawn pawn, IntVec3 c, ref bool __result)
        { if (__result && pawn.Map != null && pawn.Map.areaManager.NoRoof[c]) __result = false; }

        private static string? Refusal(RimGovernor.Brain.GameOrders.Operations.RemoveRoofIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Map? map, out List<IntVec3> cells)
        {
            map = null; cells = new List<IntVec3>();
            if (intent == null || intent.Cells.Count == 0) return "Remove roof requires cells.";
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

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.RemoveRoofIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var refusal = Refusal(intent, context, out _, out _);
            return refusal == null ? null : GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, refusal);
        }

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.RemoveRoofIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            Install();
            var refusal = Refusal(intent, context, out var map, out var cells);
            if (refusal != null) throw new InvalidOperationException("Remove roof prerequisites changed before apply: " + refusal);
            int designatedCount = 0;
            foreach (var cell in cells)
            {
                var unroofed = (map ?? throw new System.InvalidOperationException("Required native value is unavailable: map.")).roofGrid.RoofAt(cell) == null;
                if (map.areaManager.NoRoof[cell]) continue;
                // An unroofed cell joins the NoRoof area too: left out, it
                // stays in the BuildRoof area auto-roof gave it (a room's
                // walls) and pawns roof it after the intent applied.
                Designate(map, cell);
                if (!unroofed) designatedCount++;
            }
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.RemoveRoofEffect { Designated = designatedCount } };
        }

        // Puts one cell in the NoRoof area through the game's designator (which also clears BuildRoof): the intent's
        // cells, and a RemoveRoof set step's roofed members (#3617).
        internal static void Designate(Map map, IntVec3 cell)
        {
            Install();
            var designator = new Designator_AreaNoRoof();
            var accepted = designator.CanDesignateCell(cell);
            if (!accepted.Accepted) throw new InvalidOperationException($"Native remove-roof (NoRoof area) is not accepted at {cell}: {accepted.Reason}");
            designator.DesignateSingleCell(cell);
            if (!map.areaManager.NoRoof[cell]) throw new InvalidOperationException($"NoRoof area was not observed at {cell}.");
        }
    }

    internal sealed class RemoveRoofActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeRemoveRoof.Validate(action.Intent?.RemoveRoof, context);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeRemoveRoof.Apply(action.Intent?.RemoveRoof, context);
    }
}
