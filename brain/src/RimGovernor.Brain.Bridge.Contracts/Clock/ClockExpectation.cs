using RimGovernor.Protocol.Clock;
using RimGovernor.Protocol.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go clock_expectation.go, clock_epoch_evidence.go and clock_hazard.go.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>ValidateClockExpectation</c>: checks immutable admission evidence, not live permission.</summary>
    public static void ValidateClockExpectation(ClockExpectation e)
    {
        ClockWire(e.Identity);
        ClockWire(e.Attempt);
        ValidateIdentity(e.Identity);
        BuildingAttempt(e.Attempt);
        if (e.NativeGeneration == 0)
        {
            throw Contract("clock expectation admission mismatch");
        }
        var command = e.Command;
        var arms = (command.Start is not null ? 1 : 0) + (command.Renew is not null ? 1 : 0) + (command.Speed is not null ? 1 : 0);
        if (arms != 1)
        {
            throw Contract("one clock command required");
        }
        if (command.Start is { } start)
        {
            if ((int)start.Speed < 1 || (int)start.Speed > 4 || start.MaxTicks < 1 || start.MaxTicks > 1800000)
            {
                throw Contract("clock start speed or tick budget");
            }
            if (start.TestAcceleration && start.Speed != Speed.Ultrafast)
            {
                throw Contract("clock test acceleration requires ultrafast");
            }
            if (start.BlindTickBudget > 1800000 || start.MaxTicksPerSecond > 60000)
            {
                throw Contract("clock blind tick budget or tick rate ceiling");
            }
            ClockPacing(start.WirePacing ?? Pacing.Unspecified, start.Speed, start.TestAcceleration);
            AuthorityDuration(start.LeaseMs);
            ClockPolicy(start.Policy, start.MaxTicks);
            return;
        }
        Epoch? original = null;
        if (command.Renew is { } renew)
        {
            original = renew.Original;
            AuthorityDuration(renew.LeaseMs);
        }
        if (command.Speed is { } speed)
        {
            original = speed.Original;
            if ((int)speed.Speed < 1 || (int)speed.Speed > 4)
            {
                throw Contract("clock ordinary speed required");
            }
            if ((speed.Original?.TestAcceleration ?? false) && speed.Speed != Speed.Ultrafast)
            {
                throw Contract("clock accelerated epoch cannot change speed");
            }
            if (speed.MaxTicksPerSecond is { } ceiling && (ceiling < 1 || ceiling > 60000))
            {
                throw Contract("clock tick rate ceiling");
            }
        }
        ClockEpoch(original);
        if (original!.Origin.NativeGeneration != e.NativeGeneration || !SameIdentity(original.Origin.Identity, e.Identity))
        {
            throw Contract("clock original epoch mismatch");
        }
    }

    /// <summary>
    /// Go's <c>ValidateClockReceipt</c>: correlates recovered or direct evidence to its full original command. A valid
    /// uncertain receipt remains uncertain; an applied receipt may already describe a stopped epoch and never grants
    /// live permission.
    /// </summary>
    public static void ValidateClockReceipt(ControlReceipt? r, ClockExpectation e)
    {
        ValidateClockExpectation(e);
        ClockReceipt(r, e.Identity, e.Attempt, e.NativeGeneration);
        if (r!.OutcomeCase == ControlReceipt.OutcomeOneofCase.Uncertain)
        {
            return;
        }
        var actual = ClockStatusEpoch(r.Applied.Status);
        if (e.Command.Start is { } start)
        {
            if (actual is null
                || !Equals(actual.Origin, r.AdmittedContext)
                || actual.TickDeadline - actual.StartTick != start.MaxTicks
                || actual.RequestedSpeed != start.Speed
                || actual.TestAcceleration != start.TestAcceleration
                || !Equals(actual.Policy, start.Policy)
                || actual.LeaseRemainingMs > start.LeaseMs
                || actual.BlindTickBudget != start.BlindTickBudget
                || actual.MaxTicksPerSecond != start.MaxTicksPerSecond
                || (actual.Pacing == Pacing.PlayerAccelerated) != start.PlayerAccelerated)
            {
                throw Contract("clock start epoch mismatch");
            }
            return;
        }
        if (e.Command.Renew is { } renew)
        {
            ClockSameEpoch(actual, renew.Original, renew.Original?.RequestedSpeed ?? Speed.Unspecified);
            if (actual!.LeaseRemainingMs > renew.LeaseMs)
            {
                throw Contract("clock renewal lease exceeds request");
            }
            return;
        }
        var change = e.Command.Speed!;
        ClockSameEpoch(actual, change.Original, change.Speed);
        if (change.MaxTicksPerSecond is { } ceiling && actual!.MaxTicksPerSecond != ceiling)
        {
            throw Contract("clock tick rate ceiling mismatch");
        }
    }

    /// <summary>
    /// Go's <c>ValidateClockControlReply</c>: accepts valid outcome evidence without interpreting it as permission,
    /// completion, or proof that a conflicting attempt had no effect.
    /// </summary>
    public static void ValidateClockControlReply(ControlReply? reply, ClockExpectation e)
    {
        ValidateClockExpectation(e);
        ClockWire(reply);
        switch (reply!.OutcomeCase)
        {
            case ControlReply.OutcomeOneofCase.Receipt:
                ValidateClockReceipt(reply.Receipt, e);
                return;
            case ControlReply.OutcomeOneofCase.Failure:
                // A valid failure is typed refusal evidence (Go's NativeFailure is accepted); a malformed one is a
                // contract error.
                ValidateFailure(reply.Failure);
                return;
            case ControlReply.OutcomeOneofCase.LongEventPending:
                if (reply.LongEventPending is { } pending && Diagnostic(pending.HasDetail, pending.Detail))
                {
                    return;
                }
                throw Contract("invalid clock pending result");
            default:
                throw Contract("clock control outcome missing");
        }
    }

    /// <summary>
    /// Go's <c>AssessClockEpoch</c>: classifies fresh cleanup evidence, never live permission. Original ownership is
    /// immutable even when speed, lease and observed tick change.
    /// </summary>
    public static ClockEpochState AssessClockEpoch(Epoch? original, ObservationContext? current, Status? status)
    {
        ValidateClockEpoch(original);
        ClockWire(current);
        ValidateContext(current);
        if (status is not null)
        {
            ValidateClockStatus(status, current!.Identity);
            if (!Equals(status.Context, current))
            {
                throw Contract("clock cleanup context mismatch");
            }
        }
        if (!SameIdentity(original!.Origin.Identity, current!.Identity))
        {
            return ClockEpochState.Superseded;
        }
        if (current.Tick < original.LastTick)
        {
            throw Contract("clock cleanup observation regressed");
        }
        if (status is null)
        {
            throw Contract("same-world clock status required");
        }
        if (status.StateCase is Status.StateOneofCase.Unavailable or Status.StateOneofCase.NeverStarted)
        {
            return ClockEpochState.Uncertain;
        }
        var actual = ClockStatusEpoch(status) ?? throw Contract("clock cleanup epoch missing");
        if (!Equals(actual.Owner, original.Owner))
        {
            return ClockEpochState.Superseded;
        }
        if (!Equals(actual.Origin, original.Origin)
            || !Equals(actual.Policy, original.Policy)
            || actual.StartTick != original.StartTick
            || actual.TickDeadline != original.TickDeadline
            || actual.LastTick < original.LastTick)
        {
            throw Contract("clock cleanup original epoch mismatch");
        }
        if (status.StateCase == Status.StateOneofCase.Stopped)
        {
            var stopped = status.Stopped;
            return status.ActualPaused && stopped.ActualPaused && stopped.PauseVerified ? ClockEpochState.Paused : ClockEpochState.Retired;
        }
        return status.StateCase == Status.StateOneofCase.Running ? ClockEpochState.Required : ClockEpochState.Uncertain;
    }

    /// <summary>
    /// Go's <c>WithHazardThresholds</c>: sets the policy's Go-authored hazard thresholds and returns
    /// <paramref name="p"/>. Native applies exactly these and refuses a policy missing any.
    /// </summary>
    public static WatchPolicy WithHazardThresholds(WatchPolicy p)
    {
        p.SeriousSingleHitDamage = Policy.PolicyFunctions.SeriousSingleHitDamage;
        p.SeriousSummaryHealthFloor = Policy.PolicyFunctions.SeriousSummaryHealthFloor;
        p.SeriousBleedRateFloor = Policy.PolicyFunctions.SeriousBleedRateFloor;
        p.SeriousVitalPartFloor = Policy.PolicyFunctions.SeriousVitalPartFloor;
        p.ExplosiveNearMarginCells = Policy.PolicyFunctions.ExplosiveNearMarginCells;
        p.MeleeReachCells = Policy.PolicyFunctions.MeleeReachCells;
        p.InjurySeverityFloorTicks = Policy.PolicyFunctions.InjurySeverityFloorTicks;
        p.PredatorMarginCells = Policy.PolicyFunctions.HuntPredatorMarginCells;
        return p;
    }
}
