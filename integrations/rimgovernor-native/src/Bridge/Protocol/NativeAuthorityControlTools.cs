#nullable enable
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Authority = RimGovernor.Contracts.Wire.Authority;

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
            [ToolParameter(Description = "Official ProtoJSON ControlRequest. Never exposed to model dispatch.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Authority.ControlRequest>(ctx, ToolName, request, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Authority.ControlReply { Outcome = new Authority.ControlReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => Apply(parsed), cancellationToken).ConfigureAwait(false);
        }

        internal static Authority.ControlReply Apply(Authority.ControlRequest request)
        {
            if (request.Operation is Authority.ControlRequestOperation.FocusMapCase { Value: var focus })
                return Focus(focus);
            Common.Identity? identity;
            Authority.SetMode? setMode = null;
            Authority.Revoke? revoke = null;
            switch (request.Operation)
            {
                case Authority.ControlRequestOperation.SetModeCase { Value: var sm }:
                    setMode = sm;
                    if (sm.ExpectedGeneration is null || sm.ExpectedGeneration == 0 || sm.Mode is null
                        || (sm.Mode != Authority.Mode.Auto && sm.Mode != Authority.Mode.Manual)) return Invalid();
                    identity = sm.Identity;
                    break;
                case Authority.ControlRequestOperation.RevokeCase { Value: var rv }:
                    revoke = rv;
                    if (rv.ExpectedGeneration is null || rv.ExpectedGeneration == 0 || rv.Reason is null
                        || !ExternalReason(rv.Reason.GetValueOrDefault(), out _)) return Invalid();
                    identity = rv.Identity;
                    break;
                default: return Invalid();
            }
            if (identity is null) return Invalid();
            if (!ProtoBoundary.ValidateIdentity(identity, out var context, out var failure))
                return new Authority.ControlReply { Outcome = failure };
            bool settingAuto = setMode is not null && setMode.Mode == Authority.Mode.Auto;
            if (settingAuto)
                NativeAuthorityHooks.InitializeForCurrentGame();
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var state) || state == null)
                return new Authority.ControlReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.Unavailable,
                    "Native authority hooks are not initialized.") };
            NativeControlResult result;
            if (setMode is not null)
            {
                var mode = setMode.Mode == Authority.Mode.Auto ? NativeControlMode.Auto : NativeControlMode.Manual;
                result = state.SetMode(setMode.ExpectedGeneration.GetValueOrDefault(), mode);
            }
            else
            {
                NativeControlRevocationReason reason;
                ExternalReason(revoke!.Reason.GetValueOrDefault(), out reason);
                result = state.Revoke(revoke.ExpectedGeneration.GetValueOrDefault(), reason);
            }            context = context with { NativeGeneration = result.Snapshot.Generation };
            if (!result.Success)
                return new Authority.ControlReply { Outcome = Refusal(result.Error, context) };
            if (revoke is not null)
                return new Authority.ControlReply { Outcome = new Authority.Revoked
                {
                    Context = context,
                    Authority = new Authority.InactiveAuthority { Reason = revoke!.Reason }
                } };
            if (!settingAuto)
            {
                var projectedInactive = NativeAuthorityTools.Project(result.Snapshot, context);
                return new Authority.ControlReply { Outcome = new Authority.Revoked
                {
                    Context = context,
                    Authority = projectedInactive.State?.Inactive ?? new Authority.InactiveAuthority { Reason = Authority.RevocationReason.Manual }
                } };
            }
            if (result.Snapshot.Active) NativeAuthorityHooks.EnsureManualPriorities(Current.Game);
            var projected = NativeAuthorityTools.Project(result.Snapshot, context);
            return new Authority.ControlReply { Outcome = new Authority.Granted
            {
                Context = context, Authority = projected.State?.Active
            } };
        }

        private static Authority.ControlReply Focus(Authority.FocusMap request)
        {
            if (request.ExpectedGeneration is null || request.ExpectedGeneration.GetValueOrDefault() == 0 || request.Target == null
                || request.Identity == null || (request.Target.ColonyId ?? "") != (request.Identity.ColonyId ?? "")
                || (request.Target.LoadToken ?? "") != (request.Identity.LoadToken ?? "")) return Invalid();
            if (!ProtoBoundary.ValidateViewedIdentity(request.Identity, out var before, out var failure))
                return new Authority.ControlReply { Outcome = failure };
            if (!ProtoBoundary.ValidateIdentity(request.Target, out var map, out var context, out failure))
                return new Authority.ControlReply { Outcome = failure };
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var state) || state == null)
                return new Authority.ControlReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native authority unavailable.") };
            var snapshot = state.Status();
            if (snapshot.Generation != request.ExpectedGeneration.GetValueOrDefault())
                return new Authority.ControlReply { Outcome = Refusal(NativeControlError.StaleGeneration, before) };
            if (!snapshot.Active)
                return new Authority.ControlReply { Outcome = Refusal(NativeControlError.AuthorityRequired, before) };
            Current.Game.CurrentMap = map;
            context = context with { NativeGeneration = state.Status().Generation };
            SnapshotStream.Open(new RimGovernor.Contracts.Wire.Observations.SnapshotStreamRequest { Keyframe = true });
            return new Authority.ControlReply { Outcome = new Authority.FocusedMap { Context = context } };
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
            Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Control requires a complete supported operation with valid identity, generation and mode/reason.")
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
