namespace RimGovernor.Brain.Policy;

/// <summary>
/// The game's item numbers from the definition catalog, read once per load: planners look prices, nutrition,
/// potency and stuff factors up here instead of carrying tables of their own. The default value is a frame
/// without a catalog; every lookup on it throws and the planners that need one wait.
/// </summary>
public sealed record ItemFacts
{
    /// <summary>The game's MarketValue stat of every item def that shows one.</summary>
    public IReadOnlyDictionary<Resource, double> Market { get; init; } = new Dictionary<Resource, double>();

    /// <summary>The game's Nutrition stat of every item def that shows one.</summary>
    public IReadOnlyDictionary<Resource, double> Nutrition { get; init; } = new Dictionary<Resource, double>();

    /// <summary>The game's MedicalPotency of every medicine def.</summary>
    public IReadOnlyDictionary<Resource, double> MedicalPotency { get; init; } = new Dictionary<Resource, double>();

    /// <summary>Each stuff's Beauty stat factor (stuffProps.statFactors, 1 when absent); the stuff's own statBases Beauty is never it.</summary>
    public IReadOnlyDictionary<Resource, double> StuffBeauty { get; init; } = new Dictionary<Resource, double>();

    /// <summary>Each stuff's categories.</summary>
    public IReadOnlyDictionary<Resource, IReadOnlyList<string>> StuffCategories { get; init; } = new Dictionary<Resource, IReadOnlyList<string>>();

    /// <summary>Each stuff-made def's accepted stuff categories.</summary>
    public IReadOnlyDictionary<Resource, IReadOnlyList<string>> AcceptedStuff { get; init; } = new Dictionary<Resource, IReadOnlyList<string>>();

    /// <summary>Every item def's thing categories.</summary>
    public IReadOnlyDictionary<Resource, IReadOnlyList<string>> Categories { get; init; } = new Dictionary<Resource, IReadOnlyList<string>>();

    /// <summary>
    /// The apparel defs the apparel policy counts as armor (the Soldier outfit tag without Worker), sorted: the
    /// armory's apparel. Every other apparel def is clothing for the wardrobe. Empty on a frame whose stat table
    /// is missing or whose catalog names no armor.
    /// </summary>
    public IReadOnlyList<Resource> Armor { get; init; } = [];

    /// <summary>The coin every price is in and the census counts: the def Tradeable.IsCurrency tests (ThingDefOf.Silver).</summary>
    public Resource Currency { get; init; }

    /// <summary>What the fermenting barrel takes in and turns into beer (ThingDefOf.Wort): the intermediate the beer reserve produces.</summary>
    public Resource Wort { get; init; }

    /// <summary>The catalog's drug defs (a CompProperties_Drug with a chemical) in preference order: social drugs before hard ones, then the game's listOrder, then name.</summary>
    public IReadOnlyList<Drug> Drugs { get; init; } = [];

    /// <summary>The chemicals the drugs build tolerance of and addiction to, by name.</summary>
    public IReadOnlyDictionary<string, Chemical> Chemicals { get; init; } = new Dictionary<string, Chemical>();

    /// <summary>The drug that makes its taker immune to diseases, null when the game has none.</summary>
    public Prevention? Prevention { get; init; }

    /// <summary>The art recipes (RoleSculpture), smallest first.</summary>
    public IReadOnlyList<Sculpture> Sculptures { get; init; } = [];

    /// <summary>The defs the game generates as deep deposits (a positive ThingDef.deepCommonality): what a deep drill can yield, sorted.</summary>
    public IReadOnlyList<Resource> DeepResources { get; init; } = [];

    private IReadOnlyList<string> CategoriesOf(Resource def) => Categories.GetValueOrDefault(def) ?? [];

    /// <summary>Reports whether def is a deep deposit def.</summary>
    public bool IsDeepResource(Resource def) => DeepResources.Contains(def);

    /// <summary>The social drugs a per-pawn policy may allow for joy.</summary>
    public IReadOnlyList<Drug> RecreationDrugs() => DrugsWhere(d => d.Social);

    /// <summary>The combat-enhancing drugs, preferred first.</summary>
    public IReadOnlyList<Drug> CombatDrugs() => DrugsWhere(d => d.Combat);

    /// <summary>The drugs that build chemical, preferred first.</summary>
    public IReadOnlyList<Drug> DrugsOf(string chemical) => DrugsWhere(d => d.Chemical == chemical);

    /// <summary>The drug a chemical-dependency gene's chemical is satisfied with: the preferred drug of the chemical.</summary>
    public (Resource Def, bool Ok) DependencyDrug(string chemical)
    {
        var drugs = DrugsOf(chemical);
        return drugs.Count == 0 ? (default, false) : (drugs[0].Def, true);
    }

    /// <summary>The chemicals some drug builds, by name.</summary>
    public IReadOnlyList<string> DrugChemicals() => [.. Drugs.Select(d => d.Chemical).Distinct().Order(StringComparer.Ordinal)];

    private List<Drug> DrugsWhere(Func<Drug, bool> keep) => [.. Drugs.Where(keep)];

    /// <summary>Reports whether def is a stone block def: one filed under the StoneBlocks thing category.</summary>
    public bool IsStoneBlocks(Resource def) => CategoriesOf(def).Contains(PolicyConstants.StoneBlocksCategory);

    /// <summary>
    /// The stone block def a stone build uses: the one stock holds most of, ties by name (so the first by name
    /// when stock holds none). A catalog with no stone block def throws.
    /// </summary>
    public Resource StoneBlock(IReadOnlyDictionary<Resource, long>? stock)
    {
        Resource best = default;
        var found = false;
        foreach (var def in Categories.Keys)
        {
            if (!IsStoneBlocks(def))
            {
                continue;
            }
            var have = stock?.GetValueOrDefault(def) ?? 0;
            var top = stock?.GetValueOrDefault(best) ?? 0;
            if (!found || have > top || (have == top && string.CompareOrdinal(def.Value, best.Value) < 0))
            {
                best = def;
                found = true;
            }
        }
        DomainException.Require(found, $"the catalog has no def in thing category {PolicyConstants.StoneBlocksCategory}");
        return best;
    }

    /// <summary>The market value of def.</summary>
    public double MarketValue(Resource def)
    {
        DomainException.Require(Market.TryGetValue(def, out var v), $"the catalog has no market value for {def.Value}");
        return v;
    }

    /// <summary>The market value of def, false when the catalog has none (Go: the error return).</summary>
    public bool TryMarketValue(Resource def, out double value) => Market.TryGetValue(def, out value);

    /// <summary>The stuff score, false when the catalog has no such stuff or no market value for it (Go: the error return).</summary>
    public bool TryStuffScore(Resource stuff, out double score)
    {
        if (StuffBeauty.TryGetValue(stuff, out var beauty) && Market.TryGetValue(stuff, out var value))
        {
            score = beauty * value;
            return true;
        }
        score = 0;
        return false;
    }

    /// <summary>Ranks a stuff by beauty factor times market value: the better the stuff for a room's beauty and wealth, the higher.</summary>
    public double StuffScore(Resource stuff)
    {
        DomainException.Require(StuffBeauty.TryGetValue(stuff, out var beauty), $"the catalog has no stuff {stuff.Value}");
        return beauty * MarketValue(stuff);
    }

    /// <summary>The stuffs def accepts, by name: those sharing a stuff category with it. A def not made from stuff has none.</summary>
    public IReadOnlyList<Resource> StuffsFor(Resource def)
    {
        var accepted = AcceptedStuff.GetValueOrDefault(def) ?? [];
        return [.. StuffCategories.Where(kv => kv.Value.Any(accepted.Contains)).Select(kv => kv.Key).OrderBy(r => r.Value, StringComparer.Ordinal)];
    }

    /// <summary>The lowest-priced medicine def and its market value: the one the routine trade buys.</summary>
    public (Resource Def, double Price) CheapestMedicine()
    {
        Resource best = default;
        double price = 0;
        foreach (var def in MedicineTiers())
        {
            var v = MarketValue(def);
            if (best.Value is null or "" || v < price)
            {
                best = def;
                price = v;
            }
        }
        DomainException.Require(best.Value is not (null or ""), "the catalog has no medicine");
        return (best, price);
    }

    /// <summary>The medicine defs by ascending medical potency, ties by name: the herbal tier first, the best last.</summary>
    public IReadOnlyList<Resource> MedicineTiers() =>
        [.. MedicalPotency.Keys.OrderBy(d => MedicalPotency[d]).ThenBy(d => d.Value, StringComparer.Ordinal)];

    /// <summary>
    /// The medicine of the given potency rank (0 the lowest), the game's own ranking: the care categories cap a
    /// pawn's medicine by potency (herbal-or-worse at rank 0, normal-or-worse at rank 1). Throws when the
    /// catalog has fewer medicines.
    /// </summary>
    public Resource MedicineAt(int rank)
    {
        var tiers = MedicineTiers();
        DomainException.Require(rank >= 0 && rank < tiers.Count, $"the catalog has no medicine of potency rank {rank} ({tiers.Count} medicines)");
        return tiers[rank];
    }

    /// <summary>The highest-potency medicine.</summary>
    public Resource BestMedicine() => MedicineAt(MedicalPotency.Count - 1);

    /// <summary>Reports whether def is a medicine.</summary>
    public bool IsMedicine(Resource def) => MedicalPotency.ContainsKey(def);

    /// <summary>The market value of one nutrition as the cheapest raw plant food: what the colony pays to buy food as ingredients.</summary>
    public double RawFoodNutritionPrice()
    {
        double best = 0;
        var found = false;
        foreach (var (def, categories) in Categories)
        {
            var nutrition = Nutrition.GetValueOrDefault(def);
            if (nutrition <= 0 || !Market.TryGetValue(def, out var value) || !categories.Contains("PlantFoodRaw"))
            {
                continue;
            }
            var price = value / nutrition;
            if (!found || price < best)
            {
                best = price;
                found = true;
            }
        }
        DomainException.Require(found, "the catalog has no raw plant food");
        return best;
    }
}

public static partial class PolicyConstants
{
    /// <summary>The thing category the game files every stone block def under.</summary>
    internal const string StoneBlocksCategory = "StoneBlocks";
}

/// <summary>One art recipe: the building it makes, the building's footprint, the stuff it takes and its work (the recipe's workAmount, else the product's WorkToMake).</summary>
public sealed record Sculpture(string Recipe, string Def, Cell Size, long Cost, double Work)
{
    /// <summary>The zero value Go's struct literal starts from.</summary>
    public Sculpture() : this("", "", default, 0, 0)
    {
    }
}

/// <summary>One drug def: the chemical it builds addiction to, whether the game files it as a social drug (IngestibleProperties.drugCategory) and whether it enhances combat (CompProperties_Drug.isCombatEnhancingDrug).</summary>
public sealed record Drug(Resource Def, string Chemical, bool Social = false, bool Combat = false);

/// <summary>What the colony needs to know of a chemical: whether its addiction hediff fades on its own severity per day, which is what a weaning plan relies on.</summary>
public sealed record Chemical(bool Weanable);

/// <summary>A drug whose hediff makes the taker immune to Diseases for Days (the hediff's disappearance time).</summary>
public sealed record Prevention(Resource Drug, double Days, EquatableArray<string> Diseases);
