using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;

namespace HomeBridge.BridgeTools
{
    // The authenticated host exposes this capability only to its explicit player-control owner.
    // Model/operation dispatch holds no acquisition capability; direction is a CAS identity, not authentication.
    public sealed class NativeAuthorityControlTools
    {
        private const string ToolName = "rimgovernor/authority_control";

        [Tool(ToolName, Title = "Native player control authority", Description = "Trusted host player-control path only. Acquire, renew or revoke native authority using exact identity and generation.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.authority.v1.ControlReply.", Always = true)]
        public async Task<object> Control(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ControlRequest. Never exposed to model dispatch.")] object request = null)
        {
            Authority.ControlRequest parsed;
            Common.Failure failure;
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Authority.ControlRequest.Parser, out parsed, out failure))
                return ProtoBoundary.Encode(new Authority.ControlReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => ProtoBoundary.Encode(Apply(parsed)), cancellationToken).ConfigureAwait(false);
        }

        internal static Authority.ControlReply Apply(Authority.ControlRequest request)
        {
            Common.Identity identity;
            switch (request.OperationCase)
            {
                case Authority.ControlRequest.OperationOneofCase.Acquire:
                    var acquire = request.Acquire;
                    if (!acquire.HasExpectedGeneration || acquire.ExpectedGeneration == 0 || acquire.Owner == null
                        || !acquire.Owner.HasControllerSessionId || !ProtoBoundary.IsIdentifier(acquire.Owner.ControllerSessionId)
                        || !acquire.Owner.HasPlayerDirection || acquire.Owner.PlayerDirection == 0
                        || !acquire.HasLeaseMs || !Duration(acquire.LeaseMs)) return Invalid();
                    identity = acquire.Identity;
                    break;
                case Authority.ControlRequest.OperationOneofCase.Renew:
                    var renew = request.Renew;
                    if (!renew.HasExpectedGeneration || renew.ExpectedGeneration == 0
                        || !renew.HasControllerSessionId || !ProtoBoundary.IsIdentifier(renew.ControllerSessionId)
                        || !renew.HasLeaseId || !ProtoBoundary.IsIdentifier(renew.LeaseId)
                        || !renew.HasLeaseMs || !Duration(renew.LeaseMs)) return Invalid();
                    identity = renew.Identity;
                    break;
                case Authority.ControlRequest.OperationOneofCase.Revoke:
                    var revoke = request.Revoke;
                    if (!revoke.HasExpectedGeneration || revoke.ExpectedGeneration == 0 || !revoke.HasReason
                        || !ExternalReason(revoke.Reason, out _)) return Invalid();
                    identity = revoke.Identity;
                    break;
                default: return Invalid();
            }
            Common.ObservationContext context;
            Common.Failure failure;
            if (!ProtoBoundary.ValidateIdentity(identity, Find.CurrentMap, out context, out failure))
                return new Authority.ControlReply { Failure = failure };
            if (request.OperationCase == Authority.ControlRequest.OperationOneofCase.Acquire)
                NativeAuthorityHooks.InitializeForCurrentGame();
            NativeControlAuthority state;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out state) || state == null)
                return new Authority.ControlReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable,
                    "Native authority hooks are not initialized.") };
            NativeControlResult result;
            switch (request.OperationCase)
            {
                case Authority.ControlRequest.OperationOneofCase.Acquire:
                    result = state.Acquire(request.Acquire.ExpectedGeneration, request.Acquire.Owner.ControllerSessionId,
                        request.Acquire.Owner.PlayerDirection, (int)request.Acquire.LeaseMs);
                    break;
                case Authority.ControlRequest.OperationOneofCase.Renew:
                    result = state.Renew(request.Renew.ExpectedGeneration, request.Renew.LeaseId,
                        request.Renew.ControllerSessionId, (int)request.Renew.LeaseMs);
                    break;
                default:
                    NativeControlRevocationReason reason;
                    ExternalReason(request.Revoke.Reason, out reason);
                    result = state.Revoke(request.Revoke.ExpectedGeneration, reason);
                    break;
            }
            context.NativeGeneration = result.Snapshot.Generation;
            if (!result.Success)
                return new Authority.ControlReply { Failure = Refusal(result.Error, context) };
            var projected = NativeAuthorityTools.Project(result.Snapshot, context);
            if (request.OperationCase == Authority.ControlRequest.OperationOneofCase.Revoke)
                return new Authority.ControlReply { Revoked = new Authority.Revoked
                {
                    Context = context,
                    Authority = new Authority.InactiveAuthority { Reason = request.Revoke.Reason }
                } };
            return new Authority.ControlReply { Granted = new Authority.Granted
            {
                Context = context, Authority = projected.Active, LeaseId = result.Snapshot.Lease.LeaseId
            } };
        }

        internal static Common.Failure Refusal(NativeControlError error, Common.ObservationContext context)
        {
            Common.FailureCode code;
            switch (error)
            {
                case NativeControlError.StaleIdentity: code = Common.FailureCode.StaleIdentity; break;
                case NativeControlError.StaleGeneration: code = Common.FailureCode.StaleGeneration; break;
                case NativeControlError.OwnerConflict: code = Common.FailureCode.OwnerConflict; break;
                case NativeControlError.AuthorityRequired:
                case NativeControlError.LeaseMismatch: code = Common.FailureCode.AuthorityRequired; break;
                case NativeControlError.GenerationExhausted: code = Common.FailureCode.CapacityExhausted; break;
                case NativeControlError.InvalidDirection:
                case NativeControlError.InvalidLeaseDuration:
                case NativeControlError.InvalidOwner: code = Common.FailureCode.InvalidRequest; break;
                default: code = Common.FailureCode.Unavailable; break;
            }
            return new Common.Failure { Code = code, Detail = "Native authority refused: " + error, ObservedContext = context };
        }

        private static bool Duration(uint value) => value >= 1000 && value <= 30000;
        private static Authority.ControlReply Invalid() => new Authority.ControlReply
        {
            Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Control requires a complete supported operation with valid owner, generation and duration.")
        };

        private static bool ExternalReason(Authority.RevocationReason value, out NativeControlRevocationReason reason)
        {
            switch (value)
            {
                case Authority.RevocationReason.Manual: reason = NativeControlRevocationReason.Manual; return true;
                case Authority.RevocationReason.PlayerDirection: reason = NativeControlRevocationReason.PlayerDirection; return true;
                case Authority.RevocationReason.Disconnect: reason = NativeControlRevocationReason.Disconnect; return true;
                case Authority.RevocationReason.Shutdown: reason = NativeControlRevocationReason.Shutdown; return true;
                default: reason = NativeControlRevocationReason.None; return false;
            }
        }
    }
}
