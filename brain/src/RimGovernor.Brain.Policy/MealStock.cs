namespace RimGovernor.Brain.Policy;

/// <summary>Ports <c>meal_stock.go</c>.</summary>
public static partial class PolicyFunctions
{
    /// <summary>
    /// Reports accessible ingredients without inventing a replenishment rate or counting stored nutrition twice in FoodPlan.
    /// </summary>
    public static List<SupplyCandidate> StockIngredientChannels(FoodSupply s)
    {
        var amounts = new Dictionary<CandidateKind, double>();
        foreach (var stock in s.Stocks)
        {
            var ck = stock.RawClass.TryGetValue(out var rawClass);
            var ak = stock.Nutrition.TryGetValue(out var amount);
            var hk = stock.Holder.TryGetValue(out var holder);
            // Human stock belongs to its gated routing row; it cannot certify protein availability for an
            // unrestricted shared meal bill.
            if (!ck || !ak || !hk || !holder.IsEmpty || stock.Reserve || stock.Corpse || stock.IsHumanMeat || amount <= 0)
            {
                continue;
            }
            CandidateKind kind;
            switch (rawClass)
            {
                case FoodIngredientClass.Meat:
                    kind = CandidateKind.Hunt;
                    break;
                case FoodIngredientClass.Vegetable:
                    kind = CandidateKind.Crop;
                    break;
                case FoodIngredientClass.AnimalProduct:
                    kind = CandidateKind.AnimalProduct;
                    break;
                default:
                    continue;
            }
            amounts[kind] = amounts.GetValueOrDefault(kind) + amount;
        }
        var rows = new List<SupplyCandidate>();
        foreach (var kind in new[] { CandidateKind.Crop, CandidateKind.Hunt, CandidateKind.AnimalProduct })
        {
            var amount = amounts.GetValueOrDefault(kind);
            if (amount > 0)
            {
                var row = FoodCandidate(kind, "stored-ingredients", Known(0.0));
                rows.Add(
                    row with
                    {
                        LaborPerDay = Known(0.0),
                        LeadDays = Known(0.0),
                        State = FoodState(Known(true), Unknown<bool>()),
                        Terms = EquatableArray.From([new CandidateTerm("accessible_ingredient_nutrition", amount)]),
                    }
                );
            }
        }
        return rows;
    }

    private static bool MealStockAvailable(SupplyCandidate c)
    {
        if (c.Id != "stored-ingredients")
        {
            return false;
        }
        foreach (var t in c.Terms)
        {
            if (t.Name == "accessible_ingredient_nutrition" && FieldPositive(t.Value))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The meal the colony's active bills cook and its nutrition: the single product of the active bill's recipe
    /// with the highest mood (ties by name), as the recipe row reports it. Found is false when no active bill cooks
    /// a known meal.
    /// </summary>
    public static (string Def, double Nutrition, bool Found) ObservedMeal(IReadOnlyList<ProductionBench> benches)
    {
        var def = "";
        double nutrition = 0;
        double best = 0;
        var recipeName = "";
        foreach (var bench in benches)
        {
            foreach (var bill in bench.Bills)
            {
                if (!Positive(bill.Active))
                {
                    continue;
                }
                foreach (var recipe in bench.Recipes)
                {
                    var mk = recipe.Mood.TryGetValue(out var mood);
                    if (recipe.Name != bill.Recipe || !mk || recipe.Products.Count != 1)
                    {
                        continue;
                    }
                    var product = recipe.Products[0];
                    var nk = product.Nutrition.TryGetValue(out var n);
                    if (!nk || !FieldPositive(n) || !Positive(product.Edible))
                    {
                        continue;
                    }
                    if (def == "" || mood > best || (mood == best && string.CompareOrdinal(recipe.Name, recipeName) < 0))
                    {
                        (def, nutrition, best, recipeName) = (product.Name, n, mood, recipe.Name);
                    }
                }
            }
        }
        return (def, nutrition, def != "");
    }

    public static MealTier ObservedMealTier(IReadOnlyList<ProductionBench> benches)
    {
        var best = MealTier.Simple;
        foreach (var bench in benches)
        {
            foreach (var bill in bench.Bills)
            {
                if (!Positive(bill.Active))
                {
                    continue;
                }
                foreach (var recipe in bench.Recipes)
                {
                    if (recipe.Name != bill.Recipe)
                    {
                        continue;
                    }
                    if (!recipe.Mood.TryGetValue(out var mood))
                    {
                        continue;
                    }
                    var tier = MealTier.Simple;
                    if (mood >= 12)
                    {
                        tier = MealTier.Lavish;
                    }
                    else if (mood >= 5)
                    {
                        tier = MealTier.Fine;
                    }
                    if (MealTierRank(tier) > MealTierRank(best))
                    {
                        best = tier;
                    }
                }
            }
        }
        return best;
    }
}
