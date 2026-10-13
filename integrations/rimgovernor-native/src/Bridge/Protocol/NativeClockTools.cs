#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeClockTools
    {
        [Tool("rimgovernor/clock_read_status", Title = "Read owned clock", Description = "Read actual native pause/speed and canonical owned epoch status without renewing or resuming play.")]
        [ToolResponse("payload", "string", "Official clock StatusReply ProtoJSON.", Always = true)]
        public Task<object> ReadStatus(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock StatusRequest ProtoJSON string.")] object? request = null)
            => Dispatch<Clock.StatusRequest, Clock.StatusReply>(ctx, cancellationToken, "rimgovernor/clock_read_status", request,
                failure => new Clock.StatusReply { Outcome = new Clock.StatusReplyOutcome.FailureCase(failure) }, HandleReadStatus);

        // The typed cores below run on the game thread; the tools and the in-process facade share them.
        internal static Clock.StatusReply HandleReadStatus(Clock.StatusRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return new Clock.StatusReply { Outcome = failure };
            return new Clock.StatusReply { Outcome = Read(context) };
        }

        [Tool("rimgovernor/clock_read_events", Title = "Read owned clock events", Description = "Read immutable event ownership/context using an explicit nonnegative cursor and limit1..128, never held; advances are announced on the rimgovernor.clock channel; legacy or incomplete history is unavailable.")]
        [ToolResponse("payload", "string", "Official clock EventsReply ProtoJSON.", Always = true)]
        public Task<object> ReadEvents(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock EventsRequest ProtoJSON string.")] object? request = null)
            => Dispatch<Clock.EventsRequest, Clock.EventsReply>(ctx, cancellationToken, "rimgovernor/clock_read_events", request,
                failure => new Clock.EventsReply { Outcome = new Clock.EventsReplyOutcome.FailureCase(failure) }, HandleReadEvents);

        internal static Clock.EventsReply HandleReadEvents(Clock.EventsRequest parsed)
        {
            if (parsed.AfterCursor is null || parsed.AfterCursor.GetValueOrDefault() < 0 || parsed.Limit is null || parsed.Limit.GetValueOrDefault() < 1 || parsed.Limit.GetValueOrDefault() > 128)
                return new Clock.EventsReply { Outcome = Invalid("Event reads require explicit cursor>=0 and limit1..128 (subscribe to rimgovernor.clock).") };
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return new Clock.EventsReply { Outcome = failure };
            return Supervisor.TypedEvents(parsed, context);
        }

        [Tool("rimgovernor/clock_start", Title = "Start guarded owned clock", Description = "Admit one ordinary supervised epoch under current authority, a bounded tick budget and monotonic lease. Exact attempts replay; never reacquires authority.")]
        [ToolResponse("payload", "string", "Official clock ControlReply ProtoJSON.", Always = true)]
        public Task<object> Start(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock StartRequest ProtoJSON string.")] object? request = null)
            => Dispatch<Clock.StartRequest, Clock.ControlReply>(ctx, cancellationToken, "rimgovernor/clock_start", request,
                failure => new Clock.ControlReply { Outcome = new Clock.ControlReplyOutcome.FailureCase(failure) }, HandleStart);

        internal static Clock.ControlReply HandleStart(Clock.StartRequest parsed)
            => Control("Start", parsed, parsed.Authority, null,
                () => Supervisor.ValidateTypedStart(parsed), context => Supervisor.TypedStart(parsed, context));

        [Tool("rimgovernor/clock_renew", Title = "Renew owned clock lease", Description = "Renew only a live exact owned epoch under current authority; retain original tick deadline and replay admitted attempts.")]
        [ToolResponse("payload", "string", "Official clock ControlReply ProtoJSON.", Always = true)]
        public Task<object> Renew(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock RenewRequest ProtoJSON string.")] object? request = null)
            => Dispatch<Clock.RenewRequest, Clock.ControlReply>(ctx, cancellationToken, "rimgovernor/clock_renew", request,
                failure => new Clock.ControlReply { Outcome = new Clock.ControlReplyOutcome.FailureCase(failure) }, HandleRenew);

        internal static Clock.ControlReply HandleRenew(Clock.RenewRequest parsed)
            => Control("Renew", parsed, parsed.Authority, parsed.Epoch,
                () => parsed.LeaseMs is not null && parsed.LeaseMs.GetValueOrDefault() >= 1000 && parsed.LeaseMs.GetValueOrDefault() <= 30000 ? null : Invalid("Clock lease must be1000..30000ms."),
                context => Supervisor.TypedRenew(parsed, context));

        [Tool("rimgovernor/clock_change_speed", Title = "Change owned clock speed", Description = "Change one live owned epoch to an ordinary speed under current authority without extending its tick budget.")]
        [ToolResponse("payload", "string", "Official clock ControlReply ProtoJSON.", Always = true)]
        public Task<object> ChangeSpeed(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock SpeedRequest ProtoJSON string.")] object? request = null)
            => Dispatch<Clock.SpeedRequest, Clock.ControlReply>(ctx, cancellationToken, "rimgovernor/clock_change_speed", request,
                failure => new Clock.ControlReply { Outcome = new Clock.ControlReplyOutcome.FailureCase(failure) }, HandleChangeSpeed);

        internal static Clock.ControlReply HandleChangeSpeed(Clock.SpeedRequest parsed)
            => Control("ChangeSpeed", parsed, parsed.Authority, parsed.Epoch,
                () => parsed.Speed is null || !Supervisor.OrdinarySpeed(parsed.Speed.GetValueOrDefault()) ? Invalid("Clock speed must be normal, fast, superfast or ultrafast.") : Supervisor.ValidateTypedSpeedChange(parsed),
                context => Supervisor.TypedSpeed(parsed, context));

        [Tool("rimgovernor/clock_pause", Title = "Pause exact owned clock", Description = "Safe cleanup of the exact current identity/controller/epoch, including after authority revocation. Never pauses a replacement epoch.")]
        [ToolResponse("payload", "string", "Official clock StatusReply ProtoJSON.", Always = true)]
        public Task<object> Pause(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock OwnedRequest ProtoJSON string.")] object? request = null)
            => Dispatch<Clock.OwnedRequest, Clock.StatusReply>(ctx, cancellationToken, "rimgovernor/clock_pause", request,
                failure => new Clock.StatusReply { Outcome = new Clock.StatusReplyOutcome.FailureCase(failure) }, HandlePause);

        internal static Clock.StatusReply HandlePause(Clock.OwnedRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return new Clock.StatusReply { Outcome = failure };
            var refused = Supervisor.ValidateTypedOwner(parsed, false);
            if (refused != null) return new Clock.StatusReply { Outcome = refused };
            try { Supervisor.TypedPause(context); return new Clock.StatusReply { Outcome = Read(context) }; }
            catch (Exception) { return new Clock.StatusReply { Outcome = Read(context) }; }
        }

        [Tool("rimgovernor/clock_read_attempt", Title = "Read admitted clock attempt", Description = "Read original clock admission after revocation without granting authority; unknown does not prove no effect.")]
        [ToolResponse("payload", "string", "Official clock AttemptReply ProtoJSON.", Always = true)]
        public Task<object> ReadAttempt(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock AttemptRequest ProtoJSON string.")] object? request = null)
            => Dispatch<Clock.AttemptRequest, Clock.AttemptReply>(ctx, cancellationToken, "rimgovernor/clock_read_attempt", request,
                failure => new Clock.AttemptReply { Outcome = new Clock.AttemptReplyOutcome.FailureCase(failure) }, HandleReadAttempt);

        internal static Clock.AttemptReply HandleReadAttempt(Clock.AttemptRequest parsed)
        {
            if (!ValidAttempt(parsed.Attempt)) return new Clock.AttemptReply { Outcome = Invalid("A complete positive attempt key is required.") };
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return new Clock.AttemptReply { Outcome = failure };
            return NativeOperationState.TryGet(context.Identity ?? new Common.Identity(), out var state) ? state.Ledger.LookupClock(parsed.Attempt, context)
                : new Clock.AttemptReply { Outcome = new Clock.AttemptUnknown() };
        }

        private static async Task<object> Dispatch<T, TReply>(IRimBridgeContext ctx, CancellationToken token, string tool, object? request,
            Func<Common.Failure, TReply> refused, Func<T, TReply> apply) where T : class where TReply : class
        {
            if (!ProtoBoundary.TryParse<T>(ctx, tool, request, out var parsed, out var failure)) return ProtoBoundary.Encode(refused(failure));
            return await ProtoBoundary.OnMainThread(ctx, () => {
                return ProtoBoundary.Encode(apply(parsed));
            }, token).ConfigureAwait(false);
        }
        private static Clock.ControlReply Control(string method, object request, Authority.WritePrecondition? pre, Clock.OwnedRequest? owned,
            Func<Common.Failure?> validate, Func<Common.ObservationContext, Clock.Status> apply)
        {
            if (pre == null || pre.ExpectedGeneration is null || pre.ExpectedGeneration.GetValueOrDefault() == 0
                || !ValidAttempt(pre.Attempt)) return Refused(Invalid("A complete current authority and attempt precondition is required."));
            if (!ProtoBoundary.ValidateIdentity(pre.Identity, out var context, out var failure)) return Refused(failure);
            var state = NativeOperationState.ForAdmission(context.Identity ?? new Common.Identity());
            var fullMethod = "rimgovernor.clock.v1.Clock/" + method;
            var prior = state.Ledger.InspectClock(fullMethod, request);
            if (prior.Kind != NativeAttemptLedger.DecisionKind.New) return prior.DecidedReply;
            if (method != "Start")
            {
                if (owned == null || owned.Identity == null || !owned.Identity.Equals(pre.Identity) || owned.Owner == null
                    || (owned.Owner.ControllerSessionId ?? "") != (pre.Attempt.ControllerSessionId ?? "")) return Refused(Invalid("Epoch and authority must share exact identity and controller."));
                var owner = Supervisor.ValidateTypedOwner(owned, true); if (owner != null) return Refused(owner);
                var grant = Supervisor.ValidateTypedGrant(pre); if (grant != null) return Refused(grant);
            }
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                return Refused(ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority has not been acquired."));
            var guard = authority.Check(pre.ExpectedGeneration.GetValueOrDefault());
            context = context with { NativeGeneration = guard.Snapshot.Generation };
            if (!guard.Success) return Refused(NativeAuthorityControlTools.Refusal(guard.Error, context));
            if (method == "Start" && LongEventHandler.AnyEventNowOrWaiting)
                return new Clock.ControlReply { Outcome = new Clock.LongEventPending { Detail = "A native long event is pending; no clock attempt was admitted." } };
            var invalid = validate(); if (invalid != null) return Refused(invalid);
            guard = authority.Check(pre.ExpectedGeneration.GetValueOrDefault());
            context = context with { NativeGeneration = guard.Snapshot.Generation };
            if (!guard.Success) return Refused(NativeAuthorityControlTools.Refusal(guard.Error, context));
            var admission = state.Ledger.AdmitClock(fullMethod, request, context);
            if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
            try
            {
                Clock.Status status;
                using (authority.Owned()) status = apply(context);
                return new Clock.ControlReply { Outcome = state.Ledger.FinishClockApplied(admission.AdmittedHandle, status) };
            }
            catch (Exception error)
            {
                return new Clock.ControlReply { Outcome = state.Ledger.FinishClockUncertain(admission.AdmittedHandle, null,
                    "Admitted clock control requires inspection: " + error.GetType().Name) };
            }
        }
        internal static Clock.Status Read(Common.ObservationContext context)
        {
            try { return Supervisor.TypedStatus(context); }
            catch (Exception) { return Unreadable(context); }
        }

        private static Clock.Status Unreadable(Common.ObservationContext context) => new Clock.Status { Context = context, State = new Common.Unavailable
            { Reason = Common.UnavailableReason.ReadFailed, Detail = "Clock status could not be read completely." } };
        private static Common.Failure Invalid(string detail) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, detail);
        private static bool ValidAttempt([NotNullWhen(true)] Common.AttemptKey? attempt) => attempt != null && attempt.ControllerSessionId is not null
            && ProtoBoundary.IsIdentifier(attempt.ControllerSessionId) && attempt.ActionId is not null && ProtoBoundary.IsIdentifier(attempt.ActionId)
            && attempt.AttemptId is not null && attempt.AttemptId.GetValueOrDefault() > 0;
        private static Clock.ControlReply Refused(Common.Failure failure) => new Clock.ControlReply { Outcome = failure };
    }
}
