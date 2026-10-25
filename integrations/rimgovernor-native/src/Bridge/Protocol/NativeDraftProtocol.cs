
namespace RimGovernor.Native
{
    internal static class NativeDraftProtocol
    {
        // Identity-only check for a target that carries no CAS token.
        internal static bool ValidEntityId(RimGovernor.Brain.GameOrders.Operations.EntityPrecondition? entity) => entity != null
            && entity.EntityId is not null && GameThread.IsIdentifier(entity.EntityId);

        // ValidEntityTokenOptional accepts an entity precondition with or
        // without its snapshot token: the kinds the controller dispatches
        // under a running clock omit it and rely on the apply-time
        // rules; a token that is sent must still be well formed.
        internal static bool ValidEntityTokenOptional(RimGovernor.Brain.GameOrders.Operations.EntityPrecondition? entity) => ValidEntityId(entity)
            && (!TokenSent((entity ?? throw new System.InvalidOperationException("Required native value is unavailable: entity."))) || GameThread.IsIdentifier((entity ?? throw new System.InvalidOperationException("Required native value is unavailable: entity.")).ExpectedSnapshotToken));

        internal static bool TokenSent(RimGovernor.Brain.GameOrders.Operations.EntityPrecondition entity) => entity.ExpectedSnapshotToken is not null && (entity.ExpectedSnapshotToken ?? "").Length > 0;

        internal static RimGovernor.Brain.GameFacts.Common.Failure Failure(NativePawnControlResult result, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => new RimGovernor.Brain.GameFacts.Common.Failure
        {
            RefusalClass = RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified,
            Code = result == NativePawnControlResult.StaleIdentity ? RimGovernor.Brain.GameFacts.Common.FailureCode.StaleIdentity
                : result == NativePawnControlResult.StaleSnapshot ? RimGovernor.Brain.GameFacts.Common.FailureCode.OwnerConflict
                : RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable,
            Detail = "Native pawn control guard refused: " + result,
            ObservedContext = context
        };
    }
}
