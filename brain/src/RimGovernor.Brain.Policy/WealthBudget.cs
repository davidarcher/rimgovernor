namespace RimGovernor.Brain.Policy;

// Go: wealth_budget.go.

public static partial class PolicyFunctions
{
    /// <summary>The storyteller's raid-point floor (vanilla minimum, 35): a tiny reading never makes the budget divide by a vanishing threat.</summary>
    private const double WealthBudgetMinRaidPoints = 35.0;

    /// <summary>
    /// The wealth headroom the colony's defense affords. Raid points scale with wealth, so the wealth the defense
    /// capacity can hold is wealth x capacity / raidPoints; the headroom is that minus wealth: positive means wealth
    /// may grow by that much, negative means shed it. Raid points are floored at 35. Any unknown, non-finite or
    /// negative input leaves the budget unknown.
    /// </summary>
    public static Fact<double> WealthBudget(Fact<double> raidPoints, Fact<double> capacity, Fact<WealthFacts> wealth)
    {
        var pk = raidPoints.TryGetValue(out var points);
        var ck = capacity.TryGetValue(out var strength);
        var wk = wealth.TryGetValue(out var w);
        if (!pk || !ck || !wk || !Finite(points) || !Finite(strength) || !Finite(w!.Total) || points < 0 || strength < 0 || w.Total < 0)
        {
            return Unknown<double>();
        }
        points = Math.Max(points, WealthBudgetMinRaidPoints);
        return Known(w.Total * (strength - points) / points);
    }

    /// <summary>The headroom from f's raid points, defense capacity and wealth.</summary>
    public static Fact<double> WealthBudget(this RoundsFacts f) => WealthBudget(f.RaidPoints, f.DefenseCapacity, f.Wealth);
}
