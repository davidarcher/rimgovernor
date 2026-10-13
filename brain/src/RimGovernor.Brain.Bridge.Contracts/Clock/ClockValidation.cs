using RimGovernor.Protocol.Clock;
using RimGovernor.Protocol.Common;

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
            || actual.StartTick != original.StartTick
            || actual.TickDeadline != original.TickDeadline
            || actual.RequestedSpeed != speed
            || actual.TestAcceleration != original.TestAcceleration
            || actual.LastTick < original.LastTick)
        {
            throw Contract("clock immutable epoch mismatch");
        }
    }

    /// <summary>Go's <c>clockPolicy</c>: presence, bounds, finite positive hazard thresholds and distinct pawn ids.</summary>
    internal static void ClockPolicy(WatchPolicy? p, long budget)
    {
        if (p is null
            || !p.HasMode
            || (int)p.Mode < 1
            || (int)p.Mode > 2
            || !p.HasHealthDropFraction
            || !p.HasMinHealthFraction
            || !p.HasHostileWithin
            || !p.HasInjuryStopCooldownMs
            || p.InjuryStopCooldownMs > 1800000)
        {
            throw Contract("clock policy presence or bounds");
        }
        ClockWire(p);
        if (p.HealthDropFraction < 0.01f
            || p.HealthDropFraction > 1
            || p.MinHealthFraction < 0.01f
            || p.MinHealthFraction > 1
            || p.HostileWithin < 1
            || p.HostileWithin > 250)
        {
            throw Contract("clock policy numeric bounds");
        }
        if (!p.HasSeriousSingleHitDamage
            || !p.HasSeriousSummaryHealthFloor
            || !p.HasSeriousBleedRateFloor
            || !p.HasSeriousVitalPartFloor
            || !p.HasExplosiveNearMarginCells
            || !p.HasMeleeReachCells
            || !p.HasInjurySeverityFloorTicks
            || !p.HasPredatorMarginCells)
        {
            throw Contract("clock policy hazard thresholds missing");
        }
        foreach (var v in new[] { p.SeriousSingleHitDamage, p.SeriousSummaryHealthFloor, p.SeriousBleedRateFloor, p.SeriousVitalPartFloor, p.ExplosiveNearMarginCells, p.MeleeReachCells, p.PredatorMarginCells })
        {
            if (!(v > 0) || float.IsInfinity(v))
            {
                throw Contract("clock policy hazard threshold not finite and positive");
            }
        }
        if (p.InjurySeverityFloorTicks == 0 || p.InjurySeverityFloorTicks > int.MaxValue)
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
        if (p.CombatStopEvents.Count > 0 && p.Mode != WatchMode.Combat)
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
        if (owner is null || !owner.HasControllerSessionId || !owner.HasEpoch || owner.Epoch <= 0)
        {
            throw Contract("clock epoch owner required");
        }
        ClockWire(owner);
        ValidId(owner.ControllerSessionId);
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
        if (!e.Origin.HasNativeGeneration
            || !e.HasStartTick
            || e.StartTick != e.Origin.Tick
            || !e.HasTickDeadline
            || e.TickDeadline <= e.StartTick
            || e.TickDeadline - e.StartTick > 1800000
            || !e.HasLastTick
            || e.LastTick < e.StartTick
            || !e.HasLeaseRemainingMs
            || e.LeaseRemainingMs > 30000
            || !e.HasRequestedSpeed
            || (int)e.RequestedSpeed < 1
            || (int)e.RequestedSpeed > 4)
        {
            throw Contract("clock epoch bounds or presence");
        }
        if (e.TestAcceleration && e.RequestedSpeed != Speed.Ultrafast)
        {
            throw Contract("clock accelerated epoch speed");
        }
        ClockPolicy(e.Policy, e.TickDeadline - e.StartTick);
    }

    /// <summary>Go's <c>clockStatusEpoch</c>: the epoch a running, stopping or stopped status carries, null otherwise.</summary>
    internal static Epoch? ClockStatusEpoch(Status? s) =>
        s?.StateCase switch
        {
            Status.StateOneofCase.Running => s.Running.Epoch,
            Status.StateOneofCase.Stopping => s.Stopping.Epoch,
            Status.StateOneofCase.Stopped => s.Stopped.Epoch,
            _ => null,
        };

    /// <summary>Go's <c>ValidateClockStatus</c>: typed evidence, including explicit unavailable and stopped states.</summary>
    public static void ValidateClockStatus(Status? status, Identity? identity)
    {
        AuthorityIdentity(identity);
        ClockStatus(status, identity);
    }

    private static bool HealthInBounds(Health h) =>
        !((h.HasInjuryCount && h.InjuryCount < 0)
            || (h.HasSeverity && h.Severity < 0)
            || (h.HasBleedRate && h.BleedRate < 0)
            || (h.HasBloodLoss && (h.BloodLoss < 0 || h.BloodLoss > 1))
            || (h.HasSummaryHealth && (h.SummaryHealth < 0 || h.SummaryHealth > 1)));

    /// <summary>Go's <c>clockStatus</c>.</summary>
    internal static void ClockStatus(Status? s, Identity? identity)
    {
        if (s is null)
        {
            throw Contract("clock status required");
        }
        ClockWire(s);
        ValidateContext(s.Context);
        if (!SameIdentity(s.Context.Identity, identity))
        {
            throw Contract("clock status identity mismatch");
        }
        if (s.StateCase == Status.StateOneofCase.Unavailable)
        {
            ValidateUnavailable(s.Unavailable);
            return;
        }
        if (!s.HasNativeTickBoundary
            || !s.HasDurableEvents
            || !s.HasNewestCursor
            || s.NewestCursor < 0
            || !s.HasObservedSpeed
            || (int)s.ObservedSpeed < 1
            || (int)s.ObservedSpeed > 5
            || !s.HasActualPaused)
        {
            throw Contract("clock status facts missing");
        }
        if (!Diagnostic(s.HasWatcherError, s.WatcherError) || (s.HasMaxProbeTickGap && s.MaxProbeTickGap < 0))
        {
            throw Contract("clock status diagnostics");
        }
        switch (s.StateCase)
        {
            case Status.StateOneofCase.Running:
                break;
            case Status.StateOneofCase.Stopping:
                var stopping = s.Stopping;
                if (!stopping.HasPendingReason || !Diagnostic(stopping.HasDetail, stopping.Detail) || !stopping.HasActualPaused)
                {
                    throw Contract("missing stopping facts");
                }
                break;
            case Status.StateOneofCase.Stopped:
                var stopped = s.Stopped;
                if (!stopped.HasReason
                    || !Diagnostic(stopped.HasDetail, stopped.Detail)
                    || !stopped.HasActualPaused
                    || !stopped.HasPauseVerified
                    || !stopped.HasPauseRequested
                    || !stopped.HasStoppedAtUnixMs
                    || stopped.StoppedAtUnixMs < 0)
                {
                    throw Contract("missing stopped facts");
                }
                break;
            case Status.StateOneofCase.NeverStarted:
                break;
            default:
                throw Contract("clock state required");
        }
        var epoch = ClockStatusEpoch(s);
        if (epoch is not null)
        {
            ClockEpoch(epoch);
        }
        else if (s.StateCase != Status.StateOneofCase.NeverStarted)
        {
            throw Contract("missing clock epoch");
        }
        foreach (var v in s.BaselineAlerts)
        {
            if (!IsValidId(v.Key) || !Diagnostic(v.HasLabel, v.Label) || !Diagnostic(v.HasPriority, v.Priority))
            {
                throw Contract("clock alert evidence");
            }
        }
        foreach (var v in s.SuppressedInjuries)
        {
            if (v.Pawn is null
                || !IsValidId(v.Pawn.PawnId)
                || !Diagnostic(v.Pawn.HasName, v.Pawn.Name)
                || !Diagnostic(v.Pawn.HasReason, v.Pawn.Reason)
                || !v.HasSuppression
                || !v.HasNewWound)
            {
                throw Contract("clock injury evidence");
            }
            if (v.Pawn.Position is { } p && (!p.HasX || !p.HasZ || p.X < 0 || p.Z < 0))
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
            || !SameIdentity(r.AdmittedContext.Identity, identity)
            || !r.AdmittedContext.HasNativeGeneration
            || (generation != 0 && r.AdmittedContext.NativeGeneration != generation))
        {
            throw Contract("clock receipt admission mismatch");
        }
        switch (r.OutcomeCase)
        {
            case ControlReceipt.OutcomeOneofCase.Applied:
                var applied = r.Applied;
                ClockStatus(applied.Status, identity);
                if (applied.Status.Context.Tick < r.AdmittedContext.Tick)
                {
                    throw Contract("clock status before admission");
                }
                var epoch = ClockStatusEpoch(applied.Status);
                if (epoch is null
                    || epoch.Owner.ControllerSessionId != (attempt?.ControllerSessionId ?? "")
                    || !SameIdentity(epoch.Origin.Identity, identity)
                    || epoch.Origin.Tick > r.AdmittedContext.Tick)
                {
                    throw Contract("clock applied epoch admission mismatch");
                }
                break;
            case ControlReceipt.OutcomeOneofCase.Uncertain:
                var uncertain = r.Uncertain;
                if (!Diagnostic(uncertain.HasDetail, uncertain.Detail))
                {
                    throw Contract("missing uncertain clock");
                }
                if (uncertain.LastObserved is not null)
                {
                    ClockStatus(uncertain.LastObserved, identity);
                    if (uncertain.LastObserved.Context.Tick < r.AdmittedContext.Tick)
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
