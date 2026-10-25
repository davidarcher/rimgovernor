namespace RimGovernor.Brain.Policy;

public static partial class MealRules
{
    /// <summary>
    /// Reports accessible ingredients without inventing a replenishment rate or counting stored nutrition twice in FoodPlan.
    /// </summary>
    public static List<SupplyCandidate> StockIngredientChannels(FoodSupply s)
    {
        var amounts = new Dictionary<CandidateKind, double>();
        foreach (var stock in s.Stocks)
        {
            var rawClass = stock.RawClass;
            var amount = stock.Nutrition;
            // Human stock belongs to its gated routing row; it cannot certify protein availability for an
            // unrestricted shared meal bill.
            if (!stock.Holder.IsEmpty || stock.Reserve || stock.Corpse || stock.IsHumanMeat || amount <= 0)
            {
                continue;
            }
            var candidate = StockIngredientKind(rawClass);
            if (candidate is not { } kind)
            {
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
                var row = CandidateRules.FoodCandidate(kind, "stored-ingredients", new YieldSupply.Steady(0), CandidateState.Delivering, new CandidateLead.Days(0), new CandidateWork.PerDay(0));
                rows.Add(
                    row with
                    {
                        Terms = EquatableArray.From([new CandidateTerm("accessible_ingredient_nutrition", amount)]),
                    }
                );
            }
        }
        return rows;
    }

    private static CandidateKind? StockIngredientKind(FoodIngredientClass rawClass)
    {
        switch (rawClass)
        {
            case FoodIngredientClass.Meat:
                return CandidateKind.Hunt;
            case FoodIngredientClass.Vegetable:
                return CandidateKind.Crop;
            case FoodIngredientClass.AnimalProduct:
                return CandidateKind.AnimalProduct;
            case FoodIngredientClass.None:
            case FoodIngredientClass.Any:
                return null;
        }
        throw new DomainException($"undefined food ingredient class {(int)rawClass}");
    }
    private static bool MealStockAvailable(SupplyCandidate c)
    {
        if (c.Id != "stored-ingredients")
        {
            return false;
        }
        foreach (var t in c.Terms)
        {
            if (t.Name == "accessible_ingredient_nutrition" && FieldCapacityRules.FieldPositive(t.Value))
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
                if (!bill.Active)
                {
                    continue;
                }
                foreach (var recipe in bench.Recipes)
                {
                    if (recipe.Name != bill.Recipe || recipe.Food is not RecipeFood.Food { Mood: var mood } || recipe.Products.Count != 1)
                    {
                        continue;
                    }
                    var product = recipe.Products[0];
                    if (!product.EatenNutrition(out var n) || !FieldCapacityRules.FieldPositive(n))
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
                if (!bill.Active)
                {
                    continue;
                }
                foreach (var recipe in bench.Recipes)
                {
                    if (recipe.Name != bill.Recipe)
                    {
                        continue;
                    }
                    if (recipe.Food is not RecipeFood.Food { Mood: var mood })
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
