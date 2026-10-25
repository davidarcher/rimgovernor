using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimGovernor.Contracts.Wire.Common;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Wire = RimGovernor.Contracts.Wire.Placement;

namespace HomeBridge.BridgeTools
{
    internal static class PlacementProtocol
    {

        internal static bool Validate(Wire.PlacementRequest request, out Failure failure)
        {
            // The 64-candidate bound is kept (docs/developers/contracts/kept-constants.md).
            failure = new Failure { RefusalClass = RimGovernor.Contracts.Wire.Common.RefusalClass.Unclassified, Code = FailureCode.InvalidRequest, Detail = "Placement request requires identity and 1..64 complete candidates." };
            if (request.Placements.Count < 1 || request.Placements.Count > 64) return false;
            foreach (var row in request.Placements)
                if (!ProtoBoundary.IsIdentifier(row.DefName) || row.Rotation < Wire.Rotation.North || row.Rotation > Wire.Rotation.All
                    || row.Stuff.Length != 0 && !ProtoBoundary.IsIdentifier(row.Stuff)) return false;
            return true;
        }

        internal static string RotationName(Wire.Rotation rotation)
        {
            switch (rotation)
            {
                case Wire.Rotation.North: return "north";
                case Wire.Rotation.East: return "east";
                case Wire.Rotation.South: return "south";
                case Wire.Rotation.West: return "west";
                case Wire.Rotation.All: return "all";
                default: throw new ArgumentOutOfRangeException(nameof(rotation));
            }
        }

        internal static Wire.CandidateReply Map(PlacementPreviewResult result, ObservationContext context)
        {
            if (result is PlacementPreviewFailure refused)
                return Fail(Code(refused.Kind), refused.error, context);
            if (!(result is PlacementPreviewEvaluation value))
                return Fail(FailureCode.Unavailable, "Native preview result unavailable.", context);
            try
            {
                if (value.rotations.Count < 1 || value.rotations.Count > 4)
                    return Fail(FailureCode.CapacityExhausted, "Native preview exceeds bounded collections.", context);
                var costs = new List<Wire.PlacementCost>();
                foreach (var cost in value.costList)
                {
                    if (!ProtoBoundary.IsIdentifier(cost.defName) || cost.count < 0)
                        throw new InvalidOperationException("Native cost is invalid.");
                    costs.Add(new Wire.PlacementCost { DefName = cost.defName, Count = cost.count });
                }
                var materials = Materials(value.materials, value.costList.Select(row => row.defName).Distinct().ToArray());
                var rotations = new List<Wire.PlacementRotation>();
                foreach (var row in value.rotations)
                {
                    if (row.occupiedCells.Count < 1)
                        return Fail(FailureCode.CapacityExhausted, "Native geometry is empty.", context);
                    var blockers = new List<Wire.PlacementBlocker>();
                    foreach (var blocker in row.blockingThings)
                    {
                        if (!ProtoBoundary.IsIdentifier(blocker.category)) throw new InvalidOperationException("Native blocker category unavailable.");
                        blockers.Add(new Wire.PlacementBlocker {
                            Category = blocker.category, DefName = blocker.Thing.def.defName, IsBlueprint = blocker.isBlueprint, IsFrame = blocker.isFrame,
                            WouldBeWiped = blocker.wouldBeWiped, FrameWouldBeCancelled = blocker.frameWouldBeCancelled
                        });
                    }
                    rotations.Add(new Wire.PlacementRotation {
                        Accepted = row.accepted,
                        Reason = PlacementPreviewOperation.Diagnostic(row.reason),
                        OccupiedCells = [.. row.occupiedCells.Select(cell => new RimGovernor.Brain.Domain.Cell(cell.x, cell.z))],
                        InteractionCells = [.. row.interactionCells.Select(cell => new RimGovernor.Brain.Domain.Cell(cell.x, cell.z))],
                        BlockingThings = [.. blockers],
                        WatchCells = row.watchCellsAccessible is { } reachable
                            ? (reachable ? Wire.WatchAccess.Reachable : Wire.WatchAccess.Unreachable)
                            : Wire.WatchAccess.NotWatched,
                        Wind = row.windBlockedCells is { } blocked
                            ? new Observations.BuildingWind.WindTurbine { BlockedCells = (uint)blocked }
                            : new Observations.BuildingWind.NotWindTurbine()
                    });
                }
                if (value.rotations.Select(row => row.rotation).Distinct().Count() != value.rotations.Count)
                    throw new InvalidOperationException("Native rotation scan contains duplicates.");
                return new Wire.CandidateReply { Outcome = new Wire.PlacementEvaluated {
                    CanPlace = value.canPlace, MadeFromStuff = value.madeFromStuff,
                    ResearchFinished = value.researchFinished, BuildableByPlayer = value.buildableByPlayer,
                    CostList = [.. costs], Materials = materials, Rotations = [.. rotations]
                } };
            }
            catch (Exception error)
            { return Fail(FailureCode.Unavailable, "Native preview mapping failed: " + error.Message, context); }
        }

        /// <summary>The native stock scan, complete; an incomplete one fails the candidate.</summary>
        private static Wire.PlacementMaterials Materials(PlacementMaterials materials, string[] definitions)
        {
            if (materials.unreadable
                || materials.rows.Select(row => row.defName).Distinct().Count() != materials.rows.Count
                || !definitions.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(
                    materials.rows.Select(row => row.defName).OrderBy(name => name, StringComparer.Ordinal)))
                throw new InvalidOperationException("Native material scan is incomplete.");
            var rows = new List<Wire.PlacementMaterialStock>();
            foreach (var row in materials.rows)
            {
                if (!ProtoBoundary.IsIdentifier(row.defName) || row.available < 0)
                    throw new InvalidOperationException("Invalid native material stock.");
                // A complete scan counts every row; a row with no count marks the scan unreadable above.
                rows.Add(new Wire.PlacementMaterialStock { DefName = row.defName, Available = row.available ?? throw new InvalidOperationException("Uncounted native material stock.") });
            }
            return new Wire.PlacementMaterials { Rows = [.. rows] };
        }

        private static Wire.CandidateReply Fail(FailureCode code, string detail, ObservationContext context) =>
            new Wire.CandidateReply { Outcome = new Failure { RefusalClass = RimGovernor.Contracts.Wire.Common.RefusalClass.Unclassified, Code = code,
                Detail = PlacementPreviewOperation.Diagnostic(detail), ObservedContext = context } };
        private static FailureCode Code(PlacementFailureKind kind)
        {
            switch (kind)
            {
                case PlacementFailureKind.InvalidRequest: return FailureCode.InvalidRequest;
                case PlacementFailureKind.NotFound: return FailureCode.NotFound;
                case PlacementFailureKind.Unsupported: return FailureCode.Unsupported;
                case PlacementFailureKind.CapacityExceeded: return FailureCode.CapacityExhausted;
                default: return FailureCode.Unavailable;
            }
        }
    }
}
