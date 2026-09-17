#nullable enable
using System;
using System.Linq;
using System.Text;
using Google.Protobuf;
using RimGovernor.Protocol.Common;
using Wire = RimGovernor.Protocol.Placement;

namespace HomeBridge.BridgeTools
{
    internal static class PlacementProtocol
    {
        internal const int MaximumReplyBytes = 1024 * 1024;

        internal static bool Validate(Wire.PlacementRequest request, out Failure failure)
        {
            failure = new Failure { Code = FailureCode.InvalidRequest, Detail = "Placement request requires identity and 1..16 complete candidates." };
            if (request.Identity == null || request.Placements.Count < 1 || request.Placements.Count > 16) return false;
            foreach (var row in request.Placements)
                if (!row.HasDefName || !ProtoBoundary.IsIdentifier(row.DefName) || !row.HasX || !row.HasZ
                    || !row.HasRotation || row.Rotation < Wire.Rotation.North || row.Rotation > Wire.Rotation.All
                    || row.HasStuff && row.Stuff.Length != 0 && !ProtoBoundary.IsIdentifier(row.Stuff)) return false;
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
                if (value.costList.Count > 256 || value.rotations.Count < 1 || value.rotations.Count > 4)
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
                    wire.CostList.Add(new Wire.PlacementCost { DefName = cost.defName, Count = cost.count });
                }
                wire.Materials = Materials(value.materials, value.costList.Select(row => row.defName).Distinct().ToArray());
                foreach (var row in value.rotations)
                {
                    if (row.occupiedCells.Count < 1 || row.occupiedCells.Count > 4096 || row.blockingThings.Count > 4096)
                        return Fail(FailureCode.CapacityExhausted, "Native geometry exceeds preview bounds.", context);
                    var rotation = new Wire.PlacementRotation {
                        Rotation = Cardinal(row.rotation), Accepted = row.accepted,
                        Reason = PlacementPreviewOperation.Diagnostic(row.reason)
                    };
                    foreach (var cell in row.occupiedCells)
                        rotation.OccupiedCells.Add(new Cell { X = cell.x, Z = cell.z });
                    foreach (var cell in row.interactionCells)
                        rotation.InteractionCells.Add(new Cell { X = cell.x, Z = cell.z });
                    if (row.watchCellsAccessible.HasValue) rotation.WatchCellsAccessible = row.watchCellsAccessible.Value;
                    foreach (var blocker in row.blockingThings)
                    {
                        if (!ProtoBoundary.IsIdentifier(blocker.category)) throw new InvalidOperationException("Native blocker category unavailable.");
                        rotation.BlockingThings.Add(new Wire.PlacementBlocker {
                            Category = blocker.category, IsBlueprint = blocker.isBlueprint, IsFrame = blocker.isFrame,
                            WouldBeWiped = blocker.wouldBeWiped, FrameWouldBeCancelled = blocker.frameWouldBeCancelled
                        });
                    }
                    wire.Rotations.Add(rotation);
                }
                if (wire.Rotations.Select(row => row.Rotation).Distinct().Count() != wire.Rotations.Count)
                    throw new InvalidOperationException("Native rotation scan contains duplicates.");
                return new Wire.CandidateReply { Evaluated = wire };
            }
            catch (Exception error)
            { return Fail(FailureCode.Unavailable, "Native preview mapping failed: " + error.Message, context); }
        }

        private static Wire.PlacementMaterials Materials(PlacementMaterials materials, string[] definitions)
        {
            if (materials.unreadable || materials.rows.Count > 256
                || materials.rows.Select(row => row.defName).Distinct().Count() != materials.rows.Count
                || !definitions.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(
                    materials.rows.Select(row => row.defName).OrderBy(name => name, StringComparer.Ordinal)))
                return new Wire.PlacementMaterials { Unavailable = new Unavailable {
                    Reason = UnavailableReason.ReadFailed, Detail = "Complete native material stock could not be read." } };
            var known = new Wire.MaterialRows();
            foreach (var row in materials.rows)
            {
                if (!ProtoBoundary.IsIdentifier(row.defName) || row.available < 0)
                    throw new InvalidOperationException("Invalid native material stock.");
                var stock = new Wire.PlacementMaterialStock { DefName = row.defName };
                if (row.available.HasValue) stock.Available = row.available.Value;
                known.Rows.Add(stock);
            }
            return new Wire.PlacementMaterials { Known = known };
        }

        internal static Wire.PlacementReply Bounded(Wire.PlacementReply reply)
        {
            if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) <= MaximumReplyBytes) return reply;
            return new Wire.PlacementReply { Failure = new Failure {
                Code = FailureCode.CapacityExhausted, Detail = "Placement reply exceeds 1 MiB.",
                ObservedContext = reply.Batch?.Context
            } };
        }

        private static Wire.CandidateReply Fail(FailureCode code, string detail, ObservationContext context) =>
            new Wire.CandidateReply { Failure = new Failure { Code = code,
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
