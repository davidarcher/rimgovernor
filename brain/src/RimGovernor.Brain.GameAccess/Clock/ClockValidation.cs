using RimGovernor.Brain.GameFacts.Clock;
using RimGovernor.Brain.GameOrders.Clock;
using RimGovernor.Brain.GameFacts.Common;

namespace RimGovernor.Brain.GameAccess;

// The watch policy validator of the clock domain, checked before the brain sends a Run.
public static class ClockValidation
{
    /// <summary>Presence, bounds, finite positive hazard thresholds and distinct pawn ids.</summary>
    internal static void ClockPolicy(WatchPolicy? p)
    {
        if (p is null
            || (int)p.Mode < 1
            || (int)p.Mode > 2
            || p.InjuryStopCooldownMs > 1800000)
        {
            throw BridgeContractException.Contract("clock policy presence or bounds");
        }
        if (p.HealthDropFraction < 0.01f
            || p.HealthDropFraction > 1
            || p.MinHealthFraction < 0.01f
            || p.MinHealthFraction > 1
            || p.HostileWithin < 1
            || p.HostileWithin > 250)
        {
            throw BridgeContractException.Contract("clock policy numeric bounds");
        }
        foreach (var v in new[] { p.SeriousSingleHitDamage, p.SeriousSummaryHealthFloor, p.SeriousBleedRateFloor, p.SeriousVitalPartFloor, p.ExplosiveNearMarginCells, p.MeleeReachCells })
        {
            if (!(v > 0) || float.IsInfinity(v))
            {
                throw BridgeContractException.Contract("clock policy hazard threshold not finite and positive");
            }
        }
        if (p.InjurySeverityFloorTicks == 0 || p.InjurySeverityFloorTicks > int.MaxValue)
        {
            throw BridgeContractException.Contract("clock policy injury severity floor bounds");
        }
        if (!Enum.IsDefined(p.HungerHoldCategory) || p.HungerHoldCategory == RimGovernor.Brain.GameFacts.Defs.HungerCategory.Fed)
        {
            throw BridgeContractException.Contract("clock policy hunger hold category missing or fed");
        }
        foreach (var ids in new[] { p.AcknowledgedHostileIds, p.AcknowledgedDownedColonistIds, p.AcknowledgedInjuredColonistIds, p.SurgicalRecoveryIds, p.MedicalRestIds, p.AcknowledgedHungryColonistIds })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!seen.Add(id))
                {
                    throw BridgeContractException.Contract("duplicate clock pawn ID");
                }
            }
        }
        if (p.CombatStopEvents.Count > 0 && p.Mode != WatchMode.Combat)
        {
            throw BridgeContractException.Contract("combat stop events outside combat mode");
        }
        var armed = new HashSet<CombatEvent>();
        foreach (var e in p.CombatStopEvents)
        {
            if (!Enum.IsDefined(e) || !armed.Add(e))
            {
                throw BridgeContractException.Contract("invalid or duplicate combat stop event");
            }
        }
    }

    /// <summary>Checks a watch policy the brain is about to send with a Run.</summary>
    public static void ValidateClockPolicy(WatchPolicy? policy) => ClockPolicy(policy);
}
