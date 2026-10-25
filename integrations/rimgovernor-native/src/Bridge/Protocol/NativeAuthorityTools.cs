using System;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.GameAccess;
using Verse;

namespace RimGovernor.Native
{
    internal static class NativeAuthorityTools
    {

        // On the game thread: the status read of the in-process facade. A refusal throws.
        internal static RimGovernor.Brain.GameFacts.Authority.Status HandleReadStatus(RimGovernor.Brain.GameOrders.Authority.StatusRequest parsed)
        {
            // The game this identity names has exited or been unloaded: its
            // retained final authority is the only thing left to report,
            // and is how a controller sees REVOCATION_REASON_SHUTDOWN.
            if (Current.Game == null && GameThread.Complete(parsed.Identity)
                && NativeControlAuthority.LastShutdown is NativeControlShutdown shutdown
                && shutdown.Matches(parsed.Identity.ColonyId, parsed.Identity.LoadToken, parsed.Identity.MapId))
                return ProjectShutdown(shutdown, parsed.Identity);
            if (!GameThread.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                throw NativeFaults.Refused(invalid);
            var game = Current.Game;
            if (game == null)
                throw NativeFaults.Refused(new RimGovernor.Brain.GameFacts.Common.Failure { RefusalClass = RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified, Code = RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, Detail = "No game is loaded." });
            if (!NativeControlAuthority.TryGetForGame(game, out var state) || state == null)
                return new RimGovernor.Brain.GameFacts.Authority.Status
                {
                    Context = context,
                    State = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotObserved,
                        Detail = "Native control hooks and trusted admission have not been initialized." }
                };
            return Project(state.Status(), context);
        }

        // This projection does not initialize hooks or grant authority.
        internal static RimGovernor.Brain.GameFacts.Authority.Status Project(NativeControlSnapshot snapshot, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var status = new RimGovernor.Brain.GameFacts.Authority.Status { Context = context };
            status = status with { Context = context with { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(snapshot.Generation) } };
            if (!snapshot.Available)
            {
                status = status with { State = new RimGovernor.Brain.GameFacts.Authority.StatusState.UnavailableCase(Unavailable(snapshot)) };
                return status;
            }
            if (snapshot.Active)
            {
                status = status with { State = new RimGovernor.Brain.GameFacts.Authority.StatusState.ActiveCase(new RimGovernor.Brain.GameFacts.Authority.ActiveAuthority { Mode = RimGovernor.Brain.GameFacts.Authority.Mode.Auto }) };
                return status;
            }
            if (TryReason(snapshot.Reason, out var reason))
                status = status with { State = new RimGovernor.Brain.GameFacts.Authority.StatusState.InactiveCase(new RimGovernor.Brain.GameFacts.Authority.InactiveAuthority { Reason = reason }) };
            else
                status = status with { State = new RimGovernor.Brain.GameFacts.Authority.StatusState.UnavailableCase(new RimGovernor.Brain.GameFacts.Common.Unavailable
                {
                    Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                    Detail = "Native authority status could not be represented."
                }) };
            return status;
        }

        // The retained state of an exited or unloaded game: inactive with its
        // final reason at its final generation, anchored to the tick it ended on.
        internal static RimGovernor.Brain.GameFacts.Authority.Status ProjectShutdown(NativeControlShutdown shutdown, RimGovernor.Brain.GameFacts.Common.Identity identity)
        {
            var status = new RimGovernor.Brain.GameFacts.Authority.Status { Context = new RimGovernor.Brain.GameFacts.Common.ObservationContext
            {
                Identity = identity, Tick = shutdown.Tick, NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(shutdown.Generation),
            } };
            if (TryReason(shutdown.Reason, out var reason))
                status = status with { State = new RimGovernor.Brain.GameFacts.Authority.StatusState.InactiveCase(new RimGovernor.Brain.GameFacts.Authority.InactiveAuthority { Reason = reason }) };
            else
                status = status with { State = new RimGovernor.Brain.GameFacts.Authority.StatusState.UnavailableCase(new RimGovernor.Brain.GameFacts.Common.Unavailable
                {
                    Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                    Detail = "Native authority status could not be represented."
                }) };
            return status;
        }

        private static RimGovernor.Brain.GameFacts.Common.Unavailable Unavailable(NativeControlSnapshot snapshot)
        {
            if (snapshot.Identity == null)
                return new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded, Detail = "No current native authority context is loaded." };
            switch (snapshot.Reason)
            {
                case NativeControlRevocationReason.GenerationExhausted:
                    return new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.LimitExceeded, Detail = "Native authority generation is exhausted." };
                case NativeControlRevocationReason.ClockUnavailable:
                    return new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed, Detail = "The native monotonic authority clock is unavailable." };
                default:
                    return new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotObserved, Detail = "Native control hooks and trusted admission have not been verified." };
            }
        }

        // ClockUnavailable is internal-only and unreachable while Available (a clock
        // fault permanently clears Available); it maps to the wire's generic
        // Unavailable=10 value, which the proto documents as covering exactly this
        // kind of terminal "not active, not a detected player action" reason.
        private static bool TryReason(NativeControlRevocationReason source, out RimGovernor.Brain.GameFacts.Authority.RevocationReason result)
        {
            switch (source)
            {
                case NativeControlRevocationReason.None: result = RimGovernor.Brain.GameFacts.Authority.RevocationReason.None; return true;
                case NativeControlRevocationReason.Manual: result = RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual; return true;
                case NativeControlRevocationReason.ExternalOrder: result = RimGovernor.Brain.GameFacts.Authority.RevocationReason.ExternalOrder; return true;
                case NativeControlRevocationReason.PlayerControl: result = RimGovernor.Brain.GameFacts.Authority.RevocationReason.PlayerControl; return true;
                case NativeControlRevocationReason.IdentityChanged: result = RimGovernor.Brain.GameFacts.Authority.RevocationReason.IdentityChanged; return true;
                case NativeControlRevocationReason.Disconnect: result = RimGovernor.Brain.GameFacts.Authority.RevocationReason.Disconnect; return true;
                case NativeControlRevocationReason.Shutdown: result = RimGovernor.Brain.GameFacts.Authority.RevocationReason.Shutdown; return true;
                case NativeControlRevocationReason.HooksUnavailable: result = RimGovernor.Brain.GameFacts.Authority.RevocationReason.HooksUnavailable; return true;
                case NativeControlRevocationReason.GenerationExhausted: result = RimGovernor.Brain.GameFacts.Authority.RevocationReason.GenerationExhausted; return true;
                case NativeControlRevocationReason.ClockUnavailable: result = RimGovernor.Brain.GameFacts.Authority.RevocationReason.Unavailable; return true;
                default: result = RimGovernor.Brain.GameFacts.Authority.RevocationReason.None; return false;
            }
        }
    }
}
