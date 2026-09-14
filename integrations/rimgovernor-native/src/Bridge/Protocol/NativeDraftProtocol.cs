#nullable enable
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativeDraftProtocol
    {
        internal static bool ValidEntity(Operations.EntityPrecondition? entity) => entity != null
            && entity.HasEntityId && ProtoBoundary.IsIdentifier(entity.EntityId)
            && entity.HasExpectedSnapshotToken && ProtoBoundary.IsIdentifier(entity.ExpectedSnapshotToken);

        // Identity-only check for a target whose CAS token travels on a
        // decoupled sibling field instead of this EntityPrecondition's own
        // (e.g. RecoverService.expected_target_snapshot_token), the same
        // split QueueSurgery uses for its patient's health-signature token.
        internal static bool ValidEntityId(Operations.EntityPrecondition? entity) => entity != null
            && entity.HasEntityId && ProtoBoundary.IsIdentifier(entity.EntityId);

        internal static bool ValidOwner(Authority.Owner? owner) => owner != null
            && owner.HasControllerSessionId && ProtoBoundary.IsIdentifier(owner.ControllerSessionId)
            && owner.HasPlayerDirection && owner.PlayerDirection > 0;

        internal static bool Validate(Operations.SetDrafted? command, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Draft command requires exact pawn ID, native snapshot token and explicit drafted state.");
            if (command == null || !ValidEntity(command.Pawn) || !command.HasDrafted
                || command.HasExpectedDraftOwner && !ProtoBoundary.IsIdentifier(command.ExpectedDraftOwner)) return false;
            if (command.AllowPersistentDraft)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported,
                    "Persistent drafting requires scoped player intent; this adapter supports temporary owned draft only.");
                return false;
            }
            return true;
        }

        internal static bool ValidateRelease(Operations.ReleaseOwnedDraftRequest? request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Release requires exact pawn ID, native snapshot token, original claim ID and complete original owner/direction.");
            return request != null && ValidEntity(request.Pawn) && request.HasExpectedClaimId
                && ProtoBoundary.IsIdentifier(request.ExpectedClaimId) && ValidOwner(request.OriginalOwner);
        }

        internal static bool SameOwner(Authority.Owner? left, Authority.Owner? right) => ValidOwner(left) && ValidOwner(right) && left!.Equals(right);

        internal static Common.Failure Failure(NativePawnControlResult result, Common.ObservationContext context) => new Common.Failure
        {
            Code = result == NativePawnControlResult.StaleIdentity ? Common.FailureCode.StaleIdentity
                : result == NativePawnControlResult.StaleSnapshot || result == NativePawnControlResult.ClaimMismatch ? Common.FailureCode.OwnerConflict
                : result == NativePawnControlResult.AuthorityRequired ? Common.FailureCode.AuthorityRequired
                : result == NativePawnControlResult.CapacityExhausted ? Common.FailureCode.CapacityExhausted
                : result == NativePawnControlResult.Ineligible ? Common.FailureCode.InvalidRequest
                : Common.FailureCode.Unavailable,
            Detail = "Native pawn control guard refused: " + result,
            ObservedContext = context.Clone()
        };

        internal static Receipts.JobEffect Effect(string pawnId, bool drafted, string token,
            bool issued, bool verified, string? claimId = null, Authority.Owner? owner = null)
        {
            var result = new Receipts.JobEffect { PawnId = pawnId, Drafted = drafted,
                ResultingSnapshotToken = token, Issued = issued, Verified = verified,
                VerifiedReason = verified ? "Exact native draft state and claim readback." : "Native draft outcome requires observation." };
            if (claimId != null) result.DraftClaimId = claimId;
            if (owner != null) result.DraftOwner = owner.ControllerSessionId;
            return result;
        }
    }
}
