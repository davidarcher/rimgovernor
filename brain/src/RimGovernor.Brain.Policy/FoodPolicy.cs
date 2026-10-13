namespace RimGovernor.Brain.Policy;

/// <summary>The pawn's current food policy: its load id and allowed definitions. It is observation, never an explicit controller preference. (Go <c>FoodRestriction</c>.)</summary>
public sealed record FoodRestriction
{
    public string PolicyId { get; init; } = "";

    public IReadOnlyList<string> Allowed { get; init; } = [];
}

/// <summary>What a food definition is for diets: a meal tier, meat by source, a vegetable, fungus, an animal product or other. (Go <c>FoodKind</c>.)</summary>
public enum FoodKind
{
    [WireName("")]
    None = 0,

    [WireName("meal_awful")]
    MealAwful,

    [WireName("meal_simple")]
    MealSimple,

    [WireName("meal_fine")]
    MealFine,

    [WireName("meal_lavish")]
    MealLavish,

    [WireName("raw_meat")]
    RawMeat,

    [WireName("human_meat")]
    HumanMeat,

    [WireName("insect_meat")]
    InsectMeat,

    [WireName("vegetable")]
    Vegetable,

    [WireName("fungus")]
    Fungus,

    [WireName("animal_product")]
    AnimalProduct,

    [WireName("other")]
    Other,

    [WireName("kibble")]
    Kibble,

    [WireName("hay")]
    Hay,
}

/// <summary>What a meal definition is made of: meat only, no meat, or either; <see cref="None"/> for a food that is no meal. (Go <c>MealIngredients</c>.)</summary>
public enum MealIngredients
{
    [WireName("")]
    None = 0,

    [WireName("any")]
    Any,

    [WireName("meat")]
    MeatOnly,

    [WireName("non_meat")]
    NonMeat,
}

/// <summary>One food ThingDef a food policy can allow. (Go <c>Food</c>.)</summary>
public sealed record Food(string Def, FoodKind Kind, MealIngredients Ingredients);

/// <summary>One native food policy: its load id, label, holders and allowed food definitions. (Go <c>FoodPolicyEntry</c>.)</summary>
public sealed record FoodPolicyEntry
{
    public string Id { get; init; } = "";

    public string Label { get; init; } = "";

    public IReadOnlyList<PawnId> Pawns { get; init; } = [];

    public IReadOnlyList<string> Allowed { get; init; } = [];
}

/// <summary>
/// What one pawn's food policy owes: the contents to write (null when the policy labelled with its short name
/// already allows exactly them) and the assignment (null when it already holds it). (Go <c>FoodPolicyChange</c>.)
/// </summary>
public sealed record FoodPolicyChange(FoodPolicy? Write, PawnSettings? Assign);

/// <summary>What a pawn's ideoligion precepts and traits make of food. (Go <c>Diet</c>.)</summary>
public sealed record Diet
{
    /// <summary>The Cannibal trait or a cannibalism precept that accepts human meat.</summary>
    public bool Cannibal { get; init; }

    /// <summary>A precept that loves insect meat; everyone else minds it.</summary>
    public bool InsectMeat { get; init; }

    /// <summary>The meat-eating precept.</summary>
    public bool Vegetarian { get; init; }

    /// <summary>The meat-eating precept.</summary>
    public bool Carnivore { get; init; }

    /// <summary>Fungus preferred.</summary>
    public bool Fungal { get; init; }

    /// <summary>Fungus despised.</summary>
    public bool NoFungus { get; init; }

    public bool Ascetic { get; init; }

    public bool Gourmand { get; init; }

    /// <summary>The mood tier allows fine and lavish meals, for a pawn near its mental break threshold or with high expectations.</summary>
    public bool FineMeals { get; init; }

    /// <summary>The colony has no other food to eat, so the travel reserve (pemmican, packaged survival meals) is allowed at home too.</summary>
    public bool Reserve { get; init; }
}

/// <summary>
/// A food policy holder outside the work census: a prisoner with its diet, or a tame animal with the food
/// definitions its race can eat. (Go <c>FoodEater</c>.)
/// </summary>
public sealed record FoodEater
{
    public PawnId Pawn { get; init; }

    public bool Animal { get; init; }

    /// <summary>The prisoner's diet inputs (DietOf).</summary>
    public IReadOnlyList<PawnTrait> Traits { get; init; } = [];

    /// <summary>The prisoner's diet inputs (DietOf).</summary>
    public IReadOnlyList<string> Precepts { get; init; } = [];

    public IReadOnlyList<string> Edible { get; init; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>How far above the minor break threshold a pawn's mood still counts as near a break.</summary>
    private const double MoodTierMargin = 0.1;

    /// <summary>
    /// The foods kept for caravans: not eaten at home while the colony has other food; when it is short of food
    /// (Diet.Reserve) they are the food. The one list Go decides it by; native only reports defs and forbidden
    /// state.
    /// </summary>
    internal static readonly IReadOnlyList<Resource> TravelReserve = [new Resource("Pemmican"), new Resource("MealSurvivalPack")];

    /// <summary>The mood tier: near the minor break threshold or under high expectations; unknown mood reads count as content.</summary>
    private static bool FineMeals(WorkPawn pawn)
    {
        if (pawn.HighExpectations.TryGetValue(out var high) && high)
        {
            return true;
        }
        var mk = pawn.Mood.TryGetValue(out var mood);
        var tk = pawn.BreakThreshold.TryGetValue(out var threshold);
        return mk && tk && mood < threshold + MoodTierMargin;
    }

    // The history events each diet fact reads the ideoligion on (the game's HistoryEventDefOf members), for the shared precept rule.
    private static readonly string[] HumanMeatEvents = ["AteHumanMeat", "AteHumanMeatDirect", "AteHumanMeatAsIngredient"];
    private static readonly string[] InsectMeatEvents = ["AteInsectMeatDirect", "AteInsectMeatAsIngredient"];
    private static readonly string[] MeatEvents = ["AteMeat"];
    private static readonly string[] NonMeatEvents = ["AteNonMeat"];
    private static readonly string[] FungusEvents = ["AteFungus", "AteFungusAsIngredient"];

    /// <summary>Go <c>PawnDiet</c>: reads the pawn's diet; unknown while its traits or policy inputs are, or, for a pawn with precepts, the ideoligion defs.</summary>
    public static (Diet Diet, bool Ok) PawnDiet(WorkPawn pawn, Fact<Ideoligion> ideology)
    {
        var ok = pawn.Traits.TryGetValue(out var traits);
        var known = pawn.PolicyInputs.TryGetValue(out var inputs);
        if (!ok || !known)
        {
            return (new Diet(), false);
        }
        var (d, dok) = DietOf(traits!, inputs!.Precepts, ideology);
        if (!dok)
        {
            return (new Diet(), false);
        }
        return (d with { FineMeals = FineMeals(pawn) }, true);
    }

    /// <summary>
    /// Go <c>DietOf</c>: the diet of trait defNames and of the precepts of the pawn's own ideoligion, read
    /// through the shared precept rule. A pawn without precepts has no ideoligion and so no precept diet; one
    /// with precepts is unknown while the ideoligion defs are. Human meat is accepted when no precept costs mood
    /// for it; insect meat and fungus where a precept approves; vegetarian and carnivore are precepts that
    /// penalise meat or non-meat; fungus is despised where a precept penalises it.
    /// </summary>
    public static (Diet Diet, bool Ok) DietOf(IReadOnlyList<PawnTrait> traits, IReadOnlyList<string> precepts, Fact<Ideoligion> ideology)
    {
        var d = TraitDiet(traits);
        if (precepts.Count == 0)
        {
            return (d, true);
        }
        if (!ideology.TryGetValue(out var i))
        {
            return (new Diet(), false);
        }
        var held = i.HeldBy(precepts);
        if (held.EventStances(HumanMeatEvents).Worst is PreceptStance.Allowed or PreceptStance.Approved)
        {
            d = d with { Cannibal = true };
        }
        if (held.EventStances(InsectMeatEvents).Best == PreceptStance.Approved)
        {
            d = d with { InsectMeat = true };
        }
        if (held.EventStances(MeatEvents).Worst.CostsMood())
        {
            d = d with { Vegetarian = true };
        }
        if (held.EventStances(NonMeatEvents).Worst.CostsMood())
        {
            d = d with { Carnivore = true };
        }
        var (worst, best) = held.EventStances(FungusEvents);
        return (d with { Fungal = best == PreceptStance.Approved, NoFungus = worst.CostsMood() }, true);
    }

    /// <summary>The diet the pawn's traits give.</summary>
    private static Diet TraitDiet(IReadOnlyList<PawnTrait> traits)
    {
        var d = new Diet();
        foreach (var t in traits)
        {
            d = d with
            {
                Cannibal = d.Cannibal || t.Effects.Cannibal,
                Ascetic = d.Ascetic || t.Effects.Ascetic,
                Gourmand = d.Gourmand || t.Effects.Gourmand,
            };
        }
        return d;
    }

    /// <summary>
    /// Go <c>DietFoods</c>: the food definitions a diet allows. Everyone gets every meal tier and raw food as a
    /// fallback, except: human meat only for a cannibal; insect meat only where it is loved; a vegetarian no
    /// meat and no meat-only meal; a carnivore no vegetable, no meat-free meal and no fungus unless fungus is
    /// preferred; fungus never where it is despised; fine and lavish meals only in the mood tier, and never for
    /// an ascetic (not also a gourmand), whose mood it does not feel; the travel reserve (pemmican, packaged
    /// survival meals) only while the colony is short of other food (Diet.Reserve).
    /// </summary>
    public static IReadOnlyList<string> DietFoods(Diet d, IReadOnlyList<Food> foods)
    {
        bool Allowed(Food f)
        {
            if (ReserveFoodDefinition(new Resource(f.Def)))
            {
                return d.Reserve;
            }
            switch (f.Kind)
            {
                case FoodKind.HumanMeat:
                    return d.Cannibal && !d.Vegetarian;
                case FoodKind.InsectMeat:
                    return d.InsectMeat && !d.Vegetarian;
                case FoodKind.RawMeat:
                    return !d.Vegetarian;
                case FoodKind.Vegetable:
                    return !d.Carnivore;
                case FoodKind.Fungus:
                    return !d.NoFungus && (d.Fungal || !d.Carnivore);
                case FoodKind.MealFine or FoodKind.MealLavish:
                    if (!d.FineMeals || (d.Ascetic && !d.Gourmand))
                    {
                        return false;
                    }
                    break;
            }
            return !((d.Vegetarian && f.Ingredients == MealIngredients.MeatOnly) || (d.Carnivore && f.Ingredients == MealIngredients.NonMeat));
        }
        return [.. foods.Where(Allowed).Select(f => f.Def).Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>What a prisoner or slave eats: nutrient paste and raw food.</summary>
    private static readonly FoodKind[] CaptiveKinds =
    [
        FoodKind.MealAwful, FoodKind.RawMeat, FoodKind.HumanMeat, FoodKind.InsectMeat, FoodKind.Vegetable, FoodKind.Fungus, FoodKind.AnimalProduct,
    ];

    /// <summary>What a tame animal eats: kibble, hay and raw food, never a meal.</summary>
    private static readonly FoodKind[] AnimalKinds =
    [
        FoodKind.Kibble, FoodKind.Hay, FoodKind.RawMeat, FoodKind.HumanMeat, FoodKind.InsectMeat, FoodKind.Vegetable, FoodKind.Fungus, FoodKind.AnimalProduct,
    ];

    /// <summary>Go <c>CaptiveFoods</c>: a prisoner's or slave's foods: its diet's paste and raw food.</summary>
    public static IReadOnlyList<string> CaptiveFoods(Diet d, IReadOnlyList<Food> foods) => DietFoods(d, [.. foods.Where(f => CaptiveKinds.Contains(f.Kind))]);

    /// <summary>Go <c>AnimalFoods</c>: a tame animal's foods: the kibble, hay and raw food its race can eat. Corpses are never foods; native disallows them on every write.</summary>
    public static IReadOnlyList<string> AnimalFoods(IReadOnlyList<string> edible, IReadOnlyList<Food> foods) =>
        [.. foods.Where(f => AnimalKinds.Contains(f.Kind) && edible.Contains(f.Def)).Select(f => f.Def).Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Go <c>DietPolicyChanges</c>: the per-pawn food policy writes owed: each owned pawn with a food policy holds
    /// the policy labelled with its short name, allowing DietFoods for a colonist, CaptiveFoods for a slave or
    /// prisoner and AnimalFoods for a tame animal. A pawn whose short name another owned pawn shares or that
    /// several policies carry waits.
    /// </summary>
    public static IReadOnlyList<FoodPolicyChange> DietPolicyChanges(
        Fact<Ideoligion> ideology,
        IReadOnlyList<WorkPawn> pawns,
        IReadOnlyList<FoodEater> eaters,
        IReadOnlyList<OwnedName> names,
        IReadOnlyList<FoodPolicyEntry> policies,
        IReadOnlyList<Food> foods,
        bool reserveFood
    )
    {
        var rows = new List<(PawnId Id, IReadOnlyList<string> Want)>();
        foreach (var pawn in pawns)
        {
            if (!pawn.FoodRestriction.IsKnown)
            {
                continue;
            }
            var (diet, ok) = PawnDiet(pawn, ideology);
            if (!ok)
            {
                continue;
            }
            _ = pawn.PolicyInputs.TryGetValue(out var inputs);
            if (inputs?.GuestStatus is "Slave" or "Prisoner")
            {
                rows.Add((pawn.Id, CaptiveFoods(diet, foods)));
            }
            else
            {
                rows.Add((pawn.Id, DietFoods(diet with { Reserve = reserveFood }, foods)));
            }
        }
        foreach (var e in eaters)
        {
            if (e.Animal)
            {
                rows.Add((e.Pawn, AnimalFoods(e.Edible, foods)));
            }
            else
            {
                var (diet, ok) = DietOf(e.Traits, e.Precepts, ideology);
                if (!ok)
                {
                    continue;
                }
                rows.Add((e.Pawn, CaptiveFoods(diet, foods)));
            }
        }
        var shortNames = new Dictionary<PawnId, string>();
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            shortNames[n.Pawn] = n.Short;
            var key = n.Short.ToLowerInvariant();
            count[key] = count.GetValueOrDefault(key) + 1;
        }
        var @out = new List<FoodPolicyChange>();
        foreach (var row in rows)
        {
            var name = shortNames.GetValueOrDefault(row.Id) ?? "";
            if (name.Length == 0 || count.GetValueOrDefault(name.ToLowerInvariant()) != 1)
            {
                continue;
            }
            var own = policies.Where(p => p.Label == name).ToList();
            if (own.Count > 1)
            {
                continue;
            }
            FoodPolicy? write = null;
            PawnSettings? assign = null;
            try
            {
                if (own.Count == 0 || !SortedCopy(own[0].Allowed).SequenceEqual(row.Want, StringComparer.Ordinal))
                {
                    write = new FoodPolicy(name, row.Want);
                }
            }
            catch (DomainException)
            {
                continue;
            }
            try
            {
                if (own.Count == 0 || !own[0].Pawns.Contains(row.Id))
                {
                    assign = PawnSettings.NewFoodPolicySetting((Domain.PawnId)row.Id, name);
                }
            }
            catch (DomainException)
            {
                continue;
            }
            if (write is not null || assign is not null)
            {
                @out.Add(new FoodPolicyChange(write, assign));
            }
        }
        return @out;
    }
}
