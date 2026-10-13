using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Names what a production bill is for. A string-valued struct rather than an enum so each family that adds a
/// purpose (MaintainArt's ArtBill, the baby food bill) declares its own member next to its rule.
/// </summary>
public readonly record struct BillPurpose(string Value)
{
    public override string ToString() => Value;
}

public static partial class PolicyConstants
{
    public static readonly BillPurpose CookFood = new("cook");

    public static readonly BillPurpose PreserveFood = new("preserve");

    public static readonly BillPurpose ButcherFood = new("butcher");

    /// <summary>
    /// MaintainRefrigeration's answer to a solar flare: the warm perishable stock the dark coolers cannot save
    /// is cooked into meals on whichever bench still works (a fuelled stove; native's usable flag already
    /// excludes the unpowered electric one), so the colony eats it before it rots instead of waiting out the
    /// outage. The target is the at-risk nutrition beyond what existing bills already reserve, in meals; the
    /// caller gates it on the flare.
    /// </summary>
    public static readonly BillPurpose CookAheadFood = new("cook_ahead");
}

public sealed record ProductionProduct
{
    public string Name { get; init; } = "";

    public Fact<double> Nutrition { get; init; }

    public Fact<double> Demand { get; init; }

    public Fact<double> RotDays { get; init; }

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

    public Fact<double> Mood { get; init; }

    public Fact<double> NutrientEfficiency { get; init; }

    public Fact<double> WorkPerNutrition { get; init; }

    public Fact<EquatableArray<FoodIngredientSlot>> IngredientClasses { get; init; }

    public Fact<bool> NeedsPower { get; init; }

    public Fact<int> CookSkillFloor { get; init; }

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

    public Fact<bool> DefaultIngredients { get; init; }

    public Fact<EquatableArray<string>> Ingredients { get; init; }

    public Fact<bool> UnrestrictedWorker { get; init; }

    public Fact<string> Worker { get; init; }

    public Fact<string> RepeatMode { get; init; }

    public bool Humanlike { get; init; }

    public string Id { get; init; } = "";

    public Fact<bool> Managed { get; init; }

    public Fact<bool> Active { get; init; }

    public string Recipe { get; init; } = "";

    public Fact<int> TargetCount { get; init; }

    public Fact<bool> Forever { get; init; }
}

public sealed record ProductionBench
{
    public IReadOnlyList<HumanButcherCandidate> HumanButchers { get; init; } = [];

    public Fact<double> HumanCorpseNutrition { get; init; }

    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public Fact<string> Token { get; init; }

    public Fact<bool> Usable { get; init; }

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

    public string Token { get; init; } = "";

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

public static partial class PolicyFunctions
{
    /// <summary>
    /// Preserves unknown fields, but any observed drift is enough to correct a matching bill. Target bills
    /// must cover the planned minimum; finite repeat counts cannot maintain stock. Forever remains adequate.
    /// </summary>
    private static bool BillAdequate(ExistingProductionBill bill, BillSelection selection)
    {
        var ak = bill.Active.TryGetValue(out var active);
        var fk = bill.Forever.TryGetValue(out var forever);
        var tk = bill.TargetCount.TryGetValue(out var target);
        if (ak && !active)
        {
            return false;
        }
        if (bill.UnrestrictedWorker.TryGetValue(out var unrestricted) && !unrestricted)
        {
            return false;
        }
        if (bill.Worker.TryGetValue(out var worker) && worker != selection.Worker)
        {
            return false;
        }
        if (selection.Ingredients.Count == 0)
        {
            if (bill.DefaultIngredients.TryGetValue(out var defaults) && !defaults)
            {
                return false;
            }
        }
        else if (bill.Ingredients.TryGetValue(out var ingredients))
        {
            if (ingredients.Count != selection.Ingredients.Count)
            {
                return false;
            }
            var allowed = new HashSet<string>(ingredients, StringComparer.Ordinal);
            foreach (var name in selection.Ingredients)
            {
                if (!allowed.Contains(name))
                {
                    return false;
                }
            }
        }
        if (bill.RepeatMode.TryGetValue(out var mode) && mode != "TargetCount" && mode != "Forever")
        {
            return false;
        }
        if (selection.Mode is BillMode.ButcherForever or BillMode.HumanButcherForever)
        {
            return !fk || forever;
        }
        if (fk && forever)
        {
            return true;
        }
        return !tk || target >= selection.Target;
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
        Fact<long> colonists,
        Fact<double> runway,
        Fact<double> atRisk,
        double targetDays,
        params ProductionBillContext[] context)
    {
        var none = (new BillSelection(), false);
        var known = benches.TryGetValue(out var rows);
        var ck = colonists.TryGetValue(out var count);
        if (!known || !ck || count <= 0 || count > 256 || !FieldPositive(targetDays) || targetDays > 60)
        {
            return none;
        }
        if (purpose != PolicyConstants.CookFood && purpose != PolicyConstants.PreserveFood && purpose != PolicyConstants.ButcherFood
            && purpose != PolicyConstants.CookAheadFood && purpose != ArtBill && purpose != BabyFoodBill)
        {
            return none;
        }
        if (context.Length > 1)
        {
            return none;
        }
        if (purpose == BabyFoodBill)
        {
            if (context.Length != 1 || context[0].BabyFeeding is null)
            {
                return none;
            }
            return SelectBabyFoodBill(benches, context[0].BabyFeeding!);
        }
        if (purpose == ArtBill || (context.Length == 1 && context[0].Artists.Count > 0))
        {
            if (purpose != ArtBill || context.Length != 1 || context[0].Artists.Count != 1)
            {
                return none;
            }
            var demand = context[0].Art ?? new ArtDemand();
            return SelectArtBill(rows!, context[0].Artists[0], demand, out var art) ? (art, true) : none;
        }
        if (context.Length == 1 && ((context[0].Meals is not null && purpose != PolicyConstants.CookFood)
            || (context[0].Reserve is not null && purpose != PolicyConstants.PreserveFood)))
        {
            return none;
        }
        if (purpose == PolicyConstants.CookFood && context.Length == 1 && context[0].Meals is not null)
        {
            if (context[0].Meals!.TargetDays != targetDays)
            {
                return none;
            }
            return SelectMealBill(benches, colonists, context[0].Meals!, context[0].Ingredients);
        }
        if (purpose == PolicyConstants.PreserveFood)
        {
            if (context.Length != 1 || context[0].Reserve is null)
            {
                return none;
            }
            return SelectReserveBill(benches, context[0].Reserve!);
        }
        var (reserved, ok) = ReservedFoodNutrition(rows!);
        if (!ok)
        {
            reserved = 0;
        }
        var ahead = 0.0;
        if (purpose == PolicyConstants.CookAheadFood)
        {
            var rk = atRisk.TryGetValue(out var risk);
            if (!rk || !FieldPositive(risk) || risk > 1e6)
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
            if (!FoodId(bench.Id) || !seen.Add(bench.Id) || bench.Bills.Count > 15)
            {
                return none;
            }
            var uk = bench.Usable.TryGetValue(out var usable);
            var tk = bench.Token.TryGetValue(out var token);
            if (!uk || !usable || !tk || !FoodId(token!) || bench.Butcher != (purpose == PolicyConstants.ButcherFood))
            {
                continue;
            }
            var recipes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in bench.Recipes)
            {
                if (!FoodId(recipe.Name) || !recipes.Add(recipe.Name))
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
                    Token = token!,
                    Mode = BillMode.FoodTarget,
                    Target = (int)(count * 3),
                };
                if (context.Length == 1)
                {
                    selection = selection with { Ingredients = context[0].Ingredients };
                }
                if (purpose == PolicyConstants.ButcherFood)
                {
                    if (recipe.Role != RecipeRole.ButcherFlesh)
                    {
                        continue;
                    }
                    selection = selection with { Mode = BillMode.ButcherForever, Target = 0 };
                }
                if (purpose != PolicyConstants.CookAheadFood)
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
                if (purpose != PolicyConstants.ButcherFood)
                {
                    if (recipe.Products.Count != 1)
                    {
                        continue;
                    }
                    var product = recipe.Products[0];
                    var ek = product.Edible.TryGetValue(out var edible);
                    var nk = product.Nutrition.TryGetValue(out var nutrition);
                    if (!ek || !edible || !nk || !FieldPositive(nutrition))
                    {
                        continue;
                    }
                    if (purpose == PolicyConstants.CookAheadFood)
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
        if (purpose == PolicyConstants.ButcherFood)
        {
            separated = SeparatedButcherBenches(rows!);
        }
        var cooking = purpose == PolicyConstants.CookFood || purpose == PolicyConstants.CookAheadFood;
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
