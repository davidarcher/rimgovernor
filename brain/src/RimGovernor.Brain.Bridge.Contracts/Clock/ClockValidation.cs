using RimGovernor.Contracts.Wire.Clock;
using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go clock.go: the wire validators of the clock domain. The ClockControl capability and the Client reads are
// transport plumbing and are not ported; these are the checks the store and the observation reads share.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>clockPolicy</c>: presence, bounds, finite positive hazard thresholds and distinct pawn ids.</summary>
    internal static void ClockPolicy(WatchPolicy? p)
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

    /// <summary>Checks a watch policy the brain is about to send with a Run.</summary>
    public static void ValidateClockPolicy(WatchPolicy? policy) => ClockPolicy(policy);

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
        ValidateContext(s.Context);
        if (!SameIdentity(s.Context?.Identity, identity))
        {
            throw Contract("clock status identity mismatch");
        }
        if (s.Unavailable is { } unavailable)
        {
            ValidateUnavailable(unavailable);
            return;
        }
        if (s.State is null
            || s.NativeTickBoundary is null
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
        if ((s.MaxProbeTickGap is not null && s.MaxProbeTickGap.GetValueOrDefault() < 0))
        {
            throw Contract("clock status diagnostics");
        }
        switch (s.State)
        {
            case ClockState.Running:
                break;
            case ClockState.Held:
                if (s.Reason is null
                    || (s.Reason != HoldReason.NeverRun
                        && (s.PauseVerified is null
                            || s.HeldAtUnixMs is null
                            || s.HeldAtUnixMs.GetValueOrDefault() < 0)))
                {
                    throw Contract("missing held facts");
                }
                break;
            default:
                throw Contract("clock state required");
        }
        foreach (var v in s.BaselineAlerts)
        {
            if (!IsValidId((v.Key ?? "")))
            {
                throw Contract("clock alert evidence");
            }
        }
        foreach (var v in s.SuppressedInjuries)
        {
            if (v.Pawn is null
                || !IsValidId((v.Pawn.PawnId ?? ""))
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
}
