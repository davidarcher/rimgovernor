namespace RimGovernor.Brain.Policy;


public static partial class WealthBudgetRules
{
    /// <summary>The storyteller's raid-point floor (vanilla minimum, 35): a tiny reading never makes the budget divide by a vanishing threat.</summary>
    internal const double WealthBudgetMinRaidPoints = 35.0;

    /// <summary>
    /// The wealth headroom the colony's defense affords. Raid points scale with wealth, so the wealth the defense
    /// capacity can hold is wealth x capacity / raidPoints; the headroom is that minus wealth: positive means wealth
    /// may grow by that much, negative means shed it. Raid points are floored at 35. Missing, non-finite or
    /// negative inputs refuse the calculation.
    /// </summary>
    public static double WealthBudget(double points, double capacity, RimGovernor.Contracts.Wire.Observations.ThreatFacts wealth)
    {
        var strength = capacity;
        var w = wealth ?? throw new RequiredFactException("wealth");
        if (!TradePolicyRules.Finite(points) || !TradePolicyRules.Finite(strength) || !TradePolicyRules.Finite(w.WealthTotal) || points < 0 || strength < 0 || w.WealthTotal < 0)
        {
            throw new RequiredFactException("finite nonnegative wealth budget inputs");
        }
        points = Math.Max(points, WealthBudgetMinRaidPoints);
        return w.WealthTotal * (strength - points) / points;
    }

    /// <summary>The headroom from f's raid points, defense capacity and wealth.</summary>
    public static double WealthBudget(this RoundsFacts f) => WealthBudget(f.Census.Threat.Observed.RaidPoints, f.DefenseCapacity, f.Census.Threat.Observed);
}
