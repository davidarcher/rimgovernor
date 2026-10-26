namespace RimGovernor.Brain.Policy;

// The food planners declare the production orders they want standing to the work ledger (OrderDeclarer), as the
// gear planners do (GearOrders.cs). A declaration is derived from the reviewed facts and the bench census alone:
// the census is read without its bills, so the same selection that once chose the next bill to place now chooses
// the order that should stand, every Round. A bill that already does the work is declared as it stands
// (AdequateOrder), which keeps it; any other food bill is an orphan.

/// <summary>Why a food declaration chose no order.</summary>
public enum FoodOrderGap
{
    /// <summary>At least one order was chosen.</summary>
    [WireName("")]
    Chosen = 0,


    /// <summary>The food plan does not yet support the capacity in Subject.</summary>
    [WireName("food_plan")]
    Plan,

    /// <summary>The purpose is not owed.</summary>
    [WireName("no_deficit")]
    NoDeficit,

    /// <summary>The purpose is owed but no bench and recipe can serve it.</summary>
    [WireName("no_bill")]
    NoBill,
}

/// <summary>
/// One food purpose's reviewed inputs. Benches is the census with its bills; Supply is the combined food supply
/// and Humans the human consumers' supply.
/// </summary>
public sealed record FoodOrderRequest
{
    public BillPurpose Purpose { get; init; }

    public required EquatableArray<ProductionBench> Benches { get; init; }

    public ReviewFacts Facts { get; init; } = new();

    public required FoodSupply Supply { get; init; }

    public required FoodSupply Humans { get; init; }

    /// <summary>The cooking purpose's meal request.</summary>
    public MealTierRequest? Meals { get; init; }

    /// <summary>The seasonal runway target both the cooking purpose and the other selections size against.</summary>
    public double TargetDays { get; init; }

    /// <summary>The cook-ahead purpose's at-risk nutrition (the refrigeration review's warm stock).</summary>
    public double Warm { get; init; }
}

/// <summary>The selection of one food purpose: the bills that should stand, with the bench each was chosen on.</summary>
public sealed record FoodOrder
{
    public IReadOnlyList<BillSelection> Selections { get; init; } = [];

    public FoodOrderGap Gap { get; init; }

    public string Subject { get; init; } = "";
}

public static class BillSelectionOrderExtensions
{
    /// <summary>The selection as the ledger identifies it on a bench of the kind.</summary>
    public static OrderSpec Order(this BillSelection s, string benchKind) =>
        new()
        {
            Recipe = s.Recipe,
            Ingredients = EquatableArray.From(s.Ingredients),
            Worker = s.Worker,
            Mode = s.Mode,
            Target = s.Target,
            BenchKind = benchKind,
        };
}

public static class FoodOrdersRules
{
    /// <summary>Whether the food plan opened or holds the channel.</summary>
    public static bool FoodPlanSupport(FoodPlan plan, CandidateKind kind, string id)
    {
        foreach (var entry in plan.Portfolio)
        {
            if (entry.Channel.Kind == kind && entry.Channel.Id == id)
            {
                return entry.Decision is FoodPlanDecision.Open or FoodPlanDecision.Hold;
            }
        }
        return false;
    }

    /// <summary>Copies the census with every bench's bills dropped: the selections then ignore what already stands, which the ledger reconciles.</summary>
    private static EquatableArray<ProductionBench> WithoutBills(EquatableArray<ProductionBench> benches) =>
        EquatableArray.From(benches.Select(b => b with { Bills = [] }));

    /// <summary>
    /// Chooses the food orders a purpose wants standing. A selector that cannot say
    /// (an unread fact, a food-plan channel not yet open) names the gap and chooses nothing.
    /// </summary>
    public static FoodOrder SelectFoodOrder(FoodOrderRequest r)
    {
        var f = r.Facts;
        var census = WithoutBills(r.Benches);
        var selections = new List<BillSelection>();
        FoodOrder Gap(FoodOrderGap g, string subject) => new() { Gap = g, Subject = subject };
        (BillSelection, bool) Bill(BillPurpose purpose, double atRisk, params ProductionBillContext[] context) =>
            ProductionRules.SelectProductionBill(purpose, census, f.Census.ColonistCount, atRisk, r.TargetDays, context);
        switch (r.Purpose)
        {
            case var purpose when purpose == ProductionRules.CookFood:
            {
                if (!FoodPlanSupport(f.RequireFoodPlan(), CandidateKind.Cook, "cooking-capacity"))
                {
                    return Gap(FoodOrderGap.Plan, "cooking-capacity");
                }
                var context = new ProductionBillContext
                {
                    Meals = r.Meals ?? throw new RequiredFactException("cooking meal request"),
                    Ingredients = HumanFoodPlanRules.HumanCookingIngredients(r.Supply, r.Humans.Consumers, f.RequireFoodPlan(), HumanMeatRoute.Meals),
                };
                var (selected, known) = Bill(ProductionRules.CookFood, 0, context);
                if (known)
                {
                    selections.Add(selected);
                }
                break;
            }
            case var purpose when purpose == ProductionRules.PreserveFood:
            {
                if (!FoodPlanSupport(f.RequireFoodPlan(), CandidateKind.Reserve, "stock-protection"))
                {
                    return Gap(FoodOrderGap.Plan, "stock-protection");
                }
                if (f.FoodReserve is not FoodReserveState.Reviewed { Review: var reserve })
                {
                    // No reserve is kept or owed: the bills that already stand for it stay.
                    return Gap(FoodOrderGap.NoDeficit, "");
                }
                var (selected, known) = Bill(ProductionRules.PreserveFood, 0, new ProductionBillContext { Reserve = reserve });
                if (known)
                {
                    selections.Add(selected);
                }
                var (sale, ok) = HumanFoodPlanRules.SelectHumanSurvivalBill(census, r.Supply, f.RequireFoodPlan());
                if (ok)
                {
                    selections.Add(sale);
                }
                break;
            }
            case var purpose when purpose == ProductionRules.ButcherFood:
            {
                // Owed on the food runway alone: native offers no hunt row until a usable bench carries this bill.
                if (f.RequireFoodRunway().Reaches(r.TargetDays) && !HumanFoodPlanRules.HumanFoodPending(f.RequireFoodPlan()))
                {
                    return Gap(FoodOrderGap.NoDeficit, "");
                }
                var (selected, known) = Bill(ProductionRules.ButcherFood, 0);
                if (known)
                {
                    selections.Add(selected);
                }
                var (human, ok) = HumanFoodRules.SelectHumanButcher(census, f.Ideology, f.Preferences);
                if (ok)
                {
                    if (!FoodPlanSupport(f.RequireFoodPlan(), CandidateKind.Corpse, "human-butchery"))
                    {
                        return Gap(FoodOrderGap.Plan, "human-butchery");
                    }
                    selections.Add(human);
                }
                break;
            }
            case var purpose when purpose == ProductionRules.CookAheadFood:
            {
                // Only under a solar flare with a known remaining duration: the coolers are dark for the outage,
                // so the warm at-risk stock is cooked instead.
                if (!DisasterRules.PowerOutageHold(f.DisasterConditions))
                {
                    return Gap(FoodOrderGap.NoDeficit, "");
                }
                var (selected, known) = Bill(ProductionRules.CookAheadFood, r.Warm);
                if (known)
                {
                    selections.Add(selected);
                }
                break;
            }
            case var purpose when purpose != ProductionRules.CookFood && purpose != ProductionRules.PreserveFood && purpose != ProductionRules.ButcherFood && purpose != ProductionRules.CookAheadFood:
                throw new DomainException($"not a food bill purpose: {purpose}");
        }
        return new FoodOrder
        {
            Selections = selections,
            Gap = selections.Count == 0 ? FoodOrderGap.NoBill : FoodOrderGap.Chosen,
        };
    }

    /// <summary>
    /// A food purpose's wanted orders. Abstain while a fact the selection needs is
    /// unread, the food plan has not opened the capacity, or a standing bill's readback is unread. A purpose that
    /// is not owed, or has no bench to serve it, keeps the bills that already stand for it, as the owners did
    /// before the ledger: the food runway is the last thing to starve on a transient fact. A solar-flare
    /// cook-ahead is the exception, which ends with the flare.
    /// </summary>
    public static Declared DeclareFoodOrders(FoodOrderRequest r, IReadOnlyList<GearBench> benches)
    {
        var order = SelectFoodOrder(r);
        if (order.Gap == FoodOrderGap.Plan)
        {
            return WorkRules.Abstaining(Cause.UnreadFoodPlan);
        }
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in r.Benches)
        {
            kinds[b.Id] = b.Definition;
        }
        var orders = new List<OrderSpec>();
        foreach (var selected in order.Selections)
        {
            if (!kinds.TryGetValue(selected.Bench, out var kind) || kind.Length == 0)
            {
                return WorkRules.Abstaining(Cause.UnreadBenchDef);
            }
            orders.Add(AdequateOrder(selected.Order(kind), benches));
        }
        if (orders.Count > 0 || r.Purpose == ProductionRules.CookAheadFood)
        {
            return new Declared { Orders = orders };
        }
        return new Declared { Orders = StandingOrders(benches, FoodFamily(r.Purpose)) };
    }

    /// <summary>Whether a bill belongs to a purpose's family.</summary>
    private static Func<GearBill, bool> FoodFamily(BillPurpose purpose) =>
        purpose == ProductionRules.CookFood ? (b => b.Role == RecipeRole.OrdinaryMeal)
        : purpose == ProductionRules.ButcherFood ? (b => b.Role == RecipeRole.ButcherFlesh)
        : purpose == ProductionRules.PreserveFood ? (b => b.Products.Any(FoodReserveRules.ReserveFoodDefinition))
        : (_ => false);

    /// <summary>The specs of the unspent production bills of a family, as they stand.</summary>
    private static List<OrderSpec> StandingOrders(IReadOnlyList<GearBench> benches, Func<GearBill, bool> family)
    {
        var orders = new List<OrderSpec>();
        foreach (var b in benches)
        {
            foreach (var bill in b.Bills)
            {
                if (bill.Spent || bill.Kind != LedgerBillKind.Production || !family(bill))
                {
                    continue;
                }
                orders.Add(bill.Spec);
            }
        }
        return orders;
    }

    /// <summary>
    /// A stock-target order's declaration: a bill that already stands on the order's bench kind and recipe, with
    /// its pin and ingredients and a target that covers the wanted one, is declared as it stands, so a target
    /// that moves with the colony does not replace the bill every Round. Any other order is declared as wanted.
    /// </summary>
    internal static OrderSpec AdequateOrder(OrderSpec want, IReadOnlyList<GearBench> benches)
    {
        if (WorkRules.WireClass(want.Mode) != BillMode.StockTarget)
        {
            return want;
        }
        OrderSpec? best = null;
        var bestId = "";
        foreach (var b in benches)
        {
            foreach (var bill in b.Bills)
            {
                var spec = bill.Spec;
                if (bill.Spent || bill.Kind != LedgerBillKind.Production || spec.BenchKind != want.BenchKind || spec.Recipe != want.Recipe
                    || WorkRules.WireClass(spec.Mode) != BillMode.StockTarget || spec.Worker != want.Worker || spec.Target < want.Target
                    || !SameSet(spec.Ingredients, want.Ingredients))
                {
                    continue;
                }
                if (best is null || spec.Target < best.Target || (spec.Target == best.Target && string.CompareOrdinal(bill.Id, bestId) < 0))
                {
                    best = spec;
                    bestId = bill.Id;
                }
            }
        }
        return best ?? want;
    }

    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>The native food products that can replace nutrition lost to spoilage, with no identity allowlist.</summary>
    private static bool StorageFood(ProductionProduct product) => product.Food is ProductFood.Food { Perishable: false, Edible: true };

    /// <summary>
    /// Preserve a nutrition floor using the shared native target-bill selector. Native product nutrition converts
    /// the deficit to item units; observed accepting storage caps it. Work per output nutrition compares recipes.
    /// These standing food bills are not finite material reservations; the shared native activity/admission path
    /// still owns ingredient choice, stock and pawn work when an iteration actually runs.
    /// </summary>
    public static Declared FoodStorageOrders(double nutrition, IReadOnlyList<ProductionBench> production,
        IReadOnlyList<GearBench> benches, FoodSupply supply, IReadOnlyList<MealCook> cooks, IReadOnlyList<string> ingredients)
    {
        if (!FieldCapacityRules.FieldPositive(nutrition)) throw new RequiredFactException("food storage nutrition deficit");
        if (cooks.Any(c => !FoodForecastRules.FoodId(c.Pawn.Value) || c.Skill is < 0 or > 20))
            throw new RequiredFactException("food storage cooks");
        var skill = cooks.Select(c => c.Skill).DefaultIfEmpty(-1).Max();
        var sources = supply.Stocks.Where(s => s.Nutrition > 0 && s.Holder.IsEmpty && !s.Forbidden && !s.IsCorpse()
                && !supply.IsReserved(s) && (!s.IsHumanMeat || ingredients.Contains(s.DefName)))
            .Select(s => s.RawClass).Where(c => c is not (FoodIngredientClass.None or FoodIngredientClass.Any)).ToHashSet();
        var eligible = production.Select(b => b with { Bills = [], Recipes = b.Recipes.Where(r =>
            r.Products.Count == 1 && StorageFood(r.Products[0]) && r.Food is RecipeFood.Food { Cooking: RecipeCooking.FromRaw }).ToList() }).ToList();
        foreach (var recipe in eligible.SelectMany(b => b.Recipes))
        {
            var food = (ProductFood.Food)recipe.Products[0].Food;
            if (!FieldCapacityRules.FieldPositive(food.Nutrition) || food.Storable < 0)
                throw new RequiredFactException("food storage product:" + food.Definition);
            if (recipe.Food is not RecipeFood.Food { WorkPerNutrition: var work } || !double.IsFinite(work) || work < 0)
                throw new RequiredFactException("food storage recipe work:" + recipe.Name);
        }
        eligible = eligible.Select(b => b with { Recipes = b.Recipes.Where(r => r.Food is RecipeFood.Food
            { Cooking: RecipeCooking.FromRaw { CookSkillFloor: var floor, IngredientClasses: var slots } }
            && skill >= floor && MealRules.MealSlotsSupported(slots, sources)).ToList() }).ToList();
        var (selected, ok) = FoodReserveRules.SelectTargetBill(eligible, new TargetBillSpec
        {
            Family = StorageFood,
            Target = (_, unitNutrition) => nutrition / unitNutrition,
            Cost = r => ((RecipeFood.Food)r.Food).WorkPerNutrition,
        });
        if (!ok) return new Declared();
        var bench = eligible.Single(b => b.Id == selected.Bench);
        var product = bench.Recipes.Single(r => r.Name == selected.Recipe).Products[0];
        var spec = (selected with { Ingredients = ingredients }).Order(bench.Definition) with { Product = new Resource(product.Name) };
        return new Declared { Orders = [AdequateOrder(spec, benches) with { Product = spec.Product }] };
    }

    /// <summary>Keep standing preservation bills during reserve access or larder work, identified by native products.</summary>
    public static Declared StandingStorageOrders(IReadOnlyList<ProductionBench> production, IReadOnlyList<GearBench> benches)
    {
        var recipes = production.SelectMany(b => b.Recipes).Where(r => r.Products.Count == 1 && StorageFood(r.Products[0]))
            .Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        return new Declared { Orders = StandingOrders(benches, b => recipes.Contains(b.Spec.Recipe)) };
    }
}
