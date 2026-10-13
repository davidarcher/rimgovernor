namespace RimGovernor.Brain.Policy;

/// <summary>
/// Proposes stock IDs for the shared supply-action planner. A proposal is not a forbid write or evidence that
/// food has been produced. (Go <c>FoodReserveReview</c>.)
/// </summary>
public sealed record FoodReserveReview
{
    public double TargetNutrition { get; init; }

    public double StockNutrition { get; init; }

    public double DeficitNutrition { get; init; }

    public IReadOnlyDictionary<Resource, double> ByDefinition { get; init; } = new Dictionary<Resource, double>();

    public IReadOnlyList<string> Hold { get; init; } = [];

    public IReadOnlyList<string> Release { get; init; } = [];

    public bool Emergency { get; init; }

    /// <summary>The runway without reserve food is under the seasonal minimum, so the reserve is food to eat, not insurance to keep.</summary>
    public bool IsShort { get; init; }
}

/// <summary>
/// What one standing target-count food bill wants: the products of its family, the product count that
/// satisfies it (native counts the product's stock toward the bill), and optionally one product ranked ahead of
/// the rest. (Go <c>targetBillSpec</c>.)
/// </summary>
internal sealed record TargetBillSpec
{
    public required Func<ProductionProduct, bool> Family { get; init; }

    public required Func<Resource, double, double> Target { get; init; }

    public Func<ProductionProduct, bool>? FirstIf { get; init; }
}

public static partial class PolicyFunctions
{
    public const double DefaultFoodReserveDays = 5.0;

    public static bool ReserveFoodDefinition(Resource def) => TravelReserve.Contains(def);

    /// <summary>Go <c>ReserveFoodDefinitions</c>: the travel reserve's definition names.</summary>
    public static IReadOnlyList<string> ReserveFoodDefinitions() => [.. TravelReserve.Select(d => d.Value)];

    /// <summary>
    /// Go <c>DropReserveHeld</c>: removes supply rows the food reserve owns, so the supplies planner never
    /// re-allows reserve food MaintainFoodStorage forbade: a stack the reserve is about to hold, or a forbidden
    /// reserve-food stack it keeps (anything outside Release). Without it both planners flipped the same
    /// Pemmican stack forever, each ending "no longer has the requested forbid state".
    /// </summary>
    public static IReadOnlyList<StartingSupply> DropReserveHeld(IReadOnlyList<StartingSupply> rows, Fact<FoodReserveReview> reserve)
    {
        if (!reserve.TryGetValue(out var review))
        {
            return rows;
        }
        var hold = new HashSet<string>(review.Hold, StringComparer.Ordinal);
        var release = new HashSet<string>(review.Release, StringComparer.Ordinal);
        return [.. rows.Where(row => !(hold.Contains(row.Thing) || (!row.Forbid && !release.Contains(row.Thing) && ReserveFoodDefinition(new Resource(row.Definition)))))];
    }

    /// <summary>
    /// Go <c>ReviewFoodReserve</c>: counts shared reserve foods, roofed or not, at observed nutrition.
    /// deliveryDays contains confirmed channel delivery times; unknown channel facts cannot authorize releasing
    /// stock. The caller supplies the selected home census. Throws <see cref="FoodFactsException"/> where Go
    /// returned ErrFoodFacts.
    /// </summary>
    public static FoodReserveReview ReviewFoodReserve(FoodSupply supply, IReadOnlyList<PawnId>? selected, double reserveDays, double minimumDays, Fact<EquatableArray<double>> deliveryDays)
    {
        // Evaluate the runway without prospective reserve food too. Otherwise releasing the last reserve would
        // immediately make it eligible to hold again, alternating forbid writes while the colony is still short.
        var baseline = supply with
        {
            Stocks =
            [
                .. supply.Stocks.Select(stock =>
                    stock.Holder.TryGetValue(out var holder) && holder.IsEmpty && ReserveFoodDefinition(stock.DefName) ? stock with { Reserve = true } : stock
                ),
            ],
        };
        var forecast = ForecastFood(baseline, selected);
        if (!FieldPositive(reserveDays) || reserveDays > 60 || !FieldPositive(minimumDays) || minimumDays > 60)
        {
            throw new FoodFactsException();
        }
        double target = 0;
        foreach (var consumer in forecast.Consumers)
        {
            target += reserveDays * consumer.NutritionPerDay;
        }
        if (!FieldPositive(target))
        {
            throw new FoodFactsException();
        }
        var known = forecast.RunwayDays.TryGetValue(out var days);
        var complete = deliveryDays.TryGetValue(out var deliveries);
        var arriving = false;
        foreach (var lead in deliveries)
        {
            if (!FoodNumber(lead))
            {
                throw new FoodFactsException();
            }
            arriving = arriving || lead < days;
        }
        var isShort = known && days < minimumDays;
        var emergency = known && complete && days < minimumDays && !arriving;
        var ordered = supply.Stocks.OrderBy(s => s.Reserve ? 0 : 1).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        double stockNutrition = 0;
        var byDefinition = new Dictionary<Resource, double>();
        var hold = new List<string>();
        var release = new List<string>();
        foreach (var stock in ordered)
        {
            if (!ReserveFoodDefinition(stock.DefName) || stock.IsHumanMeat)
            {
                continue;
            }
            if (!stock.Holder.TryGetValue(out var holder) || !holder.IsEmpty)
            {
                continue;
            }
            // The reserve is surplus insurance: below the seasonal minimum (reserve food excluded) every held stack is released to eat.
            if (emergency || (known && days < minimumDays))
            {
                if (stock.Reserve)
                {
                    release.Add(stock.Id);
                }
                continue;
            }
            var amount = stock.Nutrition.ValueOrDefault;
            if (stock.Perishable.ValueOrDefault)
            {
                var ticks = stock.RotTicks.ValueOrDefault;
                if ((double)ticks / GameTime.TicksPerDay <= reserveDays)
                {
                    continue;
                }
            }
            var eligible = false;
            foreach (var eater in stock.Eaters)
            {
                foreach (var consumer in forecast.Consumers)
                {
                    eligible = eligible || eater == consumer.Id;
                }
            }
            if (!eligible)
            {
                continue;
            }
            // Supply actions address whole stacks. Hold only enough stacks to cover the target; the final stack may round the reserve upward.
            if (!stock.Reserve && stockNutrition >= target)
            {
                continue;
            }
            stockNutrition += amount;
            byDefinition[stock.DefName] = byDefinition.GetValueOrDefault(stock.DefName) + amount;
            if (!stock.Reserve && known && days >= minimumDays)
            {
                hold.Add(stock.Id);
            }
        }
        if (!FoodNumber(stockNutrition))
        {
            throw new FoodFactsException();
        }
        hold.Sort(StringComparer.Ordinal);
        release.Sort(StringComparer.Ordinal);
        return new FoodReserveReview
        {
            TargetNutrition = target,
            StockNutrition = stockNutrition,
            DeficitNutrition = Math.Max(0, target - stockNutrition),
            ByDefinition = byDefinition,
            Hold = hold,
            Release = release,
            Emergency = emergency,
            IsShort = isShort,
        };
    }

    /// <summary>Go <c>ReserveBillRunning</c>: reports an active bill for a reserve product while the reserve is short: the product it makes needs game time, not another method.</summary>
    public static bool ReserveBillRunning(Fact<EquatableArray<ProductionBench>> benches, FoodReserveReview reserve)
    {
        if (!benches.TryGetValue(out var rows) || reserve.Emergency || !FieldPositive(reserve.DeficitNutrition))
        {
            return false;
        }
        foreach (var bench in rows)
        {
            var products = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in bench.Recipes)
            {
                if (recipe.Products.Count == 1 && ReserveFoodDefinition(new Resource(recipe.Products[0].Name)))
                {
                    products.Add(recipe.Name);
                }
            }
            foreach (var bill in bench.Bills)
            {
                if (bill.Active.TryGetValue(out var active) && active && products.Contains(bill.Recipe))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Go <c>SelectReserveBill</c>: selects a standing native target-count bill. Its target includes existing
    /// stock of the selected product because native counts that stock toward satisfaction; only other reserve
    /// products reduce the target. The target never exceeds the product's observed storable count. No reserve
    /// bill is selected while the runway is short (the food is to be eaten, not cooked ahead). Matching bills are
    /// corrected under Auto; unrelated recipes are retained.
    /// </summary>
    public static (BillSelection Selection, bool Ok) SelectReserveBill(Fact<EquatableArray<ProductionBench>> benches, FoodReserveReview reserve)
    {
        if (!benches.TryGetValue(out var rows) || reserve.Emergency || reserve.IsShort || !FieldPositive(reserve.DeficitNutrition) || !FieldPositive(reserve.TargetNutrition))
        {
            return (new BillSelection(), false);
        }
        return SelectTargetBill(
            rows,
            new TargetBillSpec
            {
                Family = p => ReserveFoodDefinition(new Resource(p.Name)),
                Target = (def, nutrition) => (reserve.TargetNutrition - reserve.StockNutrition + reserve.ByDefinition.GetValueOrDefault(def)) / nutrition,
                FirstIf = p => p.SurvivalMeal(),
            }
        );
    }

    /// <summary>
    /// Picks the recipe, bench and target for a standing target-count bill of a product family. A recipe whose
    /// product another recipe's bill already makes needs no bill of its own (Make_Pemmican and Make_PemmicanBulk
    /// are one reserve); the bulk recipe wins over its single-item sibling. The target never exceeds the
    /// product's storable count; matching bills are corrected, unrelated recipes retained.
    /// </summary>
    internal static (BillSelection Selection, bool Ok) SelectTargetBill(IReadOnlyList<ProductionBench> rows, TargetBillSpec spec)
    {
        var options = new List<BillSelection>();
        var first = new Dictionary<string, bool>(StringComparer.Ordinal);
        var bulk = new Dictionary<string, bool>(StringComparer.Ordinal);
        var produces = new Dictionary<string, Resource>(StringComparer.Ordinal);
        foreach (var bench in rows)
        {
            foreach (var recipe in bench.Recipes)
            {
                if (recipe.Products.Count == 1 && spec.Family(recipe.Products[0]))
                {
                    produces[recipe.Name] = new Resource(recipe.Products[0].Name);
                }
            }
        }
        foreach (var bench in rows)
        {
            var uk = bench.Usable.TryGetValue(out var usable);
            var tk = bench.Token.TryGetValue(out var token);
            if (!uk || !usable || !tk || !FoodId(token!) || !FoodId(bench.Id) || bench.Butcher || bench.Bills.Count > 15)
            {
                continue;
            }
            foreach (var recipe in bench.Recipes)
            {
                var ak = recipe.Available.TryGetValue(out var available);
                if (!ak || !available || !FoodId(recipe.Name) || recipe.Products.Count != 1)
                {
                    continue;
                }
                var product = recipe.Products[0];
                var def = new Resource(product.Name);
                var nk = product.Nutrition.TryGetValue(out var nutrition);
                var ek = product.Edible.TryGetValue(out var edible);
                if (!spec.Family(product) || !nk || !FieldPositive(nutrition) || !ek || !edible)
                {
                    continue;
                }
                var target = Math.Ceiling(spec.Target(def, nutrition));
                // Native cannot finish a bill whose product has nowhere to go: cap the target at what storage accepting it can hold.
                if (product.Storable.TryGetValue(out var storable) && target > storable)
                {
                    target = storable;
                }
                if (!FieldPositive(target) || target > int.MaxValue)
                {
                    continue;
                }
                var selected = new BillSelection { Bench = bench.Id, Recipe = recipe.Name, Token = token!, Mode = BillMode.FoodTarget, Target = (int)target };
                var exists = false;
                var siblingBill = false;
                foreach (var other in rows)
                {
                    foreach (var bill in other.Bills)
                    {
                        if (bill.Recipe != recipe.Name)
                        {
                            if (produces.TryGetValue(bill.Recipe, out var sibling) && sibling == def && !exists)
                            {
                                siblingBill = true;
                            }
                            continue;
                        }
                        exists = true;
                        if (other.Id == bench.Id && !BillAdequate(bill, selected) && FoodId(bill.Id) && (selected.Replace.Length == 0 || string.CompareOrdinal(bill.Id, selected.Replace) < 0))
                        {
                            selected = selected with { Replace = bill.Id };
                        }
                    }
                }
                if (siblingBill && !exists)
                {
                    continue;
                }
                if ((exists && selected.Replace.Length == 0) || (bench.Bills.Count == 15 && selected.Replace.Length == 0))
                {
                    continue;
                }
                first[recipe.Name] = spec.FirstIf is not null && spec.FirstIf(product);
                bulk[recipe.Name] = recipe.Bulk;
                options.Add(selected);
            }
        }
        if (options.Count == 0)
        {
            return (new BillSelection(), false);
        }
        // The bulk recipe wins over its single-item sibling: singles are for the odd one or two items a player cranks out by hand.
        var best = options
            .OrderBy(o => spec.FirstIf is not null && first.GetValueOrDefault(o.Recipe) ? 0 : 1)
            .ThenBy(o => bulk.GetValueOrDefault(o.Recipe) ? 0 : 1)
            .ThenBy(o => o.Recipe, StringComparer.Ordinal)
            .ThenBy(o => o.Bench, StringComparer.Ordinal)
            .First();
        return (best, true);
    }
}
