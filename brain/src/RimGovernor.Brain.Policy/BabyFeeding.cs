namespace RimGovernor.Brain.Policy;

/// <summary>
/// The review of baby food: what the babies' foods hold against what they need. <see cref="IsShort"/> leaves the
/// food/baby-feeding Need unmet.
/// </summary>
public sealed record BabyFeeding
{
    /// <summary>Each baby's observed hunger and current native care, distinct from provision.</summary>
    public EquatableArray<BabyFeedingStatus> Care { get; init; }

    /// <summary>The shared baby-edible nutrition (stocks a baby eats).</summary>
    public double StockNutrition { get; init; }

    /// <summary>The stock nutrition split by definition.</summary>
    public IReadOnlyDictionary<Resource, double> ByDefinition { get; init; } = new Dictionary<Resource, double>();

    public double TargetNutrition { get; init; }

    public bool IsShort { get; init; }
}

public static class BabyFeedingRules
{
    /// <summary>The food/baby-feeding Need's bill purpose.</summary>
    public static readonly BillPurpose BabyFoodBill = new("baby_food");

    /// <summary>
    /// The baby-edible nutrition per baby below which the game raises Alert_LowBabyFood
    /// (Alert_LowBabyFood.NutritionThresholdPerBaby, read from the game assembly's constants).
    /// </summary>
    public const double BabyFoodAlertNutrition = 1.0;

    /// <summary>
    /// Reviews the babies' food. The target is the babies' native nutrition per day over
    /// <paramref name="targetDays"/>, at least the game's alert threshold per baby. A baby with no known
    /// consumer row throws.
    /// </summary>
    public static BabyFeeding ReviewBabyFeeding(IReadOnlyList<PawnId> babies,
        EquatableArray<BabyFeedingStatus> care, FoodSupply supply, double targetDays)
    {
        var byDefinition = new Dictionary<Resource, double>();
        var r = new BabyFeeding
        {
            Care = care,
            ByDefinition = byDefinition,
        };
        if (babies.Count == 0)
        {
            return r;
        }

        DomainException.Require(FieldCapacityRules.FieldPositive(targetDays) && targetDays <= 60, "food facts unavailable");
        var isBaby = new HashSet<PawnId>(babies);
        var perDay = 0.0;
        var found = 0;
        foreach (var c in supply.Consumers)
        {
            if (!isBaby.Contains(c.Id))
            {
                continue;
            }

            var rate = c.NutritionPerDay;
            DomainException.Require(FoodForecastRules.FoodNumber(rate), "food facts unavailable");
            perDay += rate;
            found++;
        }

        DomainException.Require(found == isBaby.Count, "food facts unavailable");
        DomainException.Require(care.Count == isBaby.Count && care.Select(c => c.Baby).Distinct(StringComparer.Ordinal).Count() == care.Count
            && care.All(c => isBaby.Contains(new PawnId(c.Baby))), "baby care facts unavailable");
        var stockNutrition = 0.0;
        foreach (var s in supply.Stocks)
        {
            var amount = s.Nutrition;
            DomainException.Require(FoodForecastRules.FoodNumber(amount), "food facts unavailable");
            if (!s.Holder.IsEmpty || s.IsCorpse())
            {
                continue;
            }

            foreach (var e in s.Eaters)
            {
                if (isBaby.Contains(e))
                {
                    stockNutrition += amount;
                    byDefinition[new Resource(s.DefName)] = byDefinition.GetValueOrDefault(new Resource(s.DefName)) + amount;
                    break;
                }
            }
        }

        // Only currently usable nursing covers provision; a nursing-capable pawn count cannot cover every baby.
        var nursing = care.Count(c => c.Opportunity?.Kind == RimGovernor.Brain.GameFacts.Observations.BabyCareKind.Breastfeed
            || c.Active?.Kind == RimGovernor.Brain.GameFacts.Observations.BabyCareKind.Breastfeed);
        var floor = (babies.Count - nursing) * BabyFoodAlertNutrition;
        return r with
        {
            StockNutrition = stockNutrition,
            TargetNutrition = Math.Max(perDay * targetDays, floor),
            IsShort = stockNutrition < floor,
        };
    }

    /// <summary>
    /// Selects the standing bill that makes baby-edible food: a recipe whose single product a baby can eat.
    /// Native counts the product's stock toward the target, and other baby foods reduce it. None is selected
    /// unless the review is short.
    /// </summary>
    public static (BillSelection Selection, bool Ok) SelectBabyFoodBill(IReadOnlyList<ProductionBench> benches, BabyFeeding review)
    {
        if (!review.IsShort || !FieldCapacityRules.FieldPositive(review.TargetNutrition))
        {
            return (new BillSelection(), false);
        }

        return FoodReserveRules.SelectTargetBill(
            benches,
            new TargetBillSpec
            {
                Family = p => p.Food is ProductFood.Food { BabyEdible: true },
                Target = (def, nutrition) =>
                    (review.TargetNutrition - review.StockNutrition + review.ByDefinition.GetValueOrDefault(def)) / nutrition,
            }
        );
    }
}
