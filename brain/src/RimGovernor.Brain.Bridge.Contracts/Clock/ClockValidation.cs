using RimGovernor.Contracts.Wire.Clock;
using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go clock.go: the wire validators of the clock domain. The ClockControl capability and the Client reads are
// transport plumbing and are not ported; these are the checks the store and the observation reads share.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>clockSameEpoch</c>: <paramref name="actual"/> is the original epoch at <paramref name="speed"/>.</summary>
    internal static void ClockSameEpoch(Epoch? actual, Epoch? original, Speed speed)
    {
        if (actual is null
            || original is null
            || !Equals(actual.Owner, original.Owner)
            || !Equals(actual.Origin, original.Origin)
            || !Equals(actual.Policy, original.Policy)
            || actual.StartTick.GetValueOrDefault() != original.StartTick.GetValueOrDefault()
            || actual.TickDeadline.GetValueOrDefault() != original.TickDeadline.GetValueOrDefault()
            || actual.RequestedSpeed.GetValueOrDefault() != speed
            || actual.TestAcceleration.GetValueOrDefault() != original.TestAcceleration.GetValueOrDefault()
            || actual.LastTick.GetValueOrDefault() < original.LastTick.GetValueOrDefault())
        {
            throw Contract("clock immutable epoch mismatch");
        }
    }

    /// <summary>Go's <c>clockPolicy</c>: presence, bounds, finite positive hazard thresholds and distinct pawn ids.</summary>
    internal static void ClockPolicy(WatchPolicy? p, long budget)
    {
        if (p is null
            || p.Mode is null
            || (int)p.Mode.GetValueOrDefault() < 1
            || (int)p.Mode.GetValueOrDefault() > 2
            || p.HealthDropFraction is null
            || p.MinHealthFraction is null
            || p.HostileWithin is null
            || p.InjuryStopCooldownMs is null
            || p.InjuryStopCooldownMs.GetValueOrDefault() > 1800000)
        {
            throw Contract("clock policy presence or bounds");
        }
        ClockWire(p);
        if (p.HealthDropFraction.GetValueOrDefault() < 0.01f
            || p.HealthDropFraction.GetValueOrDefault() > 1
            || p.MinHealthFraction.GetValueOrDefault() < 0.01f
            || p.MinHealthFraction.GetValueOrDefault() > 1
            || p.HostileWithin.GetValueOrDefault() < 1
            || p.HostileWithin.GetValueOrDefault() > 250)
        {
            throw Contract("clock policy numeric bounds");
        }
        if (p.SeriousSingleHitDamage is null
            || p.SeriousSummaryHealthFloor is null
            || p.SeriousBleedRateFloor is null
            || p.SeriousVitalPartFloor is null
            || p.ExplosiveNearMarginCells is null
            || p.MeleeReachCells is null
            || p.InjurySeverityFloorTicks is null
            || p.PredatorMarginCells is null)
        {
            throw Contract("clock policy hazard thresholds missing");
        }
        foreach (var v in new[] { p.SeriousSingleHitDamage.GetValueOrDefault(), p.SeriousSummaryHealthFloor.GetValueOrDefault(), p.SeriousBleedRateFloor.GetValueOrDefault(), p.SeriousVitalPartFloor.GetValueOrDefault(), p.ExplosiveNearMarginCells.GetValueOrDefault(), p.MeleeReachCells.GetValueOrDefault(), p.PredatorMarginCells.GetValueOrDefault() })
        {
            if (!(v > 0) || float.IsInfinity(v))
            {
                throw Contract("clock policy hazard threshold not finite and positive");
            }
        }
        if (p.InjurySeverityFloorTicks.GetValueOrDefault() == 0 || p.InjurySeverityFloorTicks.GetValueOrDefault() > int.MaxValue)
        {
            throw Contract("clock policy injury severity floor bounds");
        }
        foreach (var ids in new[] { p.AcknowledgedHostileIds, p.AcknowledgedDownedColonistIds, p.AcknowledgedInjuredColonistIds, p.SurgicalRecoveryIds, p.MedicalRestIds })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!IsValidId(id) || !seen.Add(id))
                {
                    throw Contract("invalid or duplicate clock pawn ID");
                }
            }
        }
        if (p.MedicalRestIds.Count > 0 && budget > 600)
        {
            throw Contract("medical rest clock budget exceeds 600 ticks");
        }
        if (p.CombatStopEvents.Count > 0 && p.Mode.GetValueOrDefault() != WatchMode.Combat)
        {
            throw Contract("combat stop events outside combat mode");
        }
        var armed = new HashSet<CombatEvent>();
        foreach (var e in p.CombatStopEvents)
        {
            if (!Enum.IsDefined(e) || e == CombatEvent.Unspecified || !armed.Add(e))
            {
                throw Contract("invalid or duplicate combat stop event");
            }
        }
    }

    /// <summary>Go's <c>clockOwner</c>.</summary>
    internal static void ClockOwner(EpochOwner? owner)
    {
        if (owner is null || owner.ControllerSessionId is null || owner.Epoch is null || owner.Epoch.GetValueOrDefault() <= 0)
        {
            throw Contract("clock epoch owner required");
        }
        ClockWire(owner);
        ValidId((owner.ControllerSessionId ?? ""));
    }

    /// <summary>Go's <c>ValidateClockEpoch</c>.</summary>
    public static void ValidateClockEpoch(Epoch? e) => ClockEpoch(e);

    /// <summary>Go's <c>clockEpoch</c>: an epoch's owner, origin context, tick bounds, speed and policy.</summary>
    internal static void ClockEpoch(Epoch? e)
    {
        if (e is null)
        {
            throw Contract("clock epoch required");
        }
        ClockWire(e);
        ClockOwner(e.Owner);
        ValidateContext(e.Origin);
        if (e.Origin?.NativeGeneration is null
            || e.StartTick is null
            || e.StartTick.GetValueOrDefault() != e.Origin.Tick.GetValueOrDefault()
            || e.TickDeadline is null
            || e.TickDeadline.GetValueOrDefault() <= e.StartTick.GetValueOrDefault()
            || e.TickDeadline.GetValueOrDefault() - e.StartTick.GetValueOrDefault() > 1800000
            || e.LastTick is null
            || e.LastTick.GetValueOrDefault() < e.StartTick.GetValueOrDefault()
            || e.LeaseRemainingMs is null
            || e.LeaseRemainingMs.GetValueOrDefault() > 30000
            || e.RequestedSpeed is null
            || (int)e.RequestedSpeed.GetValueOrDefault() < 1
            || (int)e.RequestedSpeed.GetValueOrDefault() > 4)
        {
            throw Contract("clock epoch bounds or presence");
        }
        if (e.TestAcceleration.GetValueOrDefault() && e.RequestedSpeed.GetValueOrDefault() != Speed.Ultrafast)
        {
            throw Contract("clock accelerated epoch speed");
        }
        ClockPolicy(e.Policy, e.TickDeadline.GetValueOrDefault() - e.StartTick.GetValueOrDefault());
    }

    /// <summary>Go's <c>clockStatusEpoch</c>: the epoch a running, stopping or stopped status carries, null otherwise.</summary>
    internal static Epoch? ClockStatusEpoch(Status? s) =>
        s?.State switch
        {
            StatusState.RunningCase { Value: var running } => running.Epoch,
            StatusState.StoppingCase { Value: var stopping } => stopping.Epoch,
            StatusState.StoppedCase { Value: var stopped } => stopped.Epoch,
            _ => null,
        };

    /// <summary>Go's <c>ValidateClockStatus</c>: typed evidence, including explicit unavailable and stopped states.</summary>
    public static void ValidateClockStatus(Status? status, Identity? identity)
    {
        AuthorityIdentity(identity);
        ClockStatus(status, identity);
    }

    private static bool HealthInBounds(Health h) =>
        !((h.InjuryCount is not null && h.InjuryCount.GetValueOrDefault() < 0)
            || (h.Severity is not null && h.Severity.GetValueOrDefault() < 0)
            || (h.BleedRate is not null && h.BleedRate.GetValueOrDefault() < 0)
            || (h.BloodLoss is not null && (h.BloodLoss.GetValueOrDefault() < 0 || h.BloodLoss.GetValueOrDefault() > 1))
            || (h.SummaryHealth is not null && (h.SummaryHealth.GetValueOrDefault() < 0 || h.SummaryHealth.GetValueOrDefault() > 1)));

    /// <summary>Go's <c>clockStatus</c>.</summary>
    internal static void ClockStatus(Status? s, Identity? identity)
    {
        if (s is null)
        {
            throw Contract("clock status required");
        }
        ClockWire(s);
        ValidateContext(s.Context);
        if (!SameIdentity(s.Context?.Identity, identity))
        {
            throw Contract("clock status identity mismatch");
        }
        if (s.State is StatusState.UnavailableCase { Value: var unavailable })
        {
            ValidateUnavailable(unavailable);
            return;
        }
        if (s.NativeTickBoundary is null
            || s.DurableEvents is null
            || s.NewestCursor is null
            || s.NewestCursor.GetValueOrDefault() < 0
            || s.ObservedSpeed is null
            || (int)s.ObservedSpeed.GetValueOrDefault() < 1
            || (int)s.ObservedSpeed.GetValueOrDefault() > 5
            || s.ActualPaused is null)
        {
            throw Contract("clock status facts missing");
        }
        if (!Diagnostic(s.WatcherError is not null, (s.WatcherError ?? "")) || (s.MaxProbeTickGap is not null && s.MaxProbeTickGap.GetValueOrDefault() < 0))
        {
            throw Contract("clock status diagnostics");
        }
        switch (s.State)
        {
            case StatusState.RunningCase:
                break;
            case StatusState.StoppingCase { Value: var stopping }:
                if (stopping.PendingReason is null || !Diagnostic(stopping.Detail is not null, (stopping.Detail ?? "")) || stopping.ActualPaused is null)
                {
                    throw Contract("missing stopping facts");
                }
                break;
            case StatusState.StoppedCase { Value: var stopped }:
                if (stopped.Reason is null
                    || !Diagnostic(stopped.Detail is not null, (stopped.Detail ?? ""))
                    || stopped.ActualPaused is null
                    || stopped.PauseVerified is null
                    || stopped.PauseRequested is null
                    || stopped.StoppedAtUnixMs is null
                    || stopped.StoppedAtUnixMs.GetValueOrDefault() < 0)
                {
                    throw Contract("missing stopped facts");
                }
                break;
            case StatusState.NeverStartedCase:
                break;
            default:
                throw Contract("clock state required");
        }
        var epoch = ClockStatusEpoch(s);
        if (epoch is not null)
        {
            ClockEpoch(epoch);
        }
        else if (s.State is not StatusState.NeverStartedCase)
        {
            throw Contract("missing clock epoch");
        }
        foreach (var v in s.BaselineAlerts)
        {
            if (!IsValidId((v.Key ?? "")) || !Diagnostic(v.Label is not null, (v.Label ?? "")) || !Diagnostic(v.Priority is not null, (v.Priority ?? "")))
            {
                throw Contract("clock alert evidence");
            }
        }
        foreach (var v in s.SuppressedInjuries)
        {
            if (v.Pawn is null
                || !IsValidId((v.Pawn.PawnId ?? ""))
                || !Diagnostic(v.Pawn.Name is not null, (v.Pawn.Name ?? ""))
                || !Diagnostic(v.Pawn.Reason is not null, (v.Pawn.Reason ?? ""))
                || v.Suppression is null
                || v.NewWound is null)
            {
                throw Contract("clock injury evidence");
            }
            if (v.Pawn.Position is { } p && (p.X is null || p.Z is null || p.X.GetValueOrDefault() < 0 || p.Z.GetValueOrDefault() < 0))
            {
                throw Contract("clock injury position");
            }
            foreach (var h in new[] { v.Before, v.After })
            {
                if (h is not null && !HealthInBounds(h))
                {
                    throw Contract("clock injury health bounds");
                }
            }
        }
    }

    /// <summary>Go's <c>clockReceipt</c>: a receipt correlates to its attempt, admission context and outcome.</summary>
    internal static void ClockReceipt(ControlReceipt? r, Identity? identity, AttemptKey? attempt, ulong generation)
    {
        if (r is null)
        {
            throw Contract("clock receipt required");
        }
        ClockWire(r);
        BuildingAttempt(r.Attempt);
        ValidateContext(r.AdmittedContext);
        if (!Equals(r.Attempt, attempt)
            || !SameIdentity(r.AdmittedContext?.Identity, identity)
            || r.AdmittedContext?.NativeGeneration is null
            || (generation != 0 && r.AdmittedContext.NativeGeneration.GetValueOrDefault() != generation))
        {
            throw Contract("clock receipt admission mismatch");
        }
        var admittedTick = r.AdmittedContext.Tick.GetValueOrDefault();
        switch (r.Outcome)
        {
            case ControlReceiptOutcome.AppliedCase { Value: var applied }:
                ClockStatus(applied.Status, identity);
                if (applied.Status?.Context?.Tick.GetValueOrDefault() < admittedTick)
                {
                    throw Contract("clock status before admission");
                }
                var epoch = ClockStatusEpoch(applied.Status);
                if (epoch is null
                    || (epoch.Owner?.ControllerSessionId ?? "") != (attempt?.ControllerSessionId ?? "")
                    || !SameIdentity(epoch.Origin?.Identity, identity)
                    || epoch.Origin?.Tick.GetValueOrDefault() > admittedTick)
                {
                    throw Contract("clock applied epoch admission mismatch");
                }
                break;
            case ControlReceiptOutcome.UncertainCase { Value: var uncertain }:
                if (!Diagnostic(uncertain.Detail is not null, (uncertain.Detail ?? "")))
                {
                    throw Contract("missing uncertain clock");
                }
                if (uncertain.LastObserved is not null)
                {
                    ClockStatus(uncertain.LastObserved, identity);
                    if (uncertain.LastObserved.Context?.Tick.GetValueOrDefault() < admittedTick)
                    {
                        throw Contract("clock uncertain status before admission");
                    }
                }
                break;
            default:
                throw Contract("clock receipt outcome missing");
        }
    }

    /// <summary>
    /// Go's <c>clockPacing</c>: player acceleration is Ultrafast without test acceleration; fixed and unspecified
    /// pacing are always valid.
    /// </summary>
    internal static void ClockPacing(Pacing pacing, Speed speed, bool testAcceleration)
    {
        switch (pacing)
        {
            case Pacing.Unspecified:
            case Pacing.Fixed:
                return;
            case Pacing.PlayerAccelerated:
                if (speed != Speed.Ultrafast || testAcceleration)
                {
                    throw Contract("clock player acceleration requires ultrafast without test acceleration");
                }
                return;
            default:
                throw Contract("clock pacing");
        }
    }
}
