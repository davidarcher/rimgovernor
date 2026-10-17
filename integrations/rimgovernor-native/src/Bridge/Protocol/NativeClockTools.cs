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
    internal static class NativeClockTools
    {
        // The typed cores below run on the game thread; the tools and the in-process facade share them.
        internal static Clock.StatusReply HandleReadStatus(Clock.StatusRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return new Clock.StatusReply { Outcome = failure };
            return new Clock.StatusReply { Outcome = Read(context) };
        }

        internal static Clock.EventsReply HandleReadEvents(Clock.EventsRequest parsed)
        {
            if (parsed.AfterCursor is null || parsed.AfterCursor.GetValueOrDefault() < 0 || parsed.Limit is null || parsed.Limit.GetValueOrDefault() < 1 || parsed.Limit.GetValueOrDefault() > 128)
                return new Clock.EventsReply { Outcome = Invalid("Event reads require explicit cursor>=0 and limit1..128 (subscribe to rimgovernor.clock).") };
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return new Clock.EventsReply { Outcome = failure };
            return Supervisor.TypedEvents(parsed, context);
        }

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

        internal static Clock.StatusReply HandleHold(Clock.HoldRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) return Failed(failure);
            var invalid = Supervisor.ValidateTypedHold(parsed); if (invalid != null) return Failed(invalid);
            try { return new Clock.StatusReply { Outcome = Supervisor.TypedHold(parsed, context) }; }
            catch (Exception) { return new Clock.StatusReply { Outcome = Read(context) }; }
        }

        internal static Clock.HeartbeatReply HandleHeartbeat(Clock.HeartbeatRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out _, out var failure)) return new Clock.HeartbeatReply { Outcome = failure };
            return new Clock.HeartbeatReply { Outcome = Supervisor.TypedHeartbeat() };
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
