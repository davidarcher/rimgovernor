namespace RimGovernor.Brain.Policy;

// Go: trait_worth.go. Trait worth: whether a trait degree is good or bad in a prospect is the sign of its net
// effect on the terms the planners value, computed from the degree's TraitDef row (the definition catalog's
// TraitBalance) and weighted here, in one place. The weights are the judgment; every effect they multiply is read.

/// <summary>What a trait degree does to the terms a prospect is valued by, as the rows state them.</summary>
public sealed record TraitBalance
{
    /// <summary>The change per StatDef: the offset, or the factor minus 1.</summary>
    public IReadOnlyDictionary<string, double> Stats { get; init; } = new Dictionary<string, double>();

    /// <summary>The hunger-rate factor minus 1.</summary>
    public double HungerRate { get; init; }

    /// <summary>The mood points the trait carries (see TraitMoodWeight).</summary>
    public double Mood { get; init; }

    /// <summary>The count of work types the trait disables.</summary>
    public int DisabledWork { get; init; }

    /// <summary>The code-applied sociability (TraitSociable).</summary>
    public int Sociable { get; init; }

    /// <summary>The weighted net of the balance: positive is a good trait, negative a bad one, zero (within epsilon) neutral.</summary>
    public double Worth()
    {
        var worth =
            (HungerRate * PolicyFunctions.TraitHungerWeight)
            + (Mood * PolicyFunctions.TraitMoodWeight)
            + (DisabledWork * PolicyFunctions.TraitDisabledWorkWeight)
            + (Sociable * PolicyFunctions.TraitSociableWeight);
        // Summed in ordinal stat order: float addition is not associative, and Go's map order is random.
        foreach (var stat in Stats.Keys.Order(StringComparer.Ordinal))
        {
            worth += Stats[stat] * PolicyFunctions.TraitStatWeights.GetValueOrDefault(stat);
        }
        if (worth > -PolicyFunctions.TraitWorthEpsilon && worth < PolicyFunctions.TraitWorthEpsilon)
        {
            return 0;
        }
        return worth;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The worth of one unit of change in a StatDef by a trait degree: a stat offset counts as it is, a stat
    /// factor as its distance from 1. The weight is signed: MentalBreakThreshold and incoming damage are better
    /// lower, so their weights are negative. Registered in DefTables.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, double> TraitStatWeights = new Dictionary<string, double>
    {
        ["WorkSpeedGlobal"] = 1,
        ["GlobalLearningFactor"] = 0.25,
        ["MentalBreakThreshold"] = -1,
        ["PainShockThreshold"] = 1,
        ["IncomingDamageFactor"] = -0.5,
        ["ImmunityGainSpeed"] = 0.5,
        ["MoveSpeed"] = 0.25,
    };

    /// <summary>The worth of one mood point: a permanent offset counts in full, a situational thought only at its worst stage when that stage is negative (a risk, never a credit).</summary>
    internal const double TraitMoodWeight = 0.05;

    /// <summary>The worth of one unit of hunger-rate factor above 1 (a trait that eats more costs the colony).</summary>
    internal const double TraitHungerWeight = -0.2;

    /// <summary>The worth of each work type a trait disables.</summary>
    internal const double TraitDisabledWorkWeight = -0.1;

    /// <summary>The worth of one step of sociability.</summary>
    internal const double TraitSociableWeight = 0.1;

    /// <summary>The net below which a degree is neutral.</summary>
    internal const double TraitWorthEpsilon = 1e-9;

    /// <summary>
    /// The sociability the game applies to a trait in code, where no row of the TraitDef or a ThoughtDef states
    /// it: Kind +1, Abrasive -1 (Kind and Abrasive only nullify the looks thoughts and change certainty loss in
    /// their rows). Registered in DefTables.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, int> SociableTraits = new Dictionary<string, int> { ["Kind"] = 1, ["Abrasive"] = -1 };

    /// <summary>The code-applied sociability of a trait; zero for a trait it does not list.</summary>
    public static int TraitSociable(string name) => SociableTraits.GetValueOrDefault(name);

    // The vanilla thoughts the game raises in code, whose rows state which traits take no mood from the act or
    // carry a mood of their own: a trait that nullifies the thought of a prisoner dying (an execution), of a
    // butchered human, or of being naked is Execution, HumanButcher or Nudist; the trait the carrying-a-ranged-weapon
    // thought requires is MeleeOnly.
    public const string ThoughtPrisonerDied = "KnowPrisonerDiedInnocent";

    public const string ThoughtButcheredHuman = "ButcheredHumanlikeCorpse";

    public const string ThoughtNaked = "Naked";

    public const string ThoughtRangedCarried = "BrawlerUnhappy";

    /// <summary>The thought of a harvest from a colonist: a trait that nullifies it is a surgeon who harvests without a mood loss.</summary>
    public const string ThoughtOrganHarvested = "KnowColonistOrganHarvested";

    /// <summary>The thought of wearing a dead man's apparel.</summary>
    public const string ThoughtTaintedApparel = "DeadMansApparel";

    // The thoughts whose worker applies to one trait only (requiredTraits): a bedroom that is impressive or not
    // (Greedy, Ascetic), a better bedroom than the pawn's own (Jealous), the night and carrying an incendiary
    // weapon (NightOwl, Pyromaniac).
    public const string ThoughtGreedy = "Greedy";

    public const string ThoughtAscetic = "Ascetic";

    public const string ThoughtJealous = "Jealous";

    public const string ThoughtNightOwl = "NightOwlDuringTheNight";

    public const string ThoughtPyromaniac = "PyromaniacHappy";

    /// <summary>The thought a drug desire trait raises while unsatisfied; its required trait is the DrugDesire trait, whose degree is the chemical interest.</summary>
    public const string ThoughtDrugDesire = "DrugDesireInterest";

    /// <summary>The thoughts TraitEffects reads by name. Registered in DefTables.</summary>
    internal static readonly IReadOnlyList<string> TraitThoughts =
    [
        ThoughtPrisonerDied,
        ThoughtButcheredHuman,
        ThoughtNaked,
        ThoughtRangedCarried,
        ThoughtOrganHarvested,
        ThoughtTaintedApparel,
        ThoughtGreedy,
        ThoughtAscetic,
        ThoughtJealous,
        ThoughtNightOwl,
        ThoughtPyromaniac,
        ThoughtDrugDesire,
    ];
}
