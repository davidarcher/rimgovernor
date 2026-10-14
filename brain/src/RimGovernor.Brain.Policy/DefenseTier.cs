namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Reports that projected raid points could overpower the colony: above the observed defense capacity. An unknown raid size or capacity is never a promotion.
    /// </summary>
    public static bool DefensePromoted(Fact<double> raidPoints, Fact<double> capacity) =>
        raidPoints.TryGetValue(out var raid) && capacity.TryGetValue(out var have) && raid > have;

    /// <summary>
    /// The construction tier of a defense build. Defense is Secure unless promoted to Survive; while a roamer is
    /// owned the core ring takes Comfort so its materials arrive before other Secure builds, and a promotion
    /// still outranks that.
    /// </summary>
    public static ConstructionTier DefenseBuildTier(bool promoted, bool coreRing, bool roamerOwned)
    {
        if (promoted)
        {
            return ConstructionTier.Survive;
        }
        if (coreRing && roamerOwned)
        {
            return ConstructionTier.Comfort;
        }
        return ConstructionTier.Secure;
    }

    /// <summary>
    /// Selects the placed sites of the defense layout that a promotion must lift to Survive: a layout building
    /// whose blueprint or frame carries a known tier other than Survive. A site with no tier is ungated.
    /// </summary>
    public static IReadOnlyList<ConstructionSite> DefenseRetiers(IReadOnlyList<ConstructionSite> sites, IReadOnlyList<Building> layout)
    {
        var wanted = new HashSet<(string, Cell)>();
        foreach (var b in layout)
        {
            wanted.Add((b.Definition, b.Cell));
        }
        return sites
            .Where(s => s.Id.Length != 0
                && s.Tier.TryGetValue(out var tier)
                && tier != ConstructionTier.Survive
                && wanted.Contains((s.Building!.Definition, s.Building.Cell)))
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }
}
