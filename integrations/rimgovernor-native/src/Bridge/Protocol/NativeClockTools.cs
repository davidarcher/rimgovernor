#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeClockTools
    {
        [Tool("rimgovernor/clock_read_status", Title = "Read clock", Description = "Read the native clock: running at the target speed or held for a named reason, with actual pause/speed and the one ceiling. A read is not brain contact; heartbeat is.")]
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

        [Tool("rimgovernor/clock_read_events", Title = "Read clock events", Description = "Read immutable clock events using an explicit nonnegative cursor and limit1..128, never held; advances are announced on the rimgovernor.clock channel; legacy or incomplete history is unavailable.")]
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

        [Tool("rimgovernor/clock_run", Title = "Run the clock", Description = "Run the clock at the target speed under the watch policy: resumes a held clock, or changes speed and policy in place while running. Idempotent. Needs the current authority generation; refused while a long event or force-pausing window holds the game.")]
        [ToolResponse("payload", "string", "Official clock StatusReply ProtoJSON.", Always = true)]
        public Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock RunRequest ProtoJSON string.")] object? request = null)
            => Dispatch<Clock.RunRequest, Clock.StatusReply>(ctx, cancellationToken, "rimgovernor/clock_run", request,
                failure => new Clock.StatusReply { Outcome = new Clock.StatusReplyOutcome.FailureCase(failure) }, HandleRun);

        internal static Clock.StatusReply HandleRun(Clock.RunRequest parsed)
        {
            if (parsed.ExpectedGeneration is null || parsed.ExpectedGeneration.GetValueOrDefault() == 0)
                return Failed(Invalid("Run requires the current authority generation."));
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return Failed(failure);
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                return Failed(ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority has not been acquired."));
            var guard = authority.Check(parsed.ExpectedGeneration.GetValueOrDefault());
            context = context with { NativeGeneration = guard.Snapshot.Generation };
            if (!guard.Success) return Failed(NativeAuthorityControlTools.Refusal(guard.Error, context));
            var invalid = Supervisor.ValidateTypedRun(parsed); if (invalid != null) return Failed(invalid);
            try
            {
                using (authority.Owned()) return Supervisor.TypedRun(parsed, context);
            }
            catch (Exception error)
            {
                ModLog.Error("clock", "Clock run failed: " + error);
                return Failed(ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The clock run could not complete; read the status for the actual state: " + error.GetType().Name));
            }
        }

        [Tool("rimgovernor/clock_hold", Title = "Hold the clock", Description = "Hold the clock for a brain reason (coupled order, review, autosave, game forced): pauses and journals one HoldEntered. While held the first reason stands. Needs only the loaded identity, so cleanup can always stop play.")]
        [ToolResponse("payload", "string", "Official clock StatusReply ProtoJSON.", Always = true)]
        public Task<object> Hold(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock HoldRequest ProtoJSON string.")] object? request = null)
            => Dispatch<Clock.HoldRequest, Clock.StatusReply>(ctx, cancellationToken, "rimgovernor/clock_hold", request,
                failure => new Clock.StatusReply { Outcome = new Clock.StatusReplyOutcome.FailureCase(failure) }, HandleHold);

        internal static Clock.StatusReply HandleHold(Clock.HoldRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return Failed(failure);
            var invalid = Supervisor.ValidateTypedHold(parsed); if (invalid != null) return Failed(invalid);
            try { return new Clock.StatusReply { Outcome = Supervisor.TypedHold(parsed, context) }; }
            catch (Exception) { return new Clock.StatusReply { Outcome = Read(context) }; }
        }

        [Tool("rimgovernor/clock_heartbeat", Title = "Clock heartbeat", Description = "The brain's contact for the one ceiling. Not journalled, not a hold; silence only lowers the ceiling to Normal. Replies with the clock state, hold reason and ceiling.")]
        [ToolResponse("payload", "string", "Official clock HeartbeatReply ProtoJSON.", Always = true)]
        public Task<object> Heartbeat(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official clock HeartbeatRequest ProtoJSON string.")] object? request = null)
            => Dispatch<Clock.HeartbeatRequest, Clock.HeartbeatReply>(ctx, cancellationToken, "rimgovernor/clock_heartbeat", request,
                failure => new Clock.HeartbeatReply { Outcome = new Clock.HeartbeatReplyOutcome.FailureCase(failure) }, HandleHeartbeat);

        internal static Clock.HeartbeatReply HandleHeartbeat(Clock.HeartbeatRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out _, out var failure)) return new Clock.HeartbeatReply { Outcome = failure };
            return new Clock.HeartbeatReply { Outcome = Supervisor.TypedHeartbeat() };
        }

        private static async Task<object> Dispatch<T, TReply>(IRimBridgeContext ctx, CancellationToken token, string tool, object? request,
            Func<Common.Failure, TReply> refused, Func<T, TReply> apply) where T : class where TReply : class
        {
            if (!ProtoBoundary.TryParse<T>(ctx, tool, request, out var parsed, out var failure)) return ProtoBoundary.Encode(refused(failure));
            return await ProtoBoundary.OnMainThread(ctx, () => {
                return ProtoBoundary.Encode(apply(parsed));
            }, token).ConfigureAwait(false);
        }
        internal static Clock.Status Read(Common.ObservationContext context)
        {
            try { return Supervisor.TypedStatus(context); }
            catch (Exception) { return Unreadable(context); }
        }

        private static Clock.Status Unreadable(Common.ObservationContext context) => new Clock.Status { Context = context, Unavailable = new Common.Unavailable
            { Reason = Common.UnavailableReason.ReadFailed, Detail = "Clock status could not be read completely." } };
        private static Common.Failure Invalid(string detail) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, detail);
        private static Clock.StatusReply Failed(Common.Failure failure) => new Clock.StatusReply { Outcome = failure };
    }
}
