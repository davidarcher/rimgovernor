#nullable enable
using System;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // RemoveFloorIntent on Actions/Apply: vanilla's RemoveFloor
    // designation on one cell whose constructed floor is def_name, so
    // clearance can free planned ground. The game's own designator decides
    // whether the cell takes it; ordinary construction work removes the
    // floor. A cell already designated, or without that floor, applies again.
    internal sealed class FloorRemovalActionHandler : IActionHandler
    {
        private const string Kind = "Floor removal";

        private static bool Valid(Operations.RemoveFloorIntent? intent) => intent != null && intent.Cell != null && intent.Cell.HasX && intent.Cell.HasZ
            && intent.HasDefName && ProtoBoundary.IsIdentifier(intent.DefName);
        private static IntVec3 Cell(Operations.RemoveFloorIntent intent) => new IntVec3(intent.Cell.X, 0, intent.Cell.Z);
        // Done: the floor is gone, or the designation already stands.
        private static bool Holds(Operations.RemoveFloorIntent intent, Map map)
        {
            var cell = Cell(intent);
            return map.terrainGrid.TerrainAt(cell)?.defName != intent.DefName
                || map.designationManager.DesignationAt(cell, DesignationDefOf.RemoveFloor) != null;
        }

        private static ApplyPreconditions Rules(Operations.RemoveFloorIntent? intent, Map map) => new ApplyPreconditions(Kind)
            .Require(() => Valid(intent), "removal requires a cell and a floor def")
            .Require(() => Cell(intent!).InBounds(map) && !Cell(intent!).Fogged(map), "the cell is off the map or fogged")
            .Require(() => Holds(intent!, map) || new Designator_RemoveFloor().CanDesignateCell(Cell(intent!)).Accepted,
                "the native designator refuses the cell (no removable floor, or a building blocks it)");

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var rules = Rules(action.RemoveFloor, ProtoBoundary.LoadedMap(context));
            return rules.Holds ? null : rules.Failure();
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var intent = action.RemoveFloor;
            var rules = Rules(intent, map);
            if (!rules.Holds) throw new InvalidOperationException("Floor removal prerequisites changed before apply: " + rules.Reason);
            var cell = Cell(intent);
            if (!Holds(intent, map)) map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.RemoveFloor));
            if (!Holds(intent, map)) throw new InvalidOperationException("Native floor removal readback did not apply.");
            return new Receipts.EffectEvidence { Designation = new Receipts.DesignationEffect { ThingId = "floor-" + cell.x + "-" + cell.z,
                DesignationDef = "RemoveFloor", Present = true, ResourceDef = intent.DefName, Cell = new Common.Cell { X = cell.x, Z = cell.z } } };
        }
    }
}
