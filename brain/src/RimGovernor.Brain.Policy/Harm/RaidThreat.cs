namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the gear planner needs of the colony's expected raid (#3545 of #3471): the weapon bins of the raid composition
/// (#3542), where a hit lands on a human (#3540) and the race's own armor. Built where the gear census is decoded, from the
/// observed raid points and hostile factions and the catalog; unknown when any of them is missing.
/// </summary>
public sealed record RaidThreat
{
    public EquatableArray<WeaponBin> Bins { get; init; }
    public BodyCoverage Coverage { get; init; } = new();
    public NaturalArmor Natural { get; init; } = new();
}

/// <summary>
/// The gear planner's armor term: damage per hit an option avoids against the expected raid, the sum over bins of the bin's
/// weight times the unarmored response minus the response with the option as the only layer on its groups
/// (<see cref="ArmorResponseRules"/>; quality and condition enter there). Layers over one body group are not combined here, so
/// the planner's per-item sum over-credits two garments on one group; the ledger combines them. With no threat the response
/// is against a nominal sharp hit, landing on the parts the option covers.
/// </summary>
internal sealed class GearArmorTerm(Fact<RaidThreat> threat)
{
    /// <summary>The finding on a loadout planned without a known threat.</summary>
    internal const string ThreatUnknownFinding = "gear_threat_unknown";

    private static readonly WeaponBin[] s_nominal = [new WeaponBin { Damage = 10, ArmorPenetration = 0.15, Category = ArmorCategory.Sharp, Weight = 1 }];

    private readonly bool _known = threat.IsKnown;
    private readonly Dictionary<string, double> _cache = new(StringComparer.Ordinal);

    internal bool ThreatKnown => _known;

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
        IReadOnlyList<WeaponBin> bins = s_nominal;
        BodyCoverage coverage;
        NaturalArmor natural;
        if (_known)
        {
            var t = threat.ValueOrDefault!;
            bins = t.Bins.ToArray();
            (coverage, natural) = (t.Coverage, t.Natural);
        }
        else
        {
            coverage = new BodyCoverage { Shares = [new CoverageShare { Groups = option.Groups, Probability = 1 }], Layers = option.Layers };
            natural = new NaturalArmor();
        }

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
