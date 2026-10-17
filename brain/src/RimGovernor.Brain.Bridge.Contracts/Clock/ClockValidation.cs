using RimGovernor.Contracts.Wire.Clock;
using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go clock.go: the watch policy validator of the clock domain, checked before the brain sends a Run.
public static class ClockValidation
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
            throw BridgeContractException.Contract("clock policy presence or bounds");
        }
        if (p.HealthDropFraction.GetValueOrDefault() < 0.01f
            || p.HealthDropFraction.GetValueOrDefault() > 1
            || p.MinHealthFraction.GetValueOrDefault() < 0.01f
            || p.MinHealthFraction.GetValueOrDefault() > 1
            || p.HostileWithin.GetValueOrDefault() < 1
            || p.HostileWithin.GetValueOrDefault() > 250)
        {
            throw BridgeContractException.Contract("clock policy numeric bounds");
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
            throw BridgeContractException.Contract("clock policy hazard thresholds missing");
        }
        foreach (var v in new[] { p.SeriousSingleHitDamage.GetValueOrDefault(), p.SeriousSummaryHealthFloor.GetValueOrDefault(), p.SeriousBleedRateFloor.GetValueOrDefault(), p.SeriousVitalPartFloor.GetValueOrDefault(), p.ExplosiveNearMarginCells.GetValueOrDefault(), p.MeleeReachCells.GetValueOrDefault(), p.PredatorMarginCells.GetValueOrDefault() })
        {
            if (!(v > 0) || float.IsInfinity(v))
            {
                throw BridgeContractException.Contract("clock policy hazard threshold not finite and positive");
            }
        }
        if (p.InjurySeverityFloorTicks.GetValueOrDefault() == 0 || p.InjurySeverityFloorTicks.GetValueOrDefault() > int.MaxValue)
        {
            throw BridgeContractException.Contract("clock policy injury severity floor bounds");
        }
        foreach (var ids in new[] { p.AcknowledgedHostileIds, p.AcknowledgedDownedColonistIds, p.AcknowledgedInjuredColonistIds, p.SurgicalRecoveryIds, p.MedicalRestIds })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!WireValidation.IsValidId(id) || !seen.Add(id))
                {
                    throw BridgeContractException.Contract("invalid or duplicate clock pawn ID");
                }
            }
        }
        if (p.CombatStopEvents.Count > 0 && p.Mode.GetValueOrDefault() != WatchMode.Combat)
        {
            throw BridgeContractException.Contract("combat stop events outside combat mode");
        }
        var armed = new HashSet<CombatEvent>();
        foreach (var e in p.CombatStopEvents)
        {
            if (!Enum.IsDefined(e) || e == CombatEvent.Unspecified || !armed.Add(e))
            {
                throw BridgeContractException.Contract("invalid or duplicate combat stop event");
            }
        }
    }

    /// <summary>Checks a watch policy the brain is about to send with a Run.</summary>
    public static void ValidateClockPolicy(WatchPolicy? policy) => ClockPolicy(policy);
}
