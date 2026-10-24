namespace RimGovernor.Brain.Policy;

/// <summary>
/// The raid the colony faces: the storyteller's projected raid points and the colony's defense capacity
/// (colonists and powered turrets), both in raid-point units.
/// </summary>
public readonly record struct RaidDefense(double RaidPoints, double Capacity)
{
    /// <summary>The raid points the colony cannot yet stop; zero when the colony covers the raid.</summary>
    public double Shortfall => Math.Max(0, RaidPoints - Capacity);

    /// <summary>
    /// The raid threat a routine frame read. The raid size comes from the wire threat and the capacity from the
    /// defender roster; a frame without either throws (D3).
    /// </summary>
    public static RaidDefense Of(RoundsFacts f)
    {
        if (!f.RaidPoints.TryGetValue(out var raid))
        {
            throw new RequiredFactException("raid_points");
        }
        if (!f.DefenseCapacity.TryGetValue(out var have))
        {
            throw new RequiredFactException("defense_capacity");
        }
        return new RaidDefense(raid, have);
    }
}

public static class DefenseBudget
{
    /// <summary>
    /// How many more turrets the shortfall asks for: the shortfall divided by what a standing powered turret is
    /// observed to add (its DPS in raid-point units, averaged over those standing). With none standing there is no
    /// yield to measure, so one is built and the next review reads it; standing turrets none of which is powered
    /// yet are that probe, and ask for no more. No shortfall asks for none.
    /// </summary>
    internal static int TurretsOwed(RaidDefense threat, Fact<EquatableArray<DefenseTurretFacts>> turrets)
    {
        var shortfall = threat.Shortfall;
        if (shortfall <= 0)
        {
            return 0;
        }
        var sum = 0.0;
        var n = 0;
        foreach (var t in turrets.TryGetValue(out var standing) ? standing : default)
        {
            if (t.Powered && t.Dps > 0)
            {
                sum += t.Dps * DefenseCapacityPolicy.DefensePointsPerDps;
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
    public static int TurretBudget(RaidDefense threat, Fact<EquatableArray<DefenseTurretFacts>> turrets, int goalFloor) =>
        Math.Max(TurretsStanding(turrets) + TurretsOwed(threat, turrets), goalFloor);
}
