#nullable enable
using System;
using System.Linq;
using System.Text;
using RimGovernor.Contracts.Wire.Common;
using Wire = RimGovernor.Contracts.Wire.Placement;

namespace HomeBridge.BridgeTools
{
    internal static class PlacementProtocol
    {

        internal static bool Validate(Wire.PlacementRequest request, out Failure failure)
        {
            // The 64-candidate bound is kept (docs/developers/contracts/kept-constants.md).
            failure = new Failure { Code = FailureCode.InvalidRequest, Detail = "Placement request requires identity and 1..64 complete candidates." };
            if (request.Identity == null || request.Placements.Count < 1 || request.Placements.Count > 64) return false;
            foreach (var row in request.Placements)
                if (row.DefName is null || !ProtoBoundary.IsIdentifier(row.DefName) || row.X is null || row.Z is null
                    || row.Rotation is null || row.Rotation.GetValueOrDefault() < Wire.Rotation.North || row.Rotation.GetValueOrDefault() > Wire.Rotation.All
                    || row.Stuff is not null && (row.Stuff ?? "").Length != 0 && !ProtoBoundary.IsIdentifier(row.Stuff)) return false;
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
                var wire = new Wire.PlacementEvaluated {
                    CanPlace = value.canPlace, MadeFromStuff = value.madeFromStuff,
                    Passability = Passability(value.passability), IsDoor = value.isDoor,
                    ResearchFinished = value.researchFinished, BuildableByPlayer = value.buildableByPlayer
                };
                foreach (var cost in value.costList)
                {
                    if (!ProtoBoundary.IsIdentifier(cost.defName) || cost.count < 0)
                        throw new InvalidOperationException("Native cost is invalid.");
                    wire = wire with { CostList = [.. wire.CostList, new Wire.PlacementCost { DefName = cost.defName, Count = cost.count }] };
                }
                wire = wire with { Materials = Materials(value.materials, value.costList.Select(row => row.defName).Distinct().ToArray()) };
                foreach (var row in value.rotations)
                {
                    if (row.occupiedCells.Count < 1)
                        return Fail(FailureCode.CapacityExhausted, "Native geometry is empty.", context);
                    var rotation = new Wire.PlacementRotation {
                        Rotation = Cardinal(row.rotation), Accepted = row.accepted,
                        Reason = PlacementPreviewOperation.Diagnostic(row.reason)
                    };
                    foreach (var cell in row.occupiedCells)
                        rotation = rotation with { OccupiedCells = [.. rotation.OccupiedCells, new Cell { X = cell.x, Z = cell.z }] };
                    foreach (var cell in row.interactionCells)
                        rotation = rotation with { InteractionCells = [.. rotation.InteractionCells, new Cell { X = cell.x, Z = cell.z }] };
                    if (row.watchCellsAccessible.HasValue) rotation = rotation with { WatchCellsAccessible = row.watchCellsAccessible.Value };
                    if (row.windBlockedCells.HasValue) rotation = rotation with { WindBlockedCells = (uint)row.windBlockedCells.Value };
                    foreach (var blocker in row.blockingThings)
                    {
                        if (!ProtoBoundary.IsIdentifier(blocker.category)) throw new InvalidOperationException("Native blocker category unavailable.");
                        rotation = rotation with { BlockingThings = [.. rotation.BlockingThings, new Wire.PlacementBlocker {
                            Category = blocker.category, DefName = blocker.Thing.def.defName, IsBlueprint = blocker.isBlueprint, IsFrame = blocker.isFrame,
                            WouldBeWiped = blocker.wouldBeWiped, FrameWouldBeCancelled = blocker.frameWouldBeCancelled
                        }] };
                    }
                    wire = wire with { Rotations = [.. wire.Rotations, rotation] };
                }
                if (wire.Rotations.Select(row => row.Rotation.GetValueOrDefault()).Distinct().Count() != wire.Rotations.Count)
                    throw new InvalidOperationException("Native rotation scan contains duplicates.");
                return new Wire.CandidateReply { Outcome = wire };
            }
            catch (Exception error)
            { return Fail(FailureCode.Unavailable, "Native preview mapping failed: " + error.Message, context); }
        }

        private static Wire.PlacementMaterials Materials(PlacementMaterials materials, string[] definitions)
        {
            if (materials.unreadable
                || materials.rows.Select(row => row.defName).Distinct().Count() != materials.rows.Count
                || !definitions.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(
                    materials.rows.Select(row => row.defName).OrderBy(name => name, StringComparer.Ordinal)))
                return new Wire.PlacementMaterials { Availability = new Unavailable {
                    Reason = UnavailableReason.ReadFailed, Detail = "Complete native material stock could not be read." } };
            var known = new Wire.MaterialRows();
            foreach (var row in materials.rows)
            {
                if (!ProtoBoundary.IsIdentifier(row.defName) || row.available < 0)
                    throw new InvalidOperationException("Invalid native material stock.");
                var stock = new Wire.PlacementMaterialStock { DefName = row.defName };
                if (row.available.HasValue) stock = stock with { Available = row.available.Value };
                known = known with { Rows = [.. known.Rows, stock] };
            }
            return new Wire.PlacementMaterials { Availability = known };
        }

        private static Wire.CandidateReply Fail(FailureCode code, string detail, ObservationContext context) =>
            new Wire.CandidateReply { Outcome = new Failure { Code = code,
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
        private static Wire.Passability Passability(string value)
        {
            switch (value)
            {
                case "Standable": return Wire.Passability.Standable;
                case "PassThroughOnly": return Wire.Passability.PassThroughOnly;
                case "Impassable": return Wire.Passability.Impassable;
                default: throw new InvalidOperationException("Unknown native passability.");
            }
        }
        private static Wire.Rotation Cardinal(string value)
        {
            switch (value)
            {
                case "north": return Wire.Rotation.North;
                case "east": return Wire.Rotation.East;
                case "south": return Wire.Rotation.South;
                case "west": return Wire.Rotation.West;
                default: throw new InvalidOperationException("Unknown native rotation.");
            }
        }
    }
}
