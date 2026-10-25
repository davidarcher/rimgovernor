namespace RimGovernor.Brain.Policy;


/// <summary>
/// How far up the military ladder the armory planner reaches: the threat the storyteller's raid points call
/// for, capped by the research the colony has finished. Higher tiers include the lower ones.
/// </summary>
public enum ArmoryTier
{
    /// <summary>Needs no research: bows, clubs, tribal wear.</summary>
    Neolithic,

    /// <summary>Forged melee weapons and plate (Smithing).</summary>
    Smithing,

    /// <summary>Firearms and flak (Machining).</summary>
    Machining,

    /// <summary>Charge weapons and marine armor (Fabrication).</summary>
    Fabrication,
}

/// <summary>
/// Where a bench recipe stands on the armory ladder: laddered at the tier its research gates it to, or
/// unladdered when it makes no ordinary weapon (armor, apparel, a grenade, a specialty craft).
/// </summary>
public abstract record ArmoryRung
{
    private ArmoryRung() { }

    /// <summary>A weapon recipe on the ladder at <paramref name="Tier"/>.</summary>
    public sealed record Laddered(ArmoryTier Tier) : ArmoryRung;

    /// <summary>A recipe the armory ladder does not place.</summary>
    public sealed record Unladdered : ArmoryRung;
}

/// <summary>
/// The armory planner's tier selection: the threat tier the raid points call for, the research tier finished
/// projects allow, and the tier planned (the lower of the two).
/// </summary>
public sealed record ArmoryAssessment
{
    public ArmoryTier Threat { get; init; }

    public ArmoryTier Research { get; init; }

    public ArmoryTier Tier { get; init; }
}

/// <summary>A pawn's equipped primary weapon.</summary>
public sealed record ArmoryPrimary
{
    public bool Ranged { get; init; }

    public int Quality { get; init; }

    /// <summary>The primary's def rows.</summary>
    public WeaponDef Facts { get; init; } = new();
}

public static class ArmoryRules
{
    /// <summary>The research project each tier above neolithic needs.</summary>
    private static readonly Dictionary<ArmoryTier, ResearchProjectId> _armoryResearch = new()
    {
        [ArmoryTier.Smithing] = new("Smithing"),
        [ArmoryTier.Machining] = new("Machining"),
        [ArmoryTier.Fabrication] = new("Fabrication"),
    };

    /// <summary>
    /// The tier the threat calls for: the best the research allows while the raid outsizes the defense, neolithic
    /// once the defense covers it.
    /// </summary>
    public static ArmoryTier ArmoryThreatTier(RaidDefense threat) =>
        threat.Shortfall > 0 ? ArmoryTier.Fabrication : ArmoryTier.Neolithic;

    /// <summary>The highest tier whose research, and every lower tier's, is finished.</summary>
    public static ArmoryTier ArmoryResearchTier(ResearchFacts research)
    {
        var finished = new HashSet<ResearchProjectId>(research.Finished);

        var tier = ArmoryTier.Neolithic;
        for (var next = ArmoryTier.Smithing; next <= ArmoryTier.Fabrication && finished.Contains(_armoryResearch[next]); next++)
        {
            tier = next;
        }

        return tier;
    }

    /// <summary>Selects the armory tier from the raid threat and the research census a routine frame read; a frame without the census throws (D3).</summary>
    public static ArmoryAssessment AssessArmory(ReviewFacts f)
    {
        var research = f.Research;
        return AssessArmory(RaidDefense.Of(f), research);
    }

    /// <summary>Selects the armory tier from the raid threat and finished research.</summary>
    public static ArmoryAssessment AssessArmory(RaidDefense threat, ResearchFacts research)
    {
        var a = new ArmoryAssessment { Threat = ArmoryThreatTier(threat), Research = ArmoryResearchTier(research) };
        return a with { Tier = (ArmoryTier)Math.Min((int)a.Threat, (int)a.Research) };
    }

    /// <summary>
    /// Scales a weapon's planning score by quality (Awful=0 through Legendary=6), the weapon counterpart of
    /// GearQualityMultipliers; out of range is 0.
    /// </summary>
    public static double WeaponQualityMultiplier(int quality)
    {
        if (quality < 0 || quality > 6)
        {
            return 0;
        }

        return new[] { .8, .9, 1, 1.1, 1.2, 1.35, 1.5 }[quality];
    }

    /// <summary>
    /// The armory's bill target: per colonist, the best weapon a hosted, researched recipe at or under the tier
    /// makes. An unarmed colonist the loose weapons cannot arm wants one; an armed colonist wants one only when
    /// it beats the quality-scaled primary, and only when no loose weapon of that definition is left for it. The
    /// gear planner's GearReplace wears the upgrade once it is made.
    ///
    /// A hunter (WeaponRole.Hunter) wants a hunting weapon (WeaponDef.Hunts), so one unarmed or armed without it
    /// is demand like any unarmed colonist. Those weapons are returned apart as hunters: food owns that craft
    /// (the food/supply Need). A hunter's upgrade stays an ordinary fighter's.
    /// </summary>
    public static (IReadOnlyList<Amount> Fighters, IReadOnlyList<Amount> Hunters) ArmoryWeaponDemand(
        ArmoryTier tier,
        IReadOnlyList<EquipCandidatePawn> pawns,
        IReadOnlyDictionary<Domain.PawnId, ArmoryPrimary> primaries,
        IReadOnlyList<EquipCandidateWeapon> weapons,
        IReadOnlyList<GearRecipe> recipes,
        IReadOnlyDictionary<Resource, WeaponDef> products
    )
    {
        var (fighters, hunters, _) = ArmoryWeaponAccounting(tier, pawns, primaries, weapons, recipes, products);
        return (fighters, hunters);
    }

    /// <summary>
    /// ArmoryWeaponDemand with the loose weapons it leaves over: per definition the count lying about once the
    /// pairs AssignEquip makes and the upgrades it counts as covered are taken out.
    /// </summary>
    internal static (IReadOnlyList<Amount> Fighters, IReadOnlyList<Amount> Hunters, Dictionary<Resource, int> Spare) ArmoryWeaponAccounting(
        ArmoryTier tier,
        IReadOnlyList<EquipCandidatePawn> pawns,
        IReadOnlyDictionary<Domain.PawnId, ArmoryPrimary> primaries,
        IReadOnlyList<EquipCandidateWeapon> weapons,
        IReadOnlyList<GearRecipe> recipes,
        IReadOnlyDictionary<Resource, WeaponDef> products
    )
    {
        var assigned = new HashSet<Domain.PawnId>();
        var loose = new Dictionary<Resource, int>();
        var spare = new Dictionary<Resource, int>();
        foreach (var w in weapons)
        {
            var def = new Resource(w.Definition);
            loose[def] = loose.GetValueOrDefault(def) + 1;
            spare[def] = spare.GetValueOrDefault(def) + 1;
        }

        foreach (var pair in WeaponAssignmentRules.AssignEquip(pawns, weapons))
        {
            assigned.Add((Domain.PawnId)pair.Pawn);
            var def = new Resource(pair.Weapon.Definition);
            spare[def] = spare.GetValueOrDefault(def) - 1;
        }

        var counts = new Dictionary<Resource, long>();
        var hunts = new Dictionary<Resource, long>();
        foreach (var p in pawns)
        {
            var armed = p.Armed;
            if (assigned.Contains(p.Pawn) || !ArmoryFighter(p))
            {
                continue;
            }

            var floor = 0.0;
            var lacks = !armed;
            if (armed)
            {
                if (!primaries.TryGetValue(p.Pawn, out var current))
                {
                    continue;
                }

                var weaponClass = current.Ranged ? WeaponClass.Ranged : WeaponClass.Melee;
                lacks = !current.Facts.Hunts();
                floor =
                    WeaponAssignmentRules.ScoreWeapon(p, current.Facts, weaponClass)
                    * WeaponQualityMultiplier(current.Quality);
            }

            var (best, score) = ArmoryBestWeapon(p, tier, recipes, products);
            if (best.IsEmpty || score <= floor)
            {
                continue;
            }

            if (armed && loose.GetValueOrDefault(best) > 0)
            {
                loose[best]--;
                spare[best] = spare.GetValueOrDefault(best) - 1;
                continue;
            }

            if (p.Role == WeaponRole.Hunter && lacks)
            {
                hunts[best] = hunts.GetValueOrDefault(best) + 1;
            }
            else
            {
                counts[best] = counts.GetValueOrDefault(best) + 1;
            }
        }

        return (Amounts(counts), Amounts(hunts), spare);
    }

    internal static List<Amount> Amounts(Dictionary<Resource, long> counts) =>
        [.. counts.Select(kv => new Amount(kv.Key, kv.Value)).OrderBy(a => a.Resource.Value, StringComparer.Ordinal)];

    /// <summary>A present colonist capable of violence, armed or not.</summary>
    internal static bool ArmoryFighter(EquipCandidatePawn p)
    {
        if (p.NoArms.Length != 0 || p.MentalState)
        {
            return false;
        }

        return !p.Dead && !p.Downed && !p.Drafted && !p.IncapableOfViolence;
    }

    internal static (Resource Best, double Score) ArmoryBestWeapon(
        EquipCandidatePawn p,
        ArmoryTier reach,
        IReadOnlyList<GearRecipe> recipes,
        IReadOnlyDictionary<Resource, WeaponDef> products
    )
    {
        var best = default(Resource);
        var bestScore = 0.0;
        foreach (var recipe in recipes)
        {
            if (recipe.Armory is not ArmoryRung.Laddered { Tier: var rung } || rung > reach || !recipe.Available || !recipe.AvailableOn)
            {
                continue;
            }

            foreach (var def in recipe.Products)
            {
                if (!products.TryGetValue(def, out var facts) || !facts.Armament())
                {
                    continue;
                }

                var weaponClass = facts.Ranged ? WeaponClass.Ranged : WeaponClass.Melee;
                var score = WeaponAssignmentRules.ScoreWeapon(p, facts, weaponClass);
                if (score > bestScore || (score > 0 && score == bestScore && def < best))
                {
                    best = def;
                    bestScore = score;
                }
            }
        }

        return (best, bestScore);
    }
}
