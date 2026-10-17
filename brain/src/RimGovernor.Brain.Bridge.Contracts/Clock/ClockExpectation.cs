using RimGovernor.Contracts.Wire.Clock;
using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go clock_hazard.go: the hazard thresholds the brain authors into every Run.
public static class ClockExpectation
{
    /// <summary>
    /// Go's <c>WithHazardThresholds</c>: sets the policy's Go-authored hazard thresholds and returns
    /// <paramref name="p"/>. Native applies exactly these and refuses a policy missing any.
    /// </summary>
    public static WatchPolicy WithHazardThresholds(WatchPolicy p)
    {
        p = p with { SeriousSingleHitDamage = Policy.PolicyFunctions.SeriousSingleHitDamage };
        p = p with { SeriousSummaryHealthFloor = Policy.PolicyFunctions.SeriousSummaryHealthFloor };
        p = p with { SeriousBleedRateFloor = Policy.PolicyFunctions.SeriousBleedRateFloor };
        p = p with { SeriousVitalPartFloor = Policy.PolicyFunctions.SeriousVitalPartFloor };
        p = p with { ExplosiveNearMarginCells = Policy.PolicyFunctions.ExplosiveNearMarginCells };
        p = p with { MeleeReachCells = Policy.PolicyFunctions.MeleeReachCells };
        p = p with { InjurySeverityFloorTicks = Policy.PolicyFunctions.InjurySeverityFloorTicks };
        p = p with { PredatorMarginCells = Policy.PolicyFunctions.HuntPredatorMarginCells };
        return p;
    }
}
