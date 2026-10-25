using System;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Host.Sdk;
using Verse;

namespace RimGovernor.Native
{
    internal static class NativeClockTools
    {
        // The typed cores below run on the game thread for the in-process facade. A refusal throws.
        internal static RimGovernor.Brain.GameFacts.Clock.Status HandleReadStatus(RimGovernor.Brain.GameOrders.Clock.StatusRequest parsed)
        {
            if (!GameThread.ValidateIdentity(parsed.Identity, out var context, out var failure)) throw NativeFaults.Refused(failure);
            return Read(context);
        }

        internal static RimGovernor.Brain.GameFacts.Clock.Status HandleRun(RimGovernor.Brain.GameOrders.Clock.RunRequest parsed)
        {
            if (parsed.ExpectedGeneration == 0)
                throw NativeFaults.Refused(Invalid("Run requires the current authority generation."));
            if (!GameThread.ValidateIdentity(parsed.Identity, out var context, out var failure)) throw NativeFaults.Refused(failure);
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                throw NativeFaults.Refused(GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.AuthorityRequired, "Native authority has not been acquired."));
            var guard = authority.Check(parsed.ExpectedGeneration);
            context = context with { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(guard.Snapshot.Generation) };
            if (!guard.Success) throw NativeFaults.Refused(NativeAuthorityControlTools.Refusal(guard.Error, context));
            var invalid = Supervisor.ValidateTypedRun(parsed); if (invalid != null) throw NativeFaults.Refused(invalid);
            try
            {
                using (authority.Owned()) return Supervisor.TypedRun(parsed, context);
            }
            catch (Exception error) when (error is not BridgeRefusedException)
            {
                ModLog.Error("clock", "Clock run failed: " + error);
                throw NativeFaults.Refused(GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "The clock run could not complete; read the status for the actual state: " + error.GetType().Name));
            }
        }

        internal static RimGovernor.Brain.GameFacts.Clock.Status HandleHold(RimGovernor.Brain.GameOrders.Clock.HoldRequest parsed)
        {
            if (!GameThread.ValidateIdentity(parsed.Identity, out var context, out var failure)) throw NativeFaults.Refused(failure);
            var invalid = Supervisor.ValidateTypedHold(parsed); if (invalid != null) throw NativeFaults.Refused(invalid);
            try { return Supervisor.TypedHold(parsed, context); }
            catch (Exception) { return Read(context); }
        }

        internal static RimGovernor.Brain.GameFacts.Clock.HeartbeatAck HandleHeartbeat(RimGovernor.Brain.GameOrders.Clock.HeartbeatRequest parsed)
        {
            if (!GameThread.ValidateIdentity(parsed.Identity, out _, out var failure)) throw NativeFaults.Refused(failure);
            return Supervisor.TypedHeartbeat();
        }

        internal static RimGovernor.Brain.GameFacts.Clock.Status Read(RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            return Supervisor.TypedStatus(context);

        }


        private static RimGovernor.Brain.GameFacts.Common.Failure Invalid(string detail) => GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, detail);
    }
}
