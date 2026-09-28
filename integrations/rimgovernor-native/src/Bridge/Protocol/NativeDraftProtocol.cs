#nullable enable
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeDraftProtocol
    {
        // Identity-only check for a target that carries no CAS token.
        internal static bool ValidEntityId(Operations.EntityPrecondition? entity) => entity != null
            && entity.HasEntityId && ProtoBoundary.IsIdentifier(entity.EntityId);

        // ValidEntityTokenOptional accepts an entity precondition with or
        // without its snapshot token: the kinds the controller dispatches
        // under a running clock omit it (#243) and rely on the apply-time
        // rules; a token that is sent must still be well formed.
        internal static bool ValidEntityTokenOptional(Operations.EntityPrecondition? entity) => ValidEntityId(entity)
            && (!TokenSent(entity!) || ProtoBoundary.IsIdentifier(entity!.ExpectedSnapshotToken));

        internal static bool TokenSent(Operations.EntityPrecondition entity) => entity.HasExpectedSnapshotToken && entity.ExpectedSnapshotToken.Length > 0;

        internal static Common.Failure Failure(NativePawnControlResult result, Common.ObservationContext context) => new Common.Failure
        {
            Code = result == NativePawnControlResult.StaleIdentity ? Common.FailureCode.StaleIdentity
                : result == NativePawnControlResult.StaleSnapshot ? Common.FailureCode.OwnerConflict
                : result == NativePawnControlResult.CapacityExhausted ? Common.FailureCode.CapacityExhausted
                : Common.FailureCode.Unavailable,
            Detail = "Native pawn control guard refused: " + result,
            ObservedContext = context.Clone()
        };
    }
}
