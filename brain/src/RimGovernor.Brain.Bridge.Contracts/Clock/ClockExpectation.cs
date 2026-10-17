using RimGovernor.Brain.Policy;
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
        p = p with { SeriousSingleHitDamage = HazardThresholdsRules.SeriousSingleHitDamage };
        p = p with { SeriousSummaryHealthFloor = HazardThresholdsRules.SeriousSummaryHealthFloor };
        p = p with { SeriousBleedRateFloor = HazardThresholdsRules.SeriousBleedRateFloor };
        p = p with { SeriousVitalPartFloor = HazardThresholdsRules.SeriousVitalPartFloor };
        p = p with { ExplosiveNearMarginCells = HazardThresholdsRules.ExplosiveNearMarginCells };
        p = p with { MeleeReachCells = HazardThresholdsRules.MeleeReachCells };
        p = p with { InjurySeverityFloorTicks = HazardThresholdsRules.InjurySeverityFloorTicks };
        p = p with { PredatorMarginCells = HazardThresholdsRules.HuntPredatorMarginCells };
        return p;
    }
}
