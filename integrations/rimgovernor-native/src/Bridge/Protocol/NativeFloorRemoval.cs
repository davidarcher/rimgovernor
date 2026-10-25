using System;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // RemoveFloorIntent on Actions/Apply: vanilla's RemoveFloor
    // designation on one cell whose constructed floor is def_name, so
    // clearance can free planned ground. The game's own designator decides
    // whether the cell takes it; ordinary construction work removes the
    // floor. A cell already designated, or without that floor, applies again.
    internal sealed class FloorRemovalActionHandler : IActionHandler
    {
        private const string Kind = "Floor removal";

        private static bool Valid(RimGovernor.Brain.GameOrders.Operations.RemoveFloorIntent? intent) => intent != null
            && intent.DefName is not null && GameThread.IsIdentifier(intent.DefName);
        private static IntVec3 Cell(RimGovernor.Brain.GameOrders.Operations.RemoveFloorIntent intent) => new IntVec3(intent.Cell.X, 0, intent.Cell.Z);
        // Done: the floor is gone, or the designation already stands.
        private static bool Holds(RimGovernor.Brain.GameOrders.Operations.RemoveFloorIntent intent, Map map)
        {
            var cell = Cell(intent);
            return map.terrainGrid.TerrainAt(cell)?.defName != (intent.DefName ?? "")
                || map.designationManager.DesignationAt(cell, DesignationDefOf.RemoveFloor) != null;
        }

        private static ApplyPreconditions Rules(RimGovernor.Brain.GameOrders.Operations.RemoveFloorIntent? intent, Map map) => new ApplyPreconditions(Kind)
            .Require(() => Valid(intent), "removal requires a cell and a floor def")
            .Require(() => intent != null && (Cell(intent).InBounds(map) && !Cell(intent).Fogged(map)), "the cell is off the map or fogged")
            .Require(() => intent != null && (Holds(intent, map) || new Designator_RemoveFloor().CanDesignateCell(Cell(intent)).Accepted),
                "the native designator refuses the cell (no removable floor, or a building blocks it)");

        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var rules = Rules(action.Intent?.RemoveFloor, GameThread.LoadedMap(context));
            return rules.Holds ? null : rules.Failure();
        }

        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var map = GameThread.LoadedMap(context);
            var intent = action.Intent?.RemoveFloor ?? throw new InvalidOperationException("Action carries no floor-removal intent.");
            var rules = Rules(intent, map);
            if (!rules.Holds) throw new InvalidOperationException("Floor removal prerequisites changed before apply: " + rules.Reason);
            var cell = Cell(intent);
            if (!Holds(intent, map)) map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.RemoveFloor));
            if (!Holds(intent, map)) throw new InvalidOperationException("Native floor removal readback did not apply.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.DesignationEffect { ThingId = "floor-" + cell.x + "-" + cell.z,
                DesignationDef = "RemoveFloor", Present = true } };
        }
    }
}
