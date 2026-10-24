#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    internal static class NativeClockTools
    {
        // The typed cores below run on the game thread for the in-process facade. A refusal throws.
        internal static Clock.Status HandleReadStatus(Clock.StatusRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) throw NativeFaults.Refused(failure);
            return Read(context);
        }

        internal static Clock.Status HandleRun(Clock.RunRequest parsed)
        {
            if (parsed.ExpectedGeneration is null || parsed.ExpectedGeneration.GetValueOrDefault() == 0)
                throw NativeFaults.Refused(Invalid("Run requires the current authority generation."));
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) throw NativeFaults.Refused(failure);
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                throw NativeFaults.Refused(ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority has not been acquired."));
            var guard = authority.Check(parsed.ExpectedGeneration.GetValueOrDefault());
            context = context with { NativeGeneration = guard.Snapshot.Generation };
            if (!guard.Success) throw NativeFaults.Refused(NativeAuthorityControlTools.Refusal(guard.Error, context));
            var invalid = Supervisor.ValidateTypedRun(parsed); if (invalid != null) throw NativeFaults.Refused(invalid);
            try
            {
                using (authority.Owned()) return Supervisor.TypedRun(parsed, context);
            }
            catch (Exception error) when (error is not BridgeRefusedException)
            {
                ModLog.Error("clock", "Clock run failed: " + error);
                throw NativeFaults.Refused(ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The clock run could not complete; read the status for the actual state: " + error.GetType().Name));
            }
        }

        internal static Clock.Status HandleHold(Clock.HoldRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var failure)) throw NativeFaults.Refused(failure);
            var invalid = Supervisor.ValidateTypedHold(parsed); if (invalid != null) throw NativeFaults.Refused(invalid);
            try { return Supervisor.TypedHold(parsed, context); }
            catch (Exception) { return Read(context); }
        }

        internal static Clock.HeartbeatAck HandleHeartbeat(Clock.HeartbeatRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out _, out var failure)) throw NativeFaults.Refused(failure);
            return Supervisor.TypedHeartbeat();
        }

        internal static Clock.Status Read(Common.ObservationContext context)
        {
            try { return Supervisor.TypedStatus(context); }
            catch (Exception) { return Unreadable(context); }
        }

        private static Clock.Status Unreadable(Common.ObservationContext context) => new Clock.Status { Context = context, Unavailable = new Common.Unavailable
            { Reason = Common.UnavailableReason.ReadFailed, Detail = "Clock status could not be read completely." } };
        private static Common.Failure Invalid(string detail) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, detail);
    }
}
