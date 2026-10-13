namespace RimGovernor.Brain.Policy;

// Go: armory.go

/// <summary>
/// How far up the military ladder the armory planner reaches: the threat the storyteller's raid points call
/// for, capped by the research the colony has finished. Higher tiers include the lower ones.
/// </summary>
public enum ArmoryTier
{
    /// <summary>The raid-point reading is not observed yet; the armory plans nothing on a guess.</summary>
    Unknown,

    /// <summary>Needs no research: bows, clubs, tribal wear.</summary>
    Neolithic,

    /// <summary>Forged melee weapons and plate (Smithing).</summary>
    Smithing,

    /// <summary>Firearms and flak (Machining).</summary>
    Machining,

    /// <summary>Charge weapons and marine armor (Fabrication).</summary>
    Fabrication,
}

public static class ArmoryTierExtensions
{
    /// <summary>Go's <c>ArmoryTier.String()</c>.</summary>
    public static string Name(this ArmoryTier t) =>
        t switch
        {
            ArmoryTier.Neolithic => "neolithic",
            ArmoryTier.Smithing => "smithing",
            ArmoryTier.Machining => "machining",
            ArmoryTier.Fabrication => "fabrication",
            _ => "unknown",
        };
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
    public string Definition { get; init; } = "";

    public bool Ranged { get; init; }

    public int Quality { get; init; }

    /// <summary>The primary's def rows.</summary>
    public WeaponDef Facts { get; init; } = new();
}

public static partial class PolicyFunctions
{
    // The raid-point thresholds at which the threat tier steps up. The lower two match the turret budget's
    // (TurretBudget) so defenses and gear scale together.
    internal static readonly double ArmorySmithingPoints = TurretStepPoints;
    internal static readonly double ArmoryMachiningPoints = TurretHighPoints;
    internal const double ArmoryFabricationPoints = 2500;

    /// <summary>The research project each tier above neolithic needs.</summary>
    private static readonly Dictionary<ArmoryTier, ResearchProjectId> _armoryResearch = new()
    {
        [ArmoryTier.Smithing] = new("Smithing"),
        [ArmoryTier.Machining] = new("Machining"),
        [ArmoryTier.Fabrication] = new("Fabrication"),
    };

    /// <summary>The tier the observed raid points call for; unknown (or NaN) points are ArmoryTier.Unknown.</summary>
    public static ArmoryTier ArmoryThreatTier(Fact<double> raidPoints)
    {
        var known = raidPoints.TryGetValue(out var points);
        if (!known || double.IsNaN(points))
        {
            return ArmoryTier.Unknown;
        }

        if (points < ArmorySmithingPoints)
        {
            return ArmoryTier.Neolithic;
        }

        if (points < ArmoryMachiningPoints)
        {
            return ArmoryTier.Smithing;
        }

        if (points < ArmoryFabricationPoints)
        {
            return ArmoryTier.Machining;
        }

        return ArmoryTier.Fabrication;
    }

    /// <summary>The highest tier whose research, and every lower tier's, is finished. An unknown census allows only neolithic.</summary>
    public static ArmoryTier ArmoryResearchTier(Fact<ResearchFacts> research)
    {
        var finished = new HashSet<ResearchProjectId>();
        if (research.TryGetValue(out var facts))
        {
            foreach (var project in facts.Finished)
            {
                finished.Add(project);
            }
        }

        var tier = ArmoryTier.Neolithic;
        for (var next = ArmoryTier.Smithing; next <= ArmoryTier.Fabrication && finished.Contains(_armoryResearch[next]); next++)
        {
            tier = next;
        }

        return tier;
    }

    /// <summary>Selects the armory tier from raid points and research.</summary>
    public static ArmoryAssessment AssessArmory(Fact<double> raidPoints, Fact<ResearchFacts> research)
    {
        var a = new ArmoryAssessment { Threat = ArmoryThreatTier(raidPoints), Research = ArmoryResearchTier(research) };
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
    /// makes. An unarmed colonist the loose weapons cannot arm wants one (an unknown tier arms at neolithic:
    /// arming the unarmed is never held on a guess); an armed colonist wants one only at a known tier, only when
    /// it beats the quality-scaled primary, and only when no loose weapon of that definition is left for it. The
    /// gear planner's GearReplace wears the upgrade once it is made.
    ///
    /// A hunter (WeaponRole.Hunter) wants a hunting weapon (WeaponDef.Hunts), so one unarmed or armed without it
    /// is demand like any unarmed colonist. Those weapons are returned apart as hunters: food owns that craft
    /// (EnsureFoodSupply). A hunter's upgrade stays an ordinary fighter's.
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

        foreach (var pair in AssignEquip(pawns, weapons))
        {
            assigned.Add(pair.Pawn);
            var def = new Resource(pair.Weapon.Definition);
            spare[def] = spare.GetValueOrDefault(def) - 1;
        }

        var counts = new Dictionary<Resource, long>();
        var hunts = new Dictionary<Resource, long>();
        foreach (var p in pawns)
        {
            var known = p.Armed.TryGetValue(out var armed);
            if (!known || assigned.Contains(p.Pawn) || !ArmoryFighter(p))
            {
                continue;
            }

            var reach = tier;
            var floor = 0.0;
            var lacks = !armed;
            if (armed)
            {
                if (!primaries.TryGetValue(p.Pawn, out var current) || tier == ArmoryTier.Unknown)
                {
                    continue;
                }

                var weaponClass = current.Ranged ? WeaponClass.Ranged : WeaponClass.Melee;
                lacks = !current.Facts.Hunts();
                floor =
                    ScoreWeapon(p, new EquipCandidateWeapon { Definition = current.Definition, Class = weaponClass, Facts = current.Facts })
                    * WeaponQualityMultiplier(current.Quality);
            }
            else if (reach == ArmoryTier.Unknown)
            {
                reach = ArmoryTier.Neolithic;
            }

            var (best, score) = ArmoryBestWeapon(p, reach, recipes, products);
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
        if (p.NoArms.Length != 0)
        {
            return false;
        }

        foreach (var f in new[] { p.Dead, p.Downed, p.Drafted, p.MentalState, p.IncapableOfViolence })
        {
            if (!f.TryGetValue(out var value) || value)
            {
                return false;
            }
        }

        return true;
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
            if (!WeaponRecipe(recipe) || recipe.Armory > reach || !Positive(recipe.Available) || !Positive(recipe.AvailableOn))
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
                var score = ScoreWeapon(p, new EquipCandidateWeapon { Definition = def.Value, Class = weaponClass, Facts = facts });
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
