namespace RimGovernor.Brain.Policy;

// Go: trade_food.go.

/// <summary>
/// Comes from the shared food review, not a second plan made by trade. DesiredIngredients describes the desired
/// recipe's conjunctive slots; IngredientNutrition is the nutrition to buy for each missing slot. Unknown facts
/// leave food trading disabled without suppressing other needs.
/// </summary>
public sealed record TradeFoodContext
{
    public Fact<FoodPlan> Plan { get; init; }

    public Fact<double> RunwayDays { get; init; }

    public double MinDays { get; init; }

    public double TargetDays { get; init; }

    public Fact<EquatableArray<FoodIngredientSlot>> DesiredIngredients { get; init; }

    public double IngredientNutrition { get; init; }
}

public sealed record TradeFoodNeed
{
    public double Nutrition { get; init; }

    public IReadOnlyList<FoodIngredientSlot> Missing { get; init; } = [];

    public double IngredientNutrition { get; init; }
}

/// <summary>
/// A native definition classification, including whether a vegetable is a raw crop eligible for surplus export.
/// It must not be inferred from localized labels. Zero/unknown classification never authorizes a sale.
/// </summary>
public sealed record TradeFoodGood
{
    public double Nutrition { get; init; }

    public FoodIngredientClass Class { get; init; }

    public bool Prepared { get; init; }

    public bool NonPerishable { get; init; }

    public bool Crop { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Uses the shared tick plan and the observed active meal recipe. One day's ingredient buffer bridges a
    /// missing source without pretending a proposed channel is already producing stock.
    /// </summary>
    public static TradeFoodContext RoundsTradeFood(RoundsFacts f, RoundsPolicy p)
    {
        var r = new TradeFoodContext
        {
            Plan = f.FoodPlan,
            RunwayDays = f.FoodDays,
            MinDays = p.FoodMinDays,
            TargetDays = p.FoodTargetDays,
            DesiredIngredients = f.TradeMealIngredients,
        };
        if (f.FoodPlan.TryGetValue(out var plan))
        {
            if (plan!.Forecast.RunwayDays.IsKnown)
            {
                r = r with { RunwayDays = plan.Forecast.RunwayDays };
            }
            if (r.DesiredIngredients.TryGetValue(out var slots) && slots!.Count > 0)
            {
                r = r with { IngredientNutrition = plan.DemandPerDay / slots.Count };
            }
        }
        return r;
    }

    /// <summary>
    /// Reads the active fine/lavish recipe's native slots. Observed bills express the intended tier even when a
    /// protein shortage makes that recipe temporarily unavailable; trade does not promote the meal tier.
    /// </summary>
    public static Fact<EquatableArray<FoodIngredientSlot>> TradeMealIngredients(Fact<EquatableArray<ProductionBench>> benches)
    {
        if (!benches.TryGetValue(out var rows))
        {
            return Unknown<EquatableArray<FoodIngredientSlot>>();
        }
        IReadOnlyList<FoodIngredientSlot> chosen = [];
        var best = 0.0;
        var name = "";
        var supported = new HashSet<FoodIngredientClass> { FoodIngredientClass.Meat, FoodIngredientClass.AnimalProduct, FoodIngredientClass.Vegetable };
        foreach (var bench in rows!)
        {
            if (bench.Butcher || !Positive(bench.Usable))
            {
                continue;
            }
            foreach (var bill in bench.Bills)
            {
                foreach (var recipe in bench.Recipes)
                {
                    if (bill.Recipe != recipe.Name)
                    {
                        continue;
                    }
                    var mk = recipe.Mood.TryGetValue(out var mood);
                    var sk = recipe.IngredientClasses.TryGetValue(out var slots);
                    if (!mk || !Finite(mood) || mood < 5 || !sk || !MealSlotsSupported(slots!, supported))
                    {
                        continue;
                    }
                    if (mood > best || (mood == best && string.CompareOrdinal(recipe.Name, name) < 0))
                    {
                        best = mood;
                        name = recipe.Name;
                        chosen = slots!;
                    }
                }
            }
        }
        return Known(EquatableArray.From(chosen));
    }

    /// <summary>
    /// Reads the food plan's trade candidates: Nutrition is what the plan opened to buy from the traders it has
    /// priced offers of. Unpriced caravans are browsed by TradeRecovered (the always-browse session), which
    /// records the offers the plan then ranks.
    /// </summary>
    internal static TradeFoodNeed ReviewTradeFood(TradeFoodContext r)
    {
        var pk = r.Plan.TryGetValue(out var plan);
        var rk = r.RunwayDays.TryGetValue(out var runway);
        if (!pk || !rk || !FoodNumber(runway) || !FieldPositive(r.MinDays) || !FieldPositive(r.TargetDays) || r.TargetDays <= r.MinDays)
        {
            return new TradeFoodNeed();
        }
        var (sources, _, valid) = MealIngredientSources(plan!);
        if (!valid)
        {
            return new TradeFoodNeed();
        }
        var nutrition = 0.0;
        foreach (var entry in plan!.Portfolio)
        {
            if (entry.Channel.Kind != CandidateKind.Trade)
            {
                continue;
            }
            if (entry.Channel.Nutrition().StockCap.TryGetValue(out var stock) && entry.Decision == FoodPlanDecision.Open)
            {
                nutrition += stock;
            }
        }
        var need = new TradeFoodNeed { Nutrition = nutrition };
        // Ingredient upgrades cannot sell away an emergency reserve.
        if (runway <= r.TargetDays || !FieldPositive(r.IngredientNutrition))
        {
            return need;
        }
        var known = r.DesiredIngredients.TryGetValue(out var slots);
        var all = new HashSet<FoodIngredientClass> { FoodIngredientClass.Meat, FoodIngredientClass.Vegetable, FoodIngredientClass.AnimalProduct };
        if (!known || !MealSlotsSupported(slots!, all))
        {
            return need;
        }
        var missing = new List<FoodIngredientSlot>();
        foreach (var slot in slots!)
        {
            if (MealSlotsSupported([slot], sources!))
            {
                continue;
            }
            var protein = slot.Alternatives.Where(c => c is FoodIngredientClass.Meat or FoodIngredientClass.AnimalProduct).ToList();
            if (protein.Count > 0)
            {
                missing.Add(new FoodIngredientSlot { Alternatives = EquatableArray.From(protein) });
            }
        }
        if (missing.Count > 0)
        {
            need = need with { Missing = missing, IngredientNutrition = r.IngredientNutrition };
        }
        return need;
    }

    internal static bool ValidTradeFood(TradeFoodGood g) =>
        FieldPositive(g.Nutrition)
        && g.Class is FoodIngredientClass.Any or FoodIngredientClass.Meat or FoodIngredientClass.Vegetable or FoodIngredientClass.AnimalProduct
        && (!g.Crop || (!g.Prepared && g.Class == FoodIngredientClass.Vegetable));

    /// <summary>
    /// Food purchases share a nutrition budget across definitions; supply from a higher ranked definition reduces
    /// what the next may buy. Silver remains the selector's independently enforced budget.
    /// </summary>
    internal static List<TradeTarget> TradeFoodTargets(TradeFoodNeed need, IReadOnlyList<TradeSheetRowFact> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            counts[row.DefName] = counts.GetValueOrDefault(row.DefName) + 1;
        }
        var candidates = new List<TradeSheetRowFact>();
        foreach (var row in rows)
        {
            var known = row.Food.TryGetValue(out var g);
            if (
                !known
                || !ValidTradeFood(g!)
                || counts[row.DefName] != 1
                || row.ColonyCount < 0
                || row.TraderCount <= 0
                || !row.TraderWillTradeKnown
                || !row.TraderWillTrade
                || !row.PawnKnown
                || row.Pawn
                || !row.CurrencyKnown
                || row.Currency
                || !row.BuyPriceKnown
                || !FieldPositive(row.BuyPrice)
            )
            {
                continue;
            }
            candidates.Add(row);
        }
        static int Rank(TradeSheetRowFact row, TradeFoodGood g)
        {
            // Pemmican has a long native rot clock; reserve policy still prefers it over short-lived meals without
            // claiming that it cannot rot.
            if (g.NonPerishable || ReserveFoodDefinition(new Resource(row.DefName)))
            {
                return 0;
            }
            return g.Prepared ? 1 : 2;
        }
        candidates = [.. candidates.OrderBy(
            x => x,
            Comparer<TradeSheetRowFact>.Create(
                (a, b) =>
                {
                    var ga = a.Food.ValueOrDefault!;
                    var gb = b.Food.ValueOrDefault!;
                    var ra = Rank(a, ga);
                    var rb = Rank(b, gb);
                    if (ra != rb)
                    {
                        return ra < rb ? -1 : 1;
                    }
                    var pa = a.BuyPrice / ga.Nutrition;
                    var pb = b.BuyPrice / gb.Nutrition;
                    if (pa != pb)
                    {
                        return pa < pb ? -1 : 1;
                    }
                    return string.CompareOrdinal(a.DefName, b.DefName);
                }
            )
        )];
        var used = new Dictionary<string, long>(StringComparer.Ordinal);
        void Buy(double nutrition, FoodIngredientSlot? slot)
        {
            if (!FieldPositive(nutrition))
            {
                return;
            }
            foreach (var row in candidates)
            {
                var g = row.Food.ValueOrDefault!;
                if (slot is not null && (g.Prepared || !MealSlotsSupported([slot], new HashSet<FoodIngredientClass> { g.Class })))
                {
                    continue;
                }
                var count = row.TraderCount - used.GetValueOrDefault(row.DefName);
                if (count <= 0)
                {
                    continue;
                }
                var wanted = Math.Ceiling(nutrition / g.Nutrition);
                if (wanted < count)
                {
                    count = (long)wanted;
                }
                used[row.DefName] = used.GetValueOrDefault(row.DefName) + count;
                nutrition -= count * g.Nutrition;
                if (nutrition <= 0)
                {
                    break;
                }
            }
        }
        Buy(need.Nutrition, null);
        foreach (var missing in need.Missing)
        {
            var remaining = need.IngredientNutrition;
            foreach (var row in rows)
            {
                var known = row.Food.TryGetValue(out var g);
                if (known && ValidTradeFood(g!) && !g!.Prepared && row.ColonyCount > 0 && counts[row.DefName] == 1 && MealSlotsSupported([missing], new HashSet<FoodIngredientClass> { g.Class }))
                {
                    remaining -= row.ColonyCount * g.Nutrition;
                }
            }
            Buy(remaining, missing);
        }
        var output = new List<TradeTarget>();
        foreach (var row in candidates)
        {
            var count = used.GetValueOrDefault(row.DefName);
            if (count > 0)
            {
                output.Add(new TradeTarget(row.DefName, row.ColonyCount + count, count, 0, 0));
            }
        }
        return output;
    }

    /// <summary>
    /// Authorizes only raw vegetable rows above an explicit retained target while buying a missing protein
    /// ingredient. SelectTrade still applies the economic floor and the export quantity/price/cash limits.
    /// </summary>
    public static IReadOnlyDictionary<string, long> CropSurplusFloors(TradeNeed need)
    {
        var output = new Dictionary<string, long>(StringComparer.Ordinal);
        if (need.Food.Missing.Count == 0 || !FieldPositive(need.Food.IngredientNutrition))
        {
            return output;
        }
        foreach (var row in need.Surplus)
        {
            if (need.Retained.TryGetValue(row.Resource, out var floor) && floor > 0 && row.Count > 0)
            {
                output[row.Resource.Value] = floor;
            }
        }
        return output;
    }

    internal static bool SelectedProteinPurchase(IReadOnlyList<TradeSelectionLine> selected, IReadOnlyList<TradeSheetRowFact> rows)
    {
        foreach (var line in selected)
        {
            if (line.Count <= 0)
            {
                continue;
            }
            foreach (var row in rows)
            {
                if (row.LineID != line.LineID)
                {
                    continue;
                }
                var known = row.Food.TryGetValue(out var g);
                if (known && ValidTradeFood(g!) && !g!.Prepared && g.Class is FoodIngredientClass.Meat or FoodIngredientClass.AnimalProduct)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
