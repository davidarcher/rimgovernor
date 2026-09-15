using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;

namespace HomeBridge.BridgeTools
{
    // The authenticated host exposes this capability only to its explicit player-control owner.
    // Model/operation dispatch holds no mode-setting capability; direction is a CAS identity, not authentication.
    public sealed class NativeAuthorityControlTools
    {
        private const string ToolName = "rimgovernor/authority_control";

        [Tool(ToolName, Title = "Native player control authority", Description = "Trusted host player-control path only. Set or revoke native authority mode using exact identity and generation.")]
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
                case Authority.ControlRequest.OperationOneofCase.SetMode:
                    var setMode = request.SetMode;
                    if (!setMode.HasExpectedGeneration || setMode.ExpectedGeneration == 0 || !setMode.HasMode
                        || (setMode.Mode != Authority.Mode.Auto && setMode.Mode != Authority.Mode.Manual)) return Invalid();
                    identity = setMode.Identity;
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
            bool settingAuto = request.OperationCase == Authority.ControlRequest.OperationOneofCase.SetMode
                && request.SetMode.Mode == Authority.Mode.Auto;
            if (settingAuto)
                NativeAuthorityHooks.InitializeForCurrentGame();
            NativeControlAuthority state;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out state) || state == null)
                return new Authority.ControlReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable,
                    "Native authority hooks are not initialized.") };
            NativeControlResult result;
            switch (request.OperationCase)
            {
                case Authority.ControlRequest.OperationOneofCase.SetMode:
                    var mode = request.SetMode.Mode == Authority.Mode.Auto ? NativeControlMode.Auto : NativeControlMode.Manual;
                    result = state.SetMode(request.SetMode.ExpectedGeneration, mode);
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
            if (request.OperationCase == Authority.ControlRequest.OperationOneofCase.Revoke)
                return new Authority.ControlReply { Revoked = new Authority.Revoked
                {
                    Context = context,
                    Authority = new Authority.InactiveAuthority { Reason = request.Revoke.Reason }
                } };
            if (!settingAuto)
            {
                var projectedInactive = NativeAuthorityTools.Project(result.Snapshot, context);
                return new Authority.ControlReply { Revoked = new Authority.Revoked
                {
                    Context = context,
                    Authority = projectedInactive.Inactive ?? new Authority.InactiveAuthority { Reason = Authority.RevocationReason.Manual }
                } };
            }
            var projected = NativeAuthorityTools.Project(result.Snapshot, context);
            return new Authority.ControlReply { Granted = new Authority.Granted
            {
                Context = context, Authority = projected.Active
            } };
        }

        internal static Common.Failure Refusal(NativeControlError error, Common.ObservationContext context)
        {
            Common.FailureCode code;
            switch (error)
            {
                case NativeControlError.StaleIdentity: code = Common.FailureCode.StaleIdentity; break;
                case NativeControlError.StaleGeneration: code = Common.FailureCode.StaleGeneration; break;
                case NativeControlError.AuthorityRequired: code = Common.FailureCode.AuthorityRequired; break;
                case NativeControlError.GenerationExhausted: code = Common.FailureCode.CapacityExhausted; break;
                default: code = Common.FailureCode.Unavailable; break;
            }
            return new Common.Failure { Code = code, Detail = "Native authority refused: " + error, ObservedContext = context };
        }

        private static Authority.ControlReply Invalid() => new Authority.ControlReply
        {
            Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Control requires a complete supported operation with valid identity, generation and mode/reason.")
        };

        private static bool ExternalReason(Authority.RevocationReason value, out NativeControlRevocationReason reason)
        {
            switch (value)
            {
                case Authority.RevocationReason.Manual: reason = NativeControlRevocationReason.Manual; return true;
                case Authority.RevocationReason.Disconnect: reason = NativeControlRevocationReason.Disconnect; return true;
                case Authority.RevocationReason.Shutdown: reason = NativeControlRevocationReason.Shutdown; return true;
                default: reason = NativeControlRevocationReason.None; return false;
            }
        }
    }
}
