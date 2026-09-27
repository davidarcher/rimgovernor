#nullable enable
using System;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // RemoveFoundationIntent on Actions/Apply (#954): the RemoveFoundation
    // designation on one cell whose laid foundation is def_name (a plain
    // Bridge under a perimeter wall that a heavy bridge replaces). The game's
    // own designator decides whether the cell takes it (a wall standing on
    // it refuses); ordinary construction work lifts the foundation. A cell
    // already designated, or without that foundation, applies again.
    internal sealed class FoundationRemovalActionHandler : IActionHandler
    {
        private const string Kind = "Foundation removal";

        private static bool Valid(Operations.RemoveFoundationIntent? intent) => intent != null && intent.Cell != null && intent.Cell.HasX && intent.Cell.HasZ
            && intent.HasDefName && ProtoBoundary.IsIdentifier(intent.DefName);
        private static IntVec3 Cell(Operations.RemoveFoundationIntent intent) => new IntVec3(intent.Cell.X, 0, intent.Cell.Z);
        // Done: the foundation is gone, or the designation already stands.
        private static bool Holds(Operations.RemoveFoundationIntent intent, Map map)
        {
            var cell = Cell(intent);
            return map.terrainGrid.FoundationAt(cell)?.defName != intent.DefName
                || map.designationManager.DesignationAt(cell, DesignationDefOf.RemoveFoundation) != null;
        }

        private static ApplyPreconditions Rules(Operations.RemoveFoundationIntent? intent, Map map) => new ApplyPreconditions(Kind)
            .Require(() => Valid(intent), "removal requires a cell and a foundation def")
            .Require(() => Cell(intent!).InBounds(map) && !Cell(intent!).Fogged(map), "the cell is off the map or fogged")
            .Require(() => Holds(intent!, map) || new Designator_RemoveFoundation().CanDesignateCell(Cell(intent!)).Accepted,
                "the native designator refuses the cell (a building stands on it, or it supports one)");

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var rules = Rules(action.RemoveFoundation, ProtoBoundary.LoadedMap(context));
            return rules.Holds ? null : rules.Failure();
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var intent = action.RemoveFoundation;
            var rules = Rules(intent, map);
            if (!rules.Holds) throw new InvalidOperationException("Foundation removal prerequisites changed before apply: " + rules.Reason);
            var cell = Cell(intent);
            if (!Holds(intent, map)) map.designationManager.AddDesignation(new Designation(cell, DesignationDefOf.RemoveFoundation));
            if (!Holds(intent, map)) throw new InvalidOperationException("Native foundation removal readback did not apply.");
            return new Receipts.EffectEvidence { Designation = new Receipts.DesignationEffect { ThingId = "foundation-" + cell.x + "-" + cell.z,
                DesignationDef = "RemoveFoundation", Present = true, ResourceDef = intent.DefName, Cell = new Common.Cell { X = cell.x, Z = cell.z } } };
        }
    }
}
