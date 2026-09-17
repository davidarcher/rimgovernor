#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Clock = RimGovernor.Protocol.Clock;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeClockTools
    {
        [Tool("rimgovernor/clock_read_status", Title = "Read owned clock", Description = "Read actual native pause/speed and canonical owned epoch status without renewing or resuming play.")]
        [ToolResponse("payload", "string", "Official clock StatusReply ProtoJSON.", Always = true)]
        public Task<object> ReadStatus(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock StatusRequest ProtoJSON string.")] object? request = null)
            => Dispatch(ctx, cancellationToken, "rimgovernor/clock_read_status", request, Clock.StatusRequest.Parser,
                failure => new Clock.StatusReply { Failure = failure }, parsed => {
                    if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return new Clock.StatusReply { Failure = failure };
                    return new Clock.StatusReply { Status = Read(context) };
                });

        [Tool("rimgovernor/clock_read_events", Title = "Read owned clock events", Description = "Read immutable event ownership/context using an explicit nonnegative cursor and limit1..128; legacy or incomplete history is unavailable.")]
        [ToolResponse("payload", "string", "Official clock EventsReply ProtoJSON.", Always = true)]
        public Task<object> ReadEvents(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock EventsRequest ProtoJSON string.")] object? request = null)
            => Dispatch(ctx, cancellationToken, "rimgovernor/clock_read_events", request, Clock.EventsRequest.Parser,
                failure => new Clock.EventsReply { Failure = failure }, parsed => {
                    if (!parsed.HasAfterCursor || parsed.AfterCursor < 0 || !parsed.HasLimit || parsed.Limit < 1 || parsed.Limit > 128)
                        return new Clock.EventsReply { Failure = Invalid("Event reads require explicit cursor>=0 and limit1..128.") };
                    if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return new Clock.EventsReply { Failure = failure };
                    return Supervisor.TypedEvents(parsed, context);
                });

        [Tool("rimgovernor/clock_start", Title = "Start guarded owned clock", Description = "Admit one ordinary supervised epoch under current authority, a bounded tick budget and monotonic lease. Exact attempts replay; never reacquires authority.")]
        [ToolResponse("payload", "string", "Official clock ControlReply ProtoJSON.", Always = true)]
        public Task<object> Start(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock StartRequest ProtoJSON string.")] object? request = null)
            => Dispatch(ctx, cancellationToken, "rimgovernor/clock_start", request, Clock.StartRequest.Parser,
                failure => new Clock.ControlReply { Failure = failure }, parsed => Control("Start", parsed, parsed.Authority, null,
                    () => Supervisor.ValidateTypedStart(parsed), context => Supervisor.TypedStart(parsed, context)));

        [Tool("rimgovernor/clock_renew", Title = "Renew owned clock lease", Description = "Renew only a live exact owned epoch under current authority; retain original tick deadline and replay admitted attempts.")]
        [ToolResponse("payload", "string", "Official clock ControlReply ProtoJSON.", Always = true)]
        public Task<object> Renew(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock RenewRequest ProtoJSON string.")] object? request = null)
            => Dispatch(ctx, cancellationToken, "rimgovernor/clock_renew", request, Clock.RenewRequest.Parser,
                failure => new Clock.ControlReply { Failure = failure }, parsed => Control("Renew", parsed, parsed.Authority, parsed.Epoch,
                    () => parsed.HasLeaseMs && parsed.LeaseMs >= 1000 && parsed.LeaseMs <= 30000 ? null : Invalid("Clock lease must be1000..30000ms."),
                    context => Supervisor.TypedRenew(parsed, context)));

        [Tool("rimgovernor/clock_change_speed", Title = "Change owned clock speed", Description = "Change one live owned epoch to an ordinary speed under current authority without extending its tick budget.")]
        [ToolResponse("payload", "string", "Official clock ControlReply ProtoJSON.", Always = true)]
        public Task<object> ChangeSpeed(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock SpeedRequest ProtoJSON string.")] object? request = null)
            => Dispatch(ctx, cancellationToken, "rimgovernor/clock_change_speed", request, Clock.SpeedRequest.Parser,
                failure => new Clock.ControlReply { Failure = failure }, parsed => Control("ChangeSpeed", parsed, parsed.Authority, parsed.Epoch,
                    () => parsed.HasSpeed && Supervisor.OrdinarySpeed(parsed.Speed) ? null : Invalid("Clock speed must be normal, fast or superfast."),
                    context => Supervisor.TypedSpeed(parsed, context)));

        [Tool("rimgovernor/clock_pause", Title = "Pause exact owned clock", Description = "Safe cleanup of the exact current identity/controller/epoch, including after authority revocation. Never pauses a replacement epoch.")]
        [ToolResponse("payload", "string", "Official clock StatusReply ProtoJSON.", Always = true)]
        public Task<object> Pause(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock OwnedRequest ProtoJSON string.")] object? request = null)
            => Dispatch(ctx, cancellationToken, "rimgovernor/clock_pause", request, Clock.OwnedRequest.Parser,
                failure => new Clock.StatusReply { Failure = failure }, parsed => {
                    if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return new Clock.StatusReply { Failure = failure };
                    var refused = Supervisor.ValidateTypedOwner(parsed, false);
                    if (refused != null) return new Clock.StatusReply { Failure = refused };
                    try { Supervisor.TypedPause(context); return new Clock.StatusReply { Status = Read(context) }; }
                    catch (Exception) { return new Clock.StatusReply { Status = Read(context) }; }
                });

        [Tool("rimgovernor/clock_read_attempt", Title = "Read admitted clock attempt", Description = "Read original clock admission after revocation without granting authority; unknown does not prove no effect.")]
        [ToolResponse("payload", "string", "Official clock AttemptReply ProtoJSON.", Always = true)]
        public Task<object> ReadAttempt(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock AttemptRequest ProtoJSON string.")] object? request = null)
            => Dispatch(ctx, cancellationToken, "rimgovernor/clock_read_attempt", request, Clock.AttemptRequest.Parser,
                failure => new Clock.AttemptReply { Failure = failure }, parsed => {
                    if (!ValidAttempt(parsed.Attempt)) return new Clock.AttemptReply { Failure = Invalid("A complete positive attempt key is required.") };
                    if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return new Clock.AttemptReply { Failure = failure };
                    return NativeOperationState.TryGet(context.Identity, out var state) ? state.Ledger.LookupClock(parsed.Attempt, context)
                        : new Clock.AttemptReply { Unknown = new Clock.AttemptUnknown() };
                });

        private static async Task<object> Dispatch<T>(IRimBridgeContext ctx, CancellationToken token, string tool, object? request,
            MessageParser<T> parser, Func<Common.Failure, IMessage> refused, Func<T, IMessage> apply) where T : class, IMessage<T>
        {
            if (!ProtoBoundary.TryParse(ctx, tool, request, parser, out var parsed, out var failure)) return ProtoBoundary.Encode(refused(failure));
            return await ProtoBoundary.OnMainThread(ctx, () => {
                var reply = apply(parsed);
                return ProtoBoundary.Encode(Fits(reply) ? reply : refused(ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted, "Clock read exceeds the bounded reply envelope; no rows were omitted.")));
            }, token).ConfigureAwait(false);
        }
        private static Clock.ControlReply Control(string method, IMessage request, Authority.WritePrecondition? pre, Clock.OwnedRequest? owned,
            Func<Common.Failure?> validate, Func<Common.ObservationContext, Clock.Status> apply)
        {
            if (pre == null || !pre.HasExpectedGeneration || pre.ExpectedGeneration == 0
                || !ValidAttempt(pre.Attempt)) return Refused(Invalid("A complete current authority and attempt precondition is required."));
            if (!ProtoBoundary.ValidateIdentity(pre.Identity, out var context, out var failure)) return Refused(failure);
            var state = NativeOperationState.ForAdmission(context.Identity);
            var fullMethod = "rimgovernor.clock.v1.Clock/" + method;
            var prior = state.Ledger.InspectClock(fullMethod, request);
            if (prior.Kind != NativeAttemptLedger.DecisionKind.New) return prior.DecidedReply;
            if (method != "Start")
            {
                if (owned == null || owned.Identity == null || !owned.Identity.Equals(pre.Identity) || owned.Owner == null
                    || owned.Owner.ControllerSessionId != pre.Attempt.ControllerSessionId) return Refused(Invalid("Epoch and authority must share exact identity and controller."));
                var owner = Supervisor.ValidateTypedOwner(owned, true); if (owner != null) return Refused(owner);
                var grant = Supervisor.ValidateTypedGrant(pre); if (grant != null) return Refused(grant);
            }
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                return Refused(ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority has not been acquired."));
            var guard = authority.Check(pre.ExpectedGeneration);
            context.NativeGeneration = guard.Snapshot.Generation;
            if (!guard.Success) return Refused(NativeAuthorityControlTools.Refusal(guard.Error, context));
            if (method == "Start" && LongEventHandler.AnyEventNowOrWaiting)
                return new Clock.ControlReply { LongEventPending = new Clock.LongEventPending { Detail = "A native long event is pending; no clock attempt was admitted." } };
            var invalid = validate(); if (invalid != null) return Refused(invalid);
            guard = authority.Check(pre.ExpectedGeneration);
            context.NativeGeneration = guard.Snapshot.Generation;
            if (!guard.Success) return Refused(NativeAuthorityControlTools.Refusal(guard.Error, context));
            var admission = state.Ledger.AdmitClock(fullMethod, request, context);
            if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
            try
            {
                Clock.Status status;
                using (authority.Owned()) status = apply(context);
                var candidate = new Clock.ControlReply { Receipt = new Clock.ControlReceipt { Attempt = pre.Attempt.Clone(), AdmittedContext = context.Clone(),
                    Applied = new Clock.AppliedControl { Status = status } } };
                if (!Fits(candidate)) return new Clock.ControlReply { Receipt = state.Ledger.FinishClockUncertain(admission.AdmittedHandle, null,
                    "Clock control was admitted; complete status exceeds the bounded receipt envelope. Inspect current clock status.") };
                return new Clock.ControlReply { Receipt = state.Ledger.FinishClockApplied(admission.AdmittedHandle, status) };
            }
            catch (Exception error)
            {
                return new Clock.ControlReply { Receipt = state.Ledger.FinishClockUncertain(admission.AdmittedHandle, null,
                    "Admitted clock control requires inspection: " + error.GetType().Name) };
            }
        }
        private static Clock.Status Read(Common.ObservationContext context)
        {
            try {
                var status = Supervisor.TypedStatus(context);
                if (!Fits(new Clock.StatusReply { Status = status })) throw new InvalidOperationException("Clock status exceeds bounded envelope");
                return status;
            }
            catch (Exception) { return new Clock.Status { Context = context.Clone(), Unavailable = new Common.Unavailable
                { Reason = Common.UnavailableReason.ReadFailed, Detail = "Clock status could not be read completely." } }; }
        }
        private static Common.Failure Invalid(string detail) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, detail);
        private static bool Fits(IMessage message) => new UTF8Encoding(false, true).GetByteCount(JsonFormatter.Default.Format(message)) <= ProtoBoundary.MaximumEnvelopeBytes;
        private static bool ValidAttempt([NotNullWhen(true)] Common.AttemptKey? attempt) => attempt != null && attempt.HasControllerSessionId
            && ProtoBoundary.IsIdentifier(attempt.ControllerSessionId) && attempt.HasActionId && ProtoBoundary.IsIdentifier(attempt.ActionId)
            && attempt.HasAttemptId && attempt.AttemptId > 0;
        private static Clock.ControlReply Refused(Common.Failure failure) => new Clock.ControlReply { Failure = failure };
    }
}
