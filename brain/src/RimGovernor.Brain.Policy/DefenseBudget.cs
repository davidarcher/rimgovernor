namespace RimGovernor.Brain.Policy;

public static class DefenseBudget
{
    /// <summary>
    /// The raid points the colony cannot yet stop: the observed raid size minus the observed defense capacity
    /// (colonists and powered turrets). Zero when the colony covers the raid; unknown when either is unknown.
    /// </summary>
    public static Fact<double> DefenseShortfall(Fact<double> raidPoints, Fact<double> capacity)
    {
        if (!raidPoints.TryGetValue(out var raid) || !capacity.TryGetValue(out var have) || double.IsNaN(raid) || double.IsInfinity(raid) || double.IsNaN(have))
        {
            return Unknown<double>();
        }
        return Known(Math.Max(0, raid - have));
    }

    /// <summary>
    /// How many more turrets the shortfall asks for: the shortfall divided by what a standing powered turret is
    /// observed to add (its DPS in raid-point units, averaged over those standing). With none standing there is no
    /// yield to measure, so one is built and the next review reads it; standing turrets none of which is powered
    /// yet are that probe, and ask for no more. No shortfall, or an unknown one, asks for none.
    /// </summary>
    internal static int TurretsOwed(Fact<double> raidPoints, Fact<double> capacity, Fact<EquatableArray<DefenseTurretFacts>> turrets)
    {
        if (!DefenseShortfall(raidPoints, capacity).TryGetValue(out var shortfall) || shortfall <= 0)
        {
            return 0;
        }
        var sum = 0.0;
        var n = 0;
        foreach (var t in turrets.TryGetValue(out var standing) ? standing : default)
        {
            if (t.Powered.TryGetValue(out var powered) && powered && t.Dps.TryGetValue(out var dps) && dps > 0)
            {
                sum += dps * DefenseCapacityPolicy.DefensePointsPerDps;
                n++;
            }
        }
        if (n == 0)
        {
            return TurretsStanding(turrets) == 0 ? 1 : 0;
        }
        return Math.Max(1, (int)Math.Ceiling(shortfall / (sum / n)));
    }

    internal static int TurretsStanding(Fact<EquatableArray<DefenseTurretFacts>> turrets) =>
        turrets.TryGetValue(out var standing) ? standing.Count : 0;

    /// <summary>
    /// The turret count the layout plans: those already standing plus those the raid shortfall still asks for.
    /// A turret goal (<paramref name="goalFloor"/>, #3496) raises the count to at least that many. There is no ceiling; power, stock and the layout's geometry decide how many are placed, and each gate that
    /// stops placement is named on the tier.
    /// </summary>
    public static int TurretBudget(Fact<double> raidPoints, Fact<double> capacity, Fact<EquatableArray<DefenseTurretFacts>> turrets, int goalFloor) =>
        Math.Max(TurretsStanding(turrets) + TurretsOwed(raidPoints, capacity, turrets), goalFloor);
}
