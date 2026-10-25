using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimGovernor.Brain.GameFacts.Common;

namespace RimGovernor.Native
{
    internal static class PlacementProtocol
    {

        internal static bool Validate(RimGovernor.Brain.GameOrders.Placement.PlacementRequest request, out Failure failure)
        {
            // The 64-candidate bound is kept (docs/developers/contracts/kept-constants.md).
            failure = new Failure { RefusalClass = RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified, Code = FailureCode.InvalidRequest, Detail = "Placement request requires identity and 1..64 complete candidates." };
            if (request.Placements.Count < 1 || request.Placements.Count > 64) return false;
            foreach (var row in request.Placements)
                if (!GameThread.IsIdentifier(row.DefName) || row.Rotation < RimGovernor.Brain.GameFacts.Placement.Rotation.North || row.Rotation > RimGovernor.Brain.GameFacts.Placement.Rotation.All
                    || row.Stuff.Length != 0 && !GameThread.IsIdentifier(row.Stuff)) return false;
            return true;
        }

        internal static string RotationName(RimGovernor.Brain.GameFacts.Placement.Rotation rotation)
        {
            switch (rotation)
            {
                case RimGovernor.Brain.GameFacts.Placement.Rotation.North: return "north";
                case RimGovernor.Brain.GameFacts.Placement.Rotation.East: return "east";
                case RimGovernor.Brain.GameFacts.Placement.Rotation.South: return "south";
                case RimGovernor.Brain.GameFacts.Placement.Rotation.West: return "west";
                case RimGovernor.Brain.GameFacts.Placement.Rotation.All: return "all";
                default: throw new ArgumentOutOfRangeException(nameof(rotation));
            }
        }

        internal static RimGovernor.Brain.GameFacts.Placement.CandidateReply Map(PlacementPreviewResult result, ObservationContext context)
        {
            if (result is PlacementPreviewFailure refused)
                return Fail(Code(refused.Kind), refused.error, context);
            if (!(result is PlacementPreviewEvaluation value))
                return Fail(FailureCode.Unavailable, "Native preview result unavailable.", context);
            try
            {
                if (value.rotations.Count < 1 || value.rotations.Count > 4)
                    return Fail(FailureCode.CapacityExhausted, "Native preview exceeds bounded collections.", context);
                var costs = new List<RimGovernor.Brain.GameFacts.Placement.PlacementCost>();
                foreach (var cost in value.costList)
                {
                    if (!GameThread.IsIdentifier(cost.defName) || cost.count < 0)
                        throw new InvalidOperationException("Native cost is invalid.");
                    costs.Add(new RimGovernor.Brain.GameFacts.Placement.PlacementCost { DefName = cost.defName, Count = cost.count });
                }
                var materials = Materials(value.materials, value.costList.Select(row => row.defName).Distinct().ToArray());
                var rotations = new List<RimGovernor.Brain.GameFacts.Placement.PlacementRotation>();
                foreach (var row in value.rotations)
                {
                    if (row.occupiedCells.Count < 1)
                        return Fail(FailureCode.CapacityExhausted, "Native geometry is empty.", context);
                    var blockers = new List<RimGovernor.Brain.GameFacts.Placement.PlacementBlocker>();
                    foreach (var blocker in row.blockingThings)
                    {
                        if (!GameThread.IsIdentifier(blocker.category)) throw new InvalidOperationException("Native blocker category unavailable.");
                        blockers.Add(new RimGovernor.Brain.GameFacts.Placement.PlacementBlocker {
                            Category = blocker.category, DefName = blocker.Thing.def.defName, IsBlueprint = blocker.isBlueprint, IsFrame = blocker.isFrame,
                            WouldBeWiped = blocker.wouldBeWiped, FrameWouldBeCancelled = blocker.frameWouldBeCancelled
                        });
                    }
                    rotations.Add(new RimGovernor.Brain.GameFacts.Placement.PlacementRotation {
                        Accepted = row.accepted,
                        Reason = PlacementPreviewOperation.Diagnostic(row.reason),
                        OccupiedCells = [.. row.occupiedCells.Select(cell => new RimGovernor.Brain.Domain.Cell(cell.x, cell.z))],
                        InteractionCells = [.. row.interactionCells.Select(cell => new RimGovernor.Brain.Domain.Cell(cell.x, cell.z))],
                        BlockingThings = [.. blockers],
                        WatchCells = row.watchCellsAccessible is { } reachable
                            ? (reachable ? RimGovernor.Brain.GameFacts.Placement.WatchAccess.Reachable : RimGovernor.Brain.GameFacts.Placement.WatchAccess.Unreachable)
                            : RimGovernor.Brain.GameFacts.Placement.WatchAccess.NotWatched,
                        Wind = row.windBlockedCells is { } blocked
                            ? new RimGovernor.Brain.GameFacts.Observations.BuildingWind.WindTurbine { BlockedCells = (uint)blocked }
                            : new RimGovernor.Brain.GameFacts.Observations.BuildingWind.NotWindTurbine()
                    });
                }
                if (value.rotations.Select(row => row.rotation).Distinct().Count() != value.rotations.Count)
                    throw new InvalidOperationException("Native rotation scan contains duplicates.");
                return new RimGovernor.Brain.GameFacts.Placement.CandidateReply { Outcome = new RimGovernor.Brain.GameFacts.Placement.PlacementEvaluated {
                    CanPlace = value.canPlace, MadeFromStuff = value.madeFromStuff,
                    ResearchFinished = value.researchFinished, BuildableByPlayer = value.buildableByPlayer,
                    CostList = [.. costs], Materials = materials, Rotations = [.. rotations]
                } };
            }
            catch (Exception error)
            { return Fail(FailureCode.Unavailable, "Native preview mapping failed: " + error.Message, context); }
        }

        /// <summary>The native stock scan, complete; an incomplete one fails the candidate.</summary>
        private static RimGovernor.Brain.GameFacts.Placement.PlacementMaterials Materials(PlacementMaterials materials, string[] definitions)
        {
            if (materials.unreadable
                || materials.rows.Select(row => row.defName).Distinct().Count() != materials.rows.Count
                || !definitions.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(
                    materials.rows.Select(row => row.defName).OrderBy(name => name, StringComparer.Ordinal)))
                throw new InvalidOperationException("Native material scan is incomplete.");
            var rows = new List<RimGovernor.Brain.GameFacts.Placement.PlacementMaterialStock>();
            foreach (var row in materials.rows)
            {
                if (!GameThread.IsIdentifier(row.defName) || row.available < 0)
                    throw new InvalidOperationException("Invalid native material stock.");
                // A complete scan counts every row; a row with no count marks the scan unreadable above.
                rows.Add(new RimGovernor.Brain.GameFacts.Placement.PlacementMaterialStock { DefName = row.defName, Available = row.available ?? throw new InvalidOperationException("Uncounted native material stock.") });
            }
            return new RimGovernor.Brain.GameFacts.Placement.PlacementMaterials { Rows = [.. rows] };
        }

        private static RimGovernor.Brain.GameFacts.Placement.CandidateReply Fail(FailureCode code, string detail, ObservationContext context) =>
            new RimGovernor.Brain.GameFacts.Placement.CandidateReply { Outcome = new Failure { RefusalClass = RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified, Code = code,
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
