using Common = RimGovernor.Contracts.Wire.Common;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

// #3100: the wire replies are records whose oneof lives on a nested abstract `Outcome`/`State`/`Operation`
// record. The probes read replies as flat properties (`reply.Failure`, `reply.Status`, `status.Running`);
// these extension properties keep that reading shape, each returning null when another case is set, exactly
// like the protobuf oneof accessors they replace.
internal static class WireProbeAccessors
{
    extension(Clock.StatusReply r)
    {
        public Clock.Status Status => r.Outcome?.Status;
        public Common.Failure Failure => r.Outcome?.Failure;
    }
    extension(Clock.ControlReply r)
    {
        public Clock.ControlReceipt Receipt => r.Outcome?.Receipt;
        public Common.Failure Failure => r.Outcome?.Failure;
        public Clock.LongEventPending LongEventPending => r.Outcome?.LongEventPending;
    }
    extension(Clock.AttemptReply r)
    {
        public Clock.ControlReceipt Receipt => r.Outcome?.Receipt;
        public Common.Failure Failure => r.Outcome?.Failure;
        public Clock.AttemptUnknown Unknown => r.Outcome?.Unknown;
    }
    extension(Clock.EventsReply r)
    {
        public Clock.EventsPage Page => r.Outcome?.Page;
        public Common.Failure Failure => r.Outcome?.Failure;
    }
    extension(Clock.ControlReceipt r)
    {
        public Clock.AppliedControl Applied => r.Outcome?.Applied;
        public Clock.UncertainControl Uncertain => r.Outcome?.Uncertain;
    }
    extension(Clock.Status s)
    {
        public Clock.Running Running => s.State?.Running;
        public Clock.Stopping Stopping => s.State?.Stopping;
        public Clock.Stopped Stopped => s.State?.Stopped;
        public Clock.NeverStarted NeverStarted => s.State?.NeverStarted;
        public Common.Unavailable Unavailable => s.State?.Unavailable;
    }
    extension(Clock.ClockEvent e)
    {
        public Clock.EpochStarted Started => e.Event?.Started;
        public Clock.SpeedChanged SpeedChanged => e.Event?.SpeedChanged;
        public Clock.StopEvent Stopped => e.Event?.Stopped;
        public Clock.Notification Notification => e.Event?.Notification;
        public Clock.Alert Alert => e.Event?.Alert;
        public Clock.Injury InjuryObserved => e.Event?.InjuryObserved;
        public Clock.HostilesCleared HostilesCleared => e.Event?.HostilesCleared;
        public Clock.PauseFailed PauseFailed => e.Event?.PauseFailed;
        public Clock.ForcePauseWaiting ForcePauseWaiting => e.Event?.ForcePauseWaiting;
        public Clock.ForcePauseCleared ForcePauseCleared => e.Event?.ForcePauseCleared;
        public Clock.OperationOutcome OperationOutcome => e.Event?.OperationOutcome;
        public Clock.AuthorityChanged AuthorityChanged => e.Event?.AuthorityChanged;
        public Clock.ObservationInvalidated ObservationInvalidated => e.Event?.ObservationInvalidated;
        public Clock.RuleFired RuleFired => e.Event?.RuleFired;
        public Clock.RuleLeaseExpired RuleLeaseExpired => e.Event?.RuleLeaseExpired;
    }
    extension(Clock.Notification n)
    {
        public Clock.Letter Letter => n.Source?.Letter;
        public Clock.TransientMessage Message => n.Source?.Message;
    }
    extension(Clock.StopEvent e)
    {
        public Clock.NotificationBatch Notifications => e.Evidence?.Notifications;
        public Clock.PawnEvent Pawn => e.Evidence?.Pawn;
        public Clock.Injury Injury => e.Evidence?.Injury;
        public Clock.HealthThreshold Health => e.Evidence?.Health;
        public Clock.BudgetReached Budget => e.Evidence?.Budget;
        public Clock.PauseEvidence Pause => e.Evidence?.Pause;
        public Common.Unavailable Unavailable => e.Evidence?.Unavailable;
        public Clock.WatchLatched Watch => e.Evidence?.Watch;
        public Clock.CombatEventStop Combat => e.Evidence?.Combat;
    }
    extension(Receipts.LookupReply r)
    {
        public Receipts.Receipt Receipt => r.Outcome?.Receipt;
        public Receipts.InFlight InFlight => r.Outcome?.InFlight;
        public Receipts.UnknownAttempt Unknown => r.Outcome?.Unknown;
        public Common.Failure Failure => r.Outcome?.Failure;
    }
    extension(Authority.ControlRequest r)
    {
        public Authority.SetMode SetMode => r.Operation?.SetMode;
        public Authority.Revoke Revoke => r.Operation?.Revoke;
        public Authority.FocusMap FocusMap => r.Operation?.FocusMap;
    }
    extension(Authority.StatusReply r)
    {
        public Authority.Status Status => r.Outcome?.Status;
        public Common.Failure Failure => r.Outcome?.Failure;
    }
    extension(Authority.ControlReply r)
    {
        public Authority.Granted Granted => r.Outcome?.Granted;
        public Authority.Revoked Revoked => r.Outcome?.Revoked;
        public Authority.FocusedMap FocusedMap => r.Outcome?.FocusedMap;
        public Common.Failure Failure => r.Outcome?.Failure;
    }
    extension(Authority.Status s)
    {
        public Common.Unavailable Unavailable => s.State?.Unavailable;
        public Authority.InactiveAuthority Inactive => s.State?.Inactive;
        public Authority.ActiveAuthority Active => s.State?.Active;
    }
}
