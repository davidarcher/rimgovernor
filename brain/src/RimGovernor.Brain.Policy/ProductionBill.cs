using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Names what a production bill is for. A string-valued struct rather than an enum so each family that adds a
/// purpose (the rooms/art Need's ArtBill, the baby food bill) declares its own member next to its rule.
/// </summary>
public readonly record struct BillPurpose(string Value)
{
    public override string ToString() => Value;
}

public sealed record ProductionProduct
{
    public string Name { get; init; } = "";

    public Fact<double> Nutrition { get; init; }

    public Fact<bool> Edible { get; init; }

    public Fact<bool> Perishable { get; init; }

    /// <summary>Whether a baby can eat the product (Biotech).</summary>
    public Fact<bool> BabyEdible { get; init; }

    /// <summary>The food kind the game gives the product.</summary>
    public FoodKind Kind { get; init; }

    /// <summary>The count of the product protected, reachable storage accepting it can hold: stored stock plus empty stack slots.</summary>
    public Fact<long> Storable { get; init; }

    /// <summary>Reports a product the game classes as a meal.</summary>
    public bool Meal() => Kind is FoodKind.MealAwful or FoodKind.MealSimple or FoodKind.MealFine or FoodKind.MealLavish;

    /// <summary>A meal that never rots: the packaged survival meal, a reserve that is no part of the perishable meal tiers.</summary>
    public bool SurvivalMeal() => Meal() && Perishable.TryGetValue(out var perishable) && !perishable;

    /// <summary>The perishable simple-tier meal, the cheapest the cooks make.</summary>
    public bool SimpleMeal() => Kind == FoodKind.MealSimple && Perishable.TryGetValue(out var perishable) && perishable;
}

public sealed record ProductionRecipe
{
    public string Name { get; init; } = "";

    /// <summary>What the recipe does by its catalog row.</summary>
    public RecipeRole Role { get; init; }

    /// <summary>Whether the recipe is the bulk sibling of another recipe: the same ingredients and products in larger counts.</summary>
    public bool Bulk { get; init; }

    /// <summary>The PawnKindDef a gestation recipe makes, "" otherwise.</summary>
    public string MechKind { get; init; } = "";

    public Fact<bool> Available { get; init; }

    public IReadOnlyList<ProductionProduct> Products { get; init; } = [];

    /// <summary>What the recipe is worth as food, by the catalog (RecipeFood).</summary>
    public required RecipeFood Food { get; init; }

    /// <summary>Whether every product of the recipe is a simple meal.</summary>
    public bool SimpleMealRecipe() => Products.Count > 0 && Products.All(p => p.SimpleMeal());
}

/// <summary>
/// An existing bill. TargetCount/Forever describe the bill's own configured target, not how much of it is
/// already produced: a TargetCount bill reserves that nutrition toward its buffer even while still filling it.
/// </summary>
public sealed record ExistingProductionBill
{
    /// <summary>The bill recipe's role by its catalog row.</summary>
    public RecipeRole Role { get; init; }

    /// <summary>The ingredient filter is the one a new bill of the recipe gets.</summary>
    public required bool DefaultIngredients { get; init; }

    /// <summary>The filter's allowed defs; a butcher bill's filter is its corpse selection, never compared as an ingredient list.</summary>
    public required EquatableArray<string> Ingredients { get; init; }

    public required bool UnrestrictedWorker { get; init; }

    /// <summary>The pinned pawn, "" when unrestricted.</summary>
    public required string Worker { get; init; }

    public required BillRepeat Repeat { get; init; }

    public bool Humanlike { get; init; }

    public string Id { get; init; } = "";

    /// <summary>Not suspended.</summary>
    public required bool Active { get; init; }

    public string Recipe { get; init; } = "";

    /// <summary>The configured stock target (vanilla keeps it in every repeat mode; it binds only a TargetCount bill).</summary>
    public required int TargetCount { get; init; }
}

/// <summary>A bill's repeat mode: run forever, a fixed count of iterations, or until the stock reaches the target count.</summary>
public enum BillRepeat
{
    Forever,
    Count,
    TargetCount,
}

public sealed record ProductionBench
{
    public IReadOnlyList<HumanButcherCandidate> HumanButchers { get; init; } = [];

    public Fact<double> HumanCorpseNutrition { get; init; }

    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    /// <summary>The bench's CAS token.</summary>
    public required string Token { get; init; }

    public required bool Usable { get; init; }

    public bool Butcher { get; init; }

    /// <summary>The native room census identity the bench stands in.</summary>
    public Fact<string> Room { get; init; }

    public IReadOnlyList<ProductionRecipe> Recipes { get; init; } = [];

    public IReadOnlyList<ExistingProductionBill> Bills { get; init; } = [];
}

public sealed record BillSelection
{
    public IReadOnlyList<string> Ingredients { get; init; } = [];

    public string Worker { get; init; } = "";

    public string Bench { get; init; } = "";

    public string Recipe { get; init; } = "";

    public string Replace { get; init; } = "";

    public BillMode Mode { get; init; }

    public int Target { get; init; }
}

/// <summary>Carries purpose-specific reviewed inputs. Meal context is supplied by the food-plan owner; its absence preserves ordinary cooking.</summary>
public sealed record ProductionBillContext
{
    public IReadOnlyList<string> Ingredients { get; init; } = [];

    public FoodReserveReview? Reserve { get; init; }

    /// <summary>Sizes the BabyFoodBill selection.</summary>
    public BabyFeeding? BabyFeeding { get; init; }

    public MealTierRequest? Meals { get; init; }

    /// <summary>The one artist an ArtBill selection pins.</summary>
    public IReadOnlyList<PawnId> Artists { get; init; } = [];

    /// <summary>Sizes the ArtBill selection; null chooses a small sculpture in any stuff.</summary>
    public ArtDemand? Art { get; init; }
}

public static partial class ProductionRules
{
    public static readonly BillPurpose CookFood = new("cook");

    public static readonly BillPurpose PreserveFood = new("preserve");

    public static readonly BillPurpose ButcherFood = new("butcher");

    /// <summary>
    /// The food-stores/refrigeration Need's answer to a solar flare: the warm perishable stock the dark coolers cannot save
    /// is cooked into meals on whichever bench still works (a fuelled stove; native's usable flag already
    /// excludes the unpowered electric one), so the colony eats it before it rots instead of waiting out the
    /// outage. The target is the at-risk nutrition beyond what existing bills already reserve, in meals; the
    /// caller gates it on the flare.
    /// </summary>
    public static readonly BillPurpose CookAheadFood = new("cook_ahead");

    /// <summary>
    /// Any drift is enough to correct a matching bill. Target bills must cover the planned minimum; finite repeat
    /// counts cannot maintain stock. Forever remains adequate. A butcher bill's filter is its corpse selection, so
    /// its allowed defs are never compared with an ingredient list.
    /// </summary>
    internal static bool BillAdequate(ExistingProductionBill bill, BillSelection selection)
    {
        if (!bill.Active || !bill.UnrestrictedWorker || bill.Worker != selection.Worker)
        {
            return false;
        }
        if (selection.Ingredients.Count == 0)
        {
            if (!bill.DefaultIngredients)
            {
                return false;
            }
        }
        else if (bill.Role != RecipeRole.ButcherFlesh)
        {
            if (bill.Ingredients.Count != selection.Ingredients.Count)
            {
                return false;
            }
            var allowed = new HashSet<string>(bill.Ingredients, StringComparer.Ordinal);
            foreach (var name in selection.Ingredients)
            {
                if (!allowed.Contains(name))
                {
                    return false;
                }
            }
        }
        if (bill.Repeat == BillRepeat.Count)
        {
            return false;
        }
        if (selection.Mode is BillMode.ButcherForever or BillMode.HumanButcherForever || bill.Repeat == BillRepeat.Forever)
        {
            return bill.Repeat == BillRepeat.Forever;
        }
        return bill.TargetCount >= selection.Target;
    }

    /// <summary>
    /// An existing bill of the selected recipe stands when it already does the work; drift in suspension,
    /// quantity, ingredients or workers is corrected by guarded replacement regardless of ownership. A
    /// cook-ahead bill adds the meals the at-risk stock needs beyond every existing bill's reserved target, so
    /// a bench already cooking to a smaller target gets a second, larger bill for the outage.
    /// </summary>
    public static (BillSelection Selection, bool Ok) SelectProductionBill(
        BillPurpose purpose,
        Fact<EquatableArray<ProductionBench>> benches,
        long colonists,
        Fact<double> runway,
        Fact<double> atRisk,
        double targetDays,
        params ProductionBillContext[] context)
    {
        var none = (new BillSelection(), false);
        var known = benches.TryGetValue(out var rows);
        if (!known || colonists <= 0 || colonists > 256 || !FieldCapacityRules.FieldPositive(targetDays) || targetDays > 60)
        {
            return none;
        }
        if (purpose != ProductionRules.CookFood && purpose != ProductionRules.PreserveFood && purpose != ProductionRules.ButcherFood
            && purpose != ProductionRules.CookAheadFood && purpose != ArtRules.ArtBill && purpose != BabyFeedingRules.BabyFoodBill)
        {
            return none;
        }
        if (context.Length > 1)
        {
            return none;
        }
        if (purpose == BabyFeedingRules.BabyFoodBill)
        {
            if (context.Length != 1 || context[0].BabyFeeding is null)
            {
                return none;
            }
            return BabyFeedingRules.SelectBabyFoodBill(benches, context[0].BabyFeeding!);
        }
        if (purpose == ArtRules.ArtBill || (context.Length == 1 && context[0].Artists.Count > 0))
        {
            if (purpose != ArtRules.ArtBill || context.Length != 1 || context[0].Artists.Count != 1)
            {
                return none;
            }
            var demand = context[0].Art ?? new ArtDemand();
            return ArtRules.SelectArtBill(rows!, context[0].Artists[0], demand, out var art) ? (art, true) : none;
        }
        if (context.Length == 1 && ((context[0].Meals is not null && purpose != ProductionRules.CookFood)
            || (context[0].Reserve is not null && purpose != ProductionRules.PreserveFood)))
        {
            return none;
        }
        if (purpose == ProductionRules.CookFood && context.Length == 1 && context[0].Meals is not null)
        {
            if (context[0].Meals!.TargetDays != targetDays)
            {
                return none;
            }
            return MealRules.SelectMealBill(benches, colonists, context[0].Meals!, context[0].Ingredients);
        }
        if (purpose == ProductionRules.PreserveFood)
        {
            if (context.Length != 1 || context[0].Reserve is null)
            {
                return none;
            }
            return FoodReserveRules.SelectReserveBill(benches, context[0].Reserve!);
        }
        var (reserved, ok) = ReservedFoodNutrition(rows!);
        if (!ok)
        {
            reserved = 0;
        }
        var ahead = 0.0;
        if (purpose == ProductionRules.CookAheadFood)
        {
            var rk = atRisk.TryGetValue(out var risk);
            if (!rk || !FieldCapacityRules.FieldPositive(risk) || risk > 1e6)
            {
                return none;
            }
            ahead = risk - reserved;
            if (ahead <= 0)
            {
                return none;
            }
        }
        var options = new List<BillSelection>();
        var bulk = new Dictionary<string, bool>(StringComparer.Ordinal);
        var simple = new Dictionary<string, bool>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bench in rows!)
        {
            if (!FoodForecastRules.FoodId(bench.Id) || !seen.Add(bench.Id) || bench.Bills.Count > 15)
            {
                return none;
            }
            if (!bench.Usable || !FoodForecastRules.FoodId(bench.Token) || bench.Butcher != (purpose == ProductionRules.ButcherFood))
            {
                continue;
            }
            var recipes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in bench.Recipes)
            {
                if (!FoodForecastRules.FoodId(recipe.Name) || !recipes.Add(recipe.Name))
                {
                    return none;
                }
                var ak = recipe.Available.TryGetValue(out var available);
                if (!ak || !available)
                {
                    continue;
                }
                var selection = new BillSelection
                {
                    Bench = bench.Id,
                    Recipe = recipe.Name,
                    Mode = BillMode.FoodTarget,
                    Target = (int)(colonists * 3),
                };
                if (context.Length == 1)
                {
                    selection = selection with { Ingredients = context[0].Ingredients };
                }
                if (purpose == ProductionRules.ButcherFood)
                {
                    if (recipe.Role != RecipeRole.ButcherFlesh)
                    {
                        continue;
                    }
                    selection = selection with { Mode = BillMode.ButcherForever, Target = 0 };
                }
                if (purpose != ProductionRules.CookAheadFood)
                {
                    var exists = false;
                    foreach (var bill in bench.Bills)
                    {
                        if (bill.Recipe != recipe.Name || bill.Humanlike)
                        {
                            continue;
                        }
                        exists = true;
                        if (!BillAdequate(bill, selection) && bill.Id.Length != 0
                            && (selection.Replace.Length == 0 || string.CompareOrdinal(bill.Id, selection.Replace) < 0))
                        {
                            selection = selection with { Replace = bill.Id };
                        }
                    }
                    if (exists && selection.Replace.Length == 0)
                    {
                        continue;
                    }
                }
                if (purpose != ProductionRules.ButcherFood)
                {
                    if (recipe.Products.Count != 1)
                    {
                        continue;
                    }
                    var product = recipe.Products[0];
                    var ek = product.Edible.TryGetValue(out var edible);
                    var nk = product.Nutrition.TryGetValue(out var nutrition);
                    if (!ek || !edible || !nk || !FieldCapacityRules.FieldPositive(nutrition))
                    {
                        continue;
                    }
                    if (purpose == ProductionRules.CookAheadFood)
                    {
                        var target = Math.Ceiling(ahead / nutrition);
                        if (target < 1 || target > int.MaxValue)
                        {
                            continue;
                        }
                        selection = selection with { Target = (int)target };
                    }
                }
                if (bench.Bills.Count >= 15 && selection.Replace.Length == 0)
                {
                    continue;
                }
                bulk[recipe.Name] = recipe.Bulk;
                simple[recipe.Name] = recipe.SimpleMealRecipe();
                options.Add(selection);
            }
        }
        if (options.Count == 0)
        {
            return none;
        }
        // Butchery belongs away from the cooking workspace: a butcher bench standing in no cooking bench's room
        // wins over one that shares.
        IReadOnlyDictionary<string, bool> separated = new Dictionary<string, bool>();
        if (purpose == ProductionRules.ButcherFood)
        {
            separated = SeparatedButcherBenches(rows!);
        }
        var cooking = purpose == ProductionRules.CookFood || purpose == ProductionRules.CookAheadFood;
        options.Sort((a, b) =>
        {
            bool first = simple.GetValueOrDefault(a.Recipe), second = simple.GetValueOrDefault(b.Recipe);
            if (cooking && first != second)
            {
                return first ? -1 : 1;
            }
            bool sa = separated.GetValueOrDefault(a.Bench), sb = separated.GetValueOrDefault(b.Bench);
            if (sa != sb)
            {
                return sa ? -1 : 1;
            }
            bool ba = bulk.GetValueOrDefault(a.Recipe), bb = bulk.GetValueOrDefault(b.Recipe);
            if (ba != bb)
            {
                return ba ? -1 : 1;
            }
            var c = string.CompareOrdinal(a.Recipe, b.Recipe);
            return c != 0 ? c : string.CompareOrdinal(a.Bench, b.Bench);
        });
        return (options[0], true);
    }

    /// <summary>
    /// Reports, by ID, every butcher bench whose room is known and holds no cooking bench. A bench with an
    /// unknown room is never certified separated.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> SeparatedButcherBenches(IReadOnlyList<ProductionBench> benches)
    {
        var cooking = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bench in benches)
        {
            if (bench.Room.TryGetValue(out var room) && !bench.Butcher)
            {
                cooking.Add(room);
            }
        }
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var bench in benches)
        {
            if (bench.Room.TryGetValue(out var room) && bench.Butcher && !cooking.Contains(room))
            {
                result[bench.Id] = true;
            }
        }
        return result;
    }

    /// <summary>
    /// True when at least one butcher bench exists, every butcher bench's room is known, and none stands apart
    /// from cooking: the separated-spot build then owns the food-supply goal before any bill.
    /// </summary>
    public static bool AllButchersColocated(IReadOnlyList<ProductionBench> benches)
    {
        var separated = SeparatedButcherBenches(benches);
        var butchers = 0;
        foreach (var bench in benches)
        {
            if (!bench.Butcher)
            {
                continue;
            }
            if (!bench.Room.TryGetValue(out _) || separated.GetValueOrDefault(bench.Id))
            {
                return false;
            }
            butchers++;
        }
        return butchers > 0;
    }
}
