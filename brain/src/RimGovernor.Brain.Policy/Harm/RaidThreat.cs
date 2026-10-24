namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the gear planner needs of the colony's expected raid (#3545 of #3471). Built once per gear census from the observed raid
/// points, the hostile factions and the catalog: <see cref="Composed"/> when a composition forms, <see cref="Uncomposed"/> when it
/// cannot (no hostile faction, or the faction defs support no weapon bin).
/// </summary>
public abstract record RaidThreat
{
    private RaidThreat()
    {
    }

    /// <summary>The weapon bins of the raid composition (#3542), where a hit lands on a human (#3540) and the race's own armor.</summary>
    public sealed record Composed(EquatableArray<WeaponBin> Bins, BodyCoverage Coverage, NaturalArmor Natural) : RaidThreat;

    /// <summary>No raid composition forms; armor is valued against a nominal sharp hit.</summary>
    public sealed record Uncomposed : RaidThreat;
}

/// <summary>
/// The gear planner's armor term: damage per hit an option avoids against the expected raid, the sum over bins of the bin's
/// weight times the unarmored response minus the response with the option as the only layer on its groups
/// (<see cref="ArmorResponseRules"/>; quality and condition enter there). Layers over one body group are not combined here, so
/// the planner's per-item sum over-credits two garments on one group; the ledger combines them. Against an uncomposed threat
/// the response is against a nominal sharp hit, landing on the parts the option covers.
/// </summary>
internal sealed class GearArmorTerm(RaidThreat threat)
{
    /// <summary>The finding on a loadout planned against an uncomposed threat.</summary>
    internal const string ThreatUncomposedFinding = "gear_threat_uncomposed";

    private static readonly WeaponBin[] s_nominal = [new WeaponBin { Damage = 10, ArmorPenetration = 0.15, Category = ArmorCategory.Sharp, Weight = 1 }];

    private readonly Dictionary<string, double> _cache = new(StringComparer.Ordinal);

    internal bool ThreatComposed => threat is RaidThreat.Composed;

    /// <summary>Damage per hit the option avoids; zero for an option with no armor layer.</summary>
    internal double Of(GearOption option)
    {
        if (!_cache.TryGetValue(option.ID, out var value))
        {
            _cache[option.ID] = value = Compute(option);
        }

        return value;
    }

    private double Compute(GearOption option)
    {
        var (bins, coverage, natural) = threat is RaidThreat.Composed t
            ? ((IReadOnlyList<WeaponBin>)t.Bins.ToArray(), t.Coverage, t.Natural)
            : (s_nominal, new BodyCoverage { Shares = [new CoverageShare { Groups = option.Groups, Probability = 1 }], Layers = option.Layers }, new NaturalArmor());

        IReadOnlyList<GearOption> bare = [];
        IReadOnlyList<GearOption> worn = [option];
        var avoided = 0.0;
        foreach (var bin in bins)
        {
            avoided += bin.Weight * (ArmorResponseRules.Of(bare, natural, coverage, bin).ExpectedDamage - ArmorResponseRules.Of(worn, natural, coverage, bin).ExpectedDamage);
        }

        return avoided;
    }
}
