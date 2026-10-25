namespace RimGovernor.Brain.Policy;

/// <summary>
/// Proposes stock IDs for the shared supply-action planner. A proposal is not a forbid write or evidence that
/// food has been produced.
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

/// <summary>What the travel reserve's review found: kept off, no one to feed, or reviewed.</summary>
public abstract record FoodReserveState
{
    private protected FoodReserveState() { }

    /// <summary>The policy keeps no reserve (zero reserve days).</summary>
    public sealed record Disabled : FoodReserveState;

    /// <summary>No consumer eats, so no reserve is owed.</summary>
    public sealed record NoConsumers : FoodReserveState;

    /// <summary>The reserve against the consumers' daily demand.</summary>
    public sealed record Reviewed(FoodReserveReview Review) : FoodReserveState;
}

/// <summary>
/// The delivery times of the food plan's producing channels: Confirmed when every one has a known lead, else
/// Unconfirmed, which cannot authorize releasing stock.
/// </summary>
public abstract record FoodDeliveries
{
    private protected FoodDeliveries() { }

    /// <summary>The leads in days of every producing channel.</summary>
    public sealed record Confirmed(EquatableArray<double> Days) : FoodDeliveries;

    /// <summary>A producing channel's lead, or the food plan itself, is not known.</summary>
    public sealed record Unconfirmed : FoodDeliveries;
}

/// <summary>
/// What one standing target-count food bill wants: the products of its family, the product count that
/// satisfies it (native counts the product's stock toward the bill), and optionally one product ranked ahead of
/// the rest.
/// </summary>
internal sealed record TargetBillSpec
{
    public required Func<ProductionProduct, bool> Family { get; init; }

    public required Func<Resource, double, double> Target { get; init; }

    public Func<ProductionProduct, bool>? FirstIf { get; init; }
}

public static class FoodReserveRules
{
    public const double DefaultFoodReserveDays = 5.0;

    public static bool ReserveFoodDefinition(Resource def) => FoodPolicyRules.TravelReserve.Contains(def);

    /// <summary>The travel reserve's definition names.</summary>
    public static IReadOnlyList<string> ReserveFoodDefinitions() => [.. FoodPolicyRules.TravelReserve.Select(d => d.Value)];

    /// <summary>
    /// Removes supply rows the food reserve owns, so the admin/supply-safety Need never
    /// re-allows reserve food the food-stores/storage Need forbade: a stack the reserve is about to hold, or a forbidden
    /// reserve-food stack it keeps (anything outside Release). Without it both planners flipped the same
    /// Pemmican stack forever, each ending "no longer has the requested forbid state".
    /// </summary>
    public static IReadOnlyList<StartingSupply> DropReserveHeld(IReadOnlyList<StartingSupply> rows, FoodReserveState reserve)
    {
        if (reserve is not FoodReserveState.Reviewed { Review: var review })
        {
            return rows;
        }
        var hold = new HashSet<string>(review.Hold, StringComparer.Ordinal);
        var release = new HashSet<string>(review.Release, StringComparer.Ordinal);
        return [.. rows.Where(row => !(hold.Contains(row.Thing) || (!row.Forbid && !release.Contains(row.Thing) && ReserveFoodDefinition(new Resource(row.Definition)))))];
    }

    /// <summary>
    /// Counts shared reserve foods, roofed or not, at observed nutrition.
    /// deliveries holds the channel delivery times; unconfirmed ones cannot authorize releasing stock. The caller
    /// supplies the selected home census. NoConsumers when no consumer eats; throws
    /// <see cref="FoodFactsException"/> for facts that break the contract.
    /// </summary>
    public static FoodReserveState ReviewFoodReserve(FoodSupply supply, IReadOnlyList<PawnId>? selected, double reserveDays, double minimumDays, FoodDeliveries deliveries)
    {
        // Evaluate the runway without prospective reserve food too. Otherwise releasing the last reserve would
        // immediately make it eligible to hold again, alternating forbid writes while the colony is still short.
        var baseline = supply with
        {
            Reserved = supply.Reserved.Concat(supply.Stocks.Where(stock => stock.Holder.IsEmpty && ReserveFoodDefinition(new Resource(stock.DefName))).Select(stock => stock.Id)).ToHashSet(StringComparer.Ordinal),
        };
        var forecast = FoodForecastRules.ForecastFood(baseline, selected);
        if (!FieldCapacityRules.FieldPositive(reserveDays) || reserveDays > 60 || !FieldCapacityRules.FieldPositive(minimumDays) || minimumDays > 60)
        {
            throw new FoodFactsException();
        }
        double target = 0;
        foreach (var consumer in forecast.Consumers)
        {
            target += reserveDays * consumer.NutritionPerDay;
        }
        if (!FieldCapacityRules.FieldPositive(target))
        {
            return new FoodReserveState.NoConsumers();
        }
        // A positive target means some consumer eats, so the forecast has a runway.
        var days = forecast.RunwayDays is FoodRunway.Days { Value: var runway } ? runway : throw new FoodFactsException();
        var complete = deliveries is FoodDeliveries.Confirmed;
        var arriving = false;
        if (deliveries is FoodDeliveries.Confirmed { Days: var leads })
        {
            foreach (var lead in leads)
            {
                if (!FoodForecastRules.FoodNumber(lead))
                {
                    throw new FoodFactsException();
                }
                arriving = arriving || lead < days;
            }
        }
        var isShort = days < minimumDays;
        var emergency = complete && days < minimumDays && !arriving;
        var ordered = supply.Stocks.OrderBy(s => supply.IsReserved(s) ? 0 : 1).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        double stockNutrition = 0;
        var byDefinition = new Dictionary<Resource, double>();
        var hold = new List<string>();
        var release = new List<string>();
        foreach (var stock in ordered)
        {
            if (!ReserveFoodDefinition(new Resource(stock.DefName)) || stock.IsHumanMeat)
            {
                continue;
            }
            if (!stock.Holder.IsEmpty)
            {
                continue;
            }
            // The reserve is surplus insurance: below the seasonal minimum (reserve food excluded) every held stack is released to eat.
            if (emergency || days < minimumDays)
            {
                if (supply.IsReserved(stock))
                {
                    release.Add(stock.Id);
                }
                continue;
            }
            var amount = stock.Nutrition;
            if (stock.Rot is FoodRot.Rots { Ticks: var ticks } && (double)ticks / GameTime.TicksPerDay <= reserveDays)
            {
                continue;
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
            if (!supply.IsReserved(stock) && stockNutrition >= target)
            {
                continue;
            }
            stockNutrition += amount;
            byDefinition[new Resource(stock.DefName)] = byDefinition.GetValueOrDefault(new Resource(stock.DefName)) + amount;
            if (!supply.IsReserved(stock) && days >= minimumDays)
            {
                hold.Add(stock.Id);
            }
        }
        if (!FoodForecastRules.FoodNumber(stockNutrition))
        {
            throw new FoodFactsException();
        }
        hold.Sort(StringComparer.Ordinal);
        release.Sort(StringComparer.Ordinal);
        return new FoodReserveState.Reviewed(new FoodReserveReview
        {
            TargetNutrition = target,
            StockNutrition = stockNutrition,
            DeficitNutrition = Math.Max(0, target - stockNutrition),
            ByDefinition = byDefinition,
            Hold = hold,
            Release = release,
            Emergency = emergency,
            IsShort = isShort,
        });
    }

    /// <summary>
    /// Selects a standing native target-count bill. Its target includes existing
    /// stock of the selected product because native counts that stock toward satisfaction; only other reserve
    /// products reduce the target. The target never exceeds the product's observed storable count. No reserve
    /// bill is selected while the runway is short (the food is to be eaten, not cooked ahead). Matching bills are
    /// corrected under Auto; unrelated recipes are retained.
    /// </summary>
    public static (BillSelection Selection, bool Ok) SelectReserveBill(IReadOnlyList<ProductionBench> benches, FoodReserveReview reserve)
    {
        if (reserve.Emergency || reserve.IsShort || !FieldCapacityRules.FieldPositive(reserve.DeficitNutrition) || !FieldCapacityRules.FieldPositive(reserve.TargetNutrition))
        {
            return (new BillSelection(), false);
        }
        return SelectTargetBill(
            benches,
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
            if (!bench.Usable || !FoodForecastRules.FoodId(bench.Token) || !FoodForecastRules.FoodId(bench.Id) || bench.Butcher || bench.Bills.Count > 15)
            {
                continue;
            }
            foreach (var recipe in bench.Recipes)
            {
                if (!recipe.Available || !FoodForecastRules.FoodId(recipe.Name) || recipe.Products.Count != 1)
                {
                    continue;
                }
                var product = recipe.Products[0];
                var def = new Resource(product.Name);
                if (!spec.Family(product) || !product.EatenNutrition(out var nutrition) || !FieldCapacityRules.FieldPositive(nutrition))
                {
                    continue;
                }
                var target = Math.Ceiling(spec.Target(def, nutrition));
                // Native cannot finish a bill whose product has nowhere to go: cap the target at what storage accepting it can hold.
                if (product.Food is ProductFood.Food { Storable: var storable } && target > storable)
                {
                    target = storable;
                }
                if (!FieldCapacityRules.FieldPositive(target) || target > int.MaxValue)
                {
                    continue;
                }
                var selected = new BillSelection { Bench = bench.Id, Recipe = recipe.Name, Mode = BillMode.FoodTarget, Target = (int)target };
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
                        if (other.Id == bench.Id && !ProductionRules.BillAdequate(bill, selected) && FoodForecastRules.FoodId(bill.Id) && (selected.Replace.Length == 0 || string.CompareOrdinal(bill.Id, selected.Replace) < 0))
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
