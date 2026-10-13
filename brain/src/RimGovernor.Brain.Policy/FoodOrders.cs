namespace RimGovernor.Brain.Policy;

// The food planners declare the production orders they want standing to the work ledger (OrderDeclarer), as the
// gear planners do (GearOrders.cs). A declaration is derived from the reviewed facts and the bench census alone:
// the census is read without its bills, so the same selection that once chose the next bill to place now chooses
// the order that should stand, every Round. A bill that already does the work is declared as it stands
// (AdequateOrder), which keeps it; any other food bill is an orphan.

/// <summary>Why a food declaration chose no order. (Go <c>FoodOrderGap</c>.)</summary>
public enum FoodOrderGap
{
    /// <summary>At least one order was chosen.</summary>
    [WireName("")]
    Chosen = 0,

    /// <summary>Subject names a fact the declaration needs and lacks.</summary>
    [WireName("field")]
    Field,

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
/// and Humans the human consumers' supply. (Go <c>FoodOrderRequest</c>.)
/// </summary>
public sealed record FoodOrderRequest
{
    public BillPurpose Purpose { get; init; }

    public Fact<EquatableArray<ProductionBench>> Benches { get; init; }

    public RoundsFacts Facts { get; init; } = new();

    public Fact<FoodSupply> Supply { get; init; }

    public Fact<FoodSupply> Humans { get; init; }

    /// <summary>The cooking purpose's meal request.</summary>
    public MealTierRequest Meals { get; init; } = new();

    /// <summary>The seasonal runway target both the cooking purpose and the other selections size against.</summary>
    public double TargetDays { get; init; }

    /// <summary>The cook-ahead purpose's at-risk nutrition (the refrigeration review's warm stock).</summary>
    public Fact<double> Warm { get; init; }
}

/// <summary>The selection of one food purpose: the bills that should stand, with the bench each was chosen on. (Go <c>FoodOrder</c>.)</summary>
public sealed record FoodOrder
{
    public IReadOnlyList<BillSelection> Selections { get; init; } = [];

    public FoodOrderGap Gap { get; init; }

    public string Subject { get; init; } = "";

    /// <summary>
    /// Whether a standing reserve bill is cooking while the reserve is short: the product needs game time, not
    /// another method.
    /// </summary>
    public bool ReserveRunning { get; init; }
}

public static class BillSelectionOrderExtensions
{
    /// <summary>Go <c>BillSelection.Order</c>: the selection as the ledger identifies it on a bench of the kind.</summary>
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

public static partial class PolicyFunctions
{
    /// <summary>Go <c>FoodPlanSupport</c>: whether the food plan opened or holds the channel.</summary>
    public static bool FoodPlanSupport(Fact<FoodPlan> p, CandidateKind kind, string id)
    {
        if (!p.TryGetValue(out var plan))
        {
            return false;
        }
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
    private static Fact<EquatableArray<ProductionBench>> WithoutBills(Fact<EquatableArray<ProductionBench>> benches)
    {
        if (!benches.TryGetValue(out var rows))
        {
            return benches;
        }
        return Known<EquatableArray<ProductionBench>>([.. rows.Select(b => b with { Bills = [] })]);
    }

    /// <summary>
    /// Go <c>SelectFoodOrder</c>: chooses the food orders a purpose wants standing. A selector that cannot say
    /// (an unread fact, a food-plan channel not yet open) names the gap and chooses nothing.
    /// </summary>
    public static FoodOrder SelectFoodOrder(FoodOrderRequest r)
    {
        var f = r.Facts;
        var census = WithoutBills(r.Benches);
        var selections = new List<BillSelection>();
        var reserveRunning = false;
        FoodOrder Gap(FoodOrderGap g, string subject) => new() { Gap = g, Subject = subject, ReserveRunning = reserveRunning };
        switch (r.Purpose)
        {
            case var purpose when purpose == PolicyConstants.CookFood:
            {
                if (!FoodPlanSupport(f.FoodPlan, CandidateKind.Cook, "cooking-capacity"))
                {
                    return Gap(FoodOrderGap.Plan, "cooking-capacity");
                }
                var context = new ProductionBillContext { Meals = r.Meals };
                if (r.Supply.TryGetValue(out var supply) && r.Humans.TryGetValue(out var humans))
                {
                    context = context with { Ingredients = HumanCookingIngredients(supply, humans.Consumers, f.FoodPlan, HumanMeatRoute.Meals) };
                }
                var (selected, known) = SelectProductionBill(PolicyConstants.CookFood, census, f.Colonists, f.FoodDays, Unknown<double>(), r.TargetDays, context);
                if (known)
                {
                    selections.Add(selected);
                }
                break;
            }
            case var purpose when purpose == PolicyConstants.PreserveFood:
            {
                if (!FoodPlanSupport(f.FoodPlan, CandidateKind.Reserve, "stock-protection"))
                {
                    return Gap(FoodOrderGap.Plan, "stock-protection");
                }
                if (!f.FoodReserve.TryGetValue(out var reserve))
                {
                    return Gap(FoodOrderGap.Field, "food_reserve");
                }
                reserveRunning = ReserveBillRunning(r.Benches, reserve);
                var (selected, known) = SelectProductionBill(PolicyConstants.PreserveFood, census, f.Colonists, f.FoodDays, Unknown<double>(), r.TargetDays, new ProductionBillContext { Reserve = reserve });
                if (known)
                {
                    selections.Add(selected);
                }
                if (r.Supply.TryGetValue(out var supply))
                {
                    var (sale, ok) = SelectHumanSurvivalBill(census, supply, f.FoodPlan);
                    if (ok)
                    {
                        selections.Add(sale);
                    }
                }
                break;
            }
            case var purpose when purpose == PolicyConstants.ButcherFood:
            {
                // Owed on the food runway alone: native offers no hunt row until a usable bench carries this bill.
                var dk = f.FoodDays.TryGetValue(out var days);
                if ((!dk || days >= r.TargetDays) && !HumanFoodPending(f.FoodPlan))
                {
                    return dk ? Gap(FoodOrderGap.NoDeficit, "") : Gap(FoodOrderGap.Field, "food_days");
                }
                var (selected, known) = SelectProductionBill(PolicyConstants.ButcherFood, census, f.Colonists, f.FoodDays, Unknown<double>(), r.TargetDays);
                if (known)
                {
                    selections.Add(selected);
                }
                var (human, ok) = SelectHumanButcher(census, f.IdeologyRead());
                if (ok)
                {
                    if (!FoodPlanSupport(f.FoodPlan, CandidateKind.Corpse, "human-butchery"))
                    {
                        return Gap(FoodOrderGap.Plan, "human-butchery");
                    }
                    selections.Add(human);
                }
                break;
            }
            case var purpose when purpose == PolicyConstants.CookAheadFood:
            {
                // Only under a solar flare with a known remaining duration: the coolers are dark for the outage,
                // so the warm at-risk stock is cooked instead.
                if (!PowerOutageHold(f.DisasterConditions))
                {
                    return Gap(FoodOrderGap.NoDeficit, "");
                }
                var (selected, known) = SelectProductionBill(PolicyConstants.CookAheadFood, census, f.Colonists, f.FoodDays, r.Warm, r.TargetDays);
                if (known)
                {
                    selections.Add(selected);
                }
                break;
            }
            default:
                return Gap(FoodOrderGap.NoBill, "");
        }
        return new FoodOrder
        {
            Selections = selections,
            Gap = selections.Count == 0 ? FoodOrderGap.NoBill : FoodOrderGap.Chosen,
            ReserveRunning = reserveRunning,
        };
    }

    /// <summary>
    /// Go <c>DeclareFoodOrders</c>: a food purpose's wanted orders. Abstain while a fact the selection needs is
    /// unread, the food plan has not opened the capacity, or a standing bill's readback is unread. A purpose that
    /// is not owed, or has no bench to serve it, keeps the bills that already stand for it, as the owners did
    /// before the ledger: the food runway is the last thing to starve on a transient fact. A solar-flare
    /// cook-ahead is the exception, which ends with the flare.
    /// </summary>
    public static Declared DeclareFoodOrders(FoodOrderRequest r, IReadOnlyList<GearBench> benches)
    {
        var order = SelectFoodOrder(r);
        if (order.Gap == FoodOrderGap.Field)
        {
            return Abstaining(Cause.UnreadFoodFacts);
        }
        if (order.Gap == FoodOrderGap.Plan)
        {
            return Abstaining(Cause.UnreadFoodPlan);
        }
        _ = r.Benches.TryGetValue(out var census);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in census)
        {
            kinds[b.Id] = b.Definition;
        }
        var orders = new List<OrderSpec>();
        foreach (var selected in order.Selections)
        {
            if (!kinds.TryGetValue(selected.Bench, out var kind) || kind.Length == 0)
            {
                return Abstaining(Cause.UnreadBenchDef);
            }
            var (spec, known) = AdequateOrder(selected.Order(kind), benches);
            if (!known)
            {
                return Abstaining(Cause.UnreadBills);
            }
            orders.Add(spec);
        }
        if (orders.Count > 0 || r.Purpose == PolicyConstants.CookAheadFood)
        {
            return new Declared { Orders = orders };
        }
        var (standing, standingKnown) = StandingOrders(benches, FoodFamily(r.Purpose));
        return AbstainUnless(UnreadIf(!standingKnown, Cause.UnreadBills), standing);
    }

    /// <summary>Whether a bill belongs to a purpose's family.</summary>
    private static Func<GearBill, bool> FoodFamily(BillPurpose purpose) =>
        purpose == PolicyConstants.CookFood ? (b => b.Role == RecipeRole.OrdinaryMeal)
        : purpose == PolicyConstants.ButcherFood ? (b => b.Role == RecipeRole.ButcherFlesh)
        : purpose == PolicyConstants.PreserveFood ? (b => b.Products.Any(ReserveFoodDefinition))
        : (_ => false);

    /// <summary>The specs of the unspent production bills of a family, as they stand; known is false while one's readback is unread.</summary>
    private static (List<OrderSpec> Orders, bool Known) StandingOrders(IReadOnlyList<GearBench> benches, Func<GearBill, bool> family)
    {
        var orders = new List<OrderSpec>();
        foreach (var b in benches)
        {
            if (!b.Bills.TryGetValue(out var bills))
            {
                return ([], false);
            }
            foreach (var bill in bills)
            {
                if (bill.Spent || bill.Kind != LedgerBillKind.Production || !family(bill))
                {
                    continue;
                }
                if (!bill.Spec.TryGetValue(out var spec))
                {
                    return ([], false);
                }
                orders.Add(spec);
            }
        }
        return (orders, true);
    }

    /// <summary>
    /// A stock-target order's declaration: a bill that already stands on the order's bench kind and recipe, with
    /// its pin and ingredients and a target that covers the wanted one, is declared as it stands, so a target
    /// that moves with the colony does not replace the bill every Round. Any other order is declared as wanted.
    /// Known is false while a bill's readback is unread.
    /// </summary>
    internal static (OrderSpec Spec, bool Known) AdequateOrder(OrderSpec want, IReadOnlyList<GearBench> benches)
    {
        if (WireClass(want.Mode) != BillMode.StockTarget)
        {
            return (want, true);
        }
        OrderSpec? best = null;
        var bestId = "";
        foreach (var b in benches)
        {
            if (!b.Bills.TryGetValue(out var bills))
            {
                return (new OrderSpec(), false);
            }
            foreach (var bill in bills)
            {
                if (!bill.Spec.TryGetValue(out var spec))
                {
                    return (new OrderSpec(), false);
                }
                if (bill.Spent || bill.Kind != LedgerBillKind.Production || spec.BenchKind != want.BenchKind || spec.Recipe != want.Recipe
                    || WireClass(spec.Mode) != BillMode.StockTarget || spec.Worker != want.Worker || spec.Target < want.Target
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
        return (best ?? want, true);
    }

    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// Go <c>FoodStorageMealOrder</c>: MaintainFoodStorage's produce fallback as an order: the stock-target bill
    /// of the choice's resource on the first bench (by id) whose recipe makes it, chosen as if no bill stood.
    /// Unknown while the bench recipes are unread; Ok is false when no bench can make the resource.
    /// </summary>
    public static (OrderSpec Spec, MedicineMethodKind Kind, bool Ok) FoodStorageMealOrder(FoodStorageMethod choice, IReadOnlyList<GearBench> benches)
    {
        var bare = benches.Select(b => b with { Bills = Known<EquatableArray<GearBill>>([]) }).ToList();
        var picked = SelectMedicineMethod(
            new MedicinePlanningRequest
            {
                Review = new MedicalReserveReview { Active = true, Target = Known(choice.Target) },
                Resource = choice.Resource,
                Benches = Known(EquatableArray.From(bare)),
            }
        );
        if (picked.Kind != MedicineMethodKind.Produce)
        {
            return (new OrderSpec(), picked.Kind, false);
        }
        var def = "";
        foreach (var b in benches)
        {
            if (b.Id == picked.Bench)
            {
                def = b.Def;
            }
        }
        var spec = new OrderSpec { Recipe = picked.Recipe, Mode = BillMode.StockTarget, Target = (int)picked.Target, BenchKind = def };
        spec = AdequateOrder(spec, benches).Spec;
        return (spec, picked.Kind, def.Length != 0);
    }

    /// <summary>Go <c>StandingStockOrders</c>: the unspent bills that make the resource, declared as they stand; Abstain while a bill readback is unread.</summary>
    public static Declared StandingStockOrders(IReadOnlyList<GearBench> benches, Resource resource)
    {
        var (orders, known) = StandingOrders(benches, b => b.Products.Contains(resource));
        return AbstainUnless(UnreadIf(!known, Cause.UnreadBills), orders);
    }
}
