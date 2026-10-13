namespace RimGovernor.Brain.Policy;

// Go: baby_feeding.go
// MaintainBabyFeeding keeps babies fed. The game feeds a baby itself: a lactating pawn breastfeeds any baby,
// otherwise a colonist on Childcare work bottle-feeds it food it can eat (baby food, milk or insect jelly).
// Childcare is pinned work for every pawn already, so the one thing the colony owes is the food: with no
// breastfeeder and too little baby-edible stock, the goal places a standing target-count bill for a
// baby-edible recipe through the existing production bill write.

/// <summary>
/// The review of baby food: what the babies' foods hold against what they need. <see cref="IsShort"/> is the
/// goal's deficit.
/// </summary>
public sealed record BabyFeeding
{
    public int Babies { get; init; }

    /// <summary>A colonist can breastfeed, which feeds every baby.</summary>
    public bool Breastfed { get; init; }

    /// <summary>The shared baby-edible nutrition (stocks a baby eats).</summary>
    public double StockNutrition { get; init; }

    /// <summary>The stock nutrition split by definition.</summary>
    public IReadOnlyDictionary<Resource, double> ByDefinition { get; init; } = new Dictionary<Resource, double>();

    public double TargetNutrition { get; init; }

    public bool IsShort { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>MaintainBabyFeeding keeps babies fed.</summary>
    public static readonly ConcernId MaintainBabyFeeding = new("MaintainBabyFeeding");

    /// <summary>MaintainBabyFeeding's bill purpose.</summary>
    public static readonly BillPurpose BabyFoodBill = new("baby_food");

    internal const int BabyFeedingPriority = 2;

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
    public static BabyFeeding ReviewBabyFeeding(IReadOnlyList<PawnId> babies, int breastfeeders, FoodSupply supply, double targetDays)
    {
        var byDefinition = new Dictionary<Resource, double>();
        var r = new BabyFeeding
        {
            Babies = babies.Count,
            Breastfed = breastfeeders > 0,
            ByDefinition = byDefinition,
        };
        if (babies.Count == 0)
        {
            return r;
        }

        DomainException.Require(FieldPositive(targetDays) && targetDays <= 60, "food facts unavailable");
        var isBaby = new HashSet<PawnId>(babies);
        var perDay = 0.0;
        var found = 0;
        foreach (var c in supply.Consumers)
        {
            if (!isBaby.Contains(c.Id))
            {
                continue;
            }

            var known = c.NutritionPerDay.TryGetValue(out var rate);
            DomainException.Require(known && FoodNumber(rate), "food facts unavailable");
            perDay += rate;
            found++;
        }

        DomainException.Require(found == isBaby.Count, "food facts unavailable");
        var stockNutrition = 0.0;
        foreach (var s in supply.Stocks)
        {
            var hk = s.Holder.TryGetValue(out var holder);
            var nk = s.Nutrition.TryGetValue(out var amount);
            DomainException.Require(hk && nk && FoodNumber(amount), "food facts unavailable");
            if (!holder.IsEmpty || s.Corpse)
            {
                continue;
            }

            foreach (var e in s.Eaters)
            {
                if (isBaby.Contains(e))
                {
                    stockNutrition += amount;
                    byDefinition[s.DefName] = byDefinition.GetValueOrDefault(s.DefName) + amount;
                    break;
                }
            }
        }

        var floor = babies.Count * BabyFoodAlertNutrition;
        return r with
        {
            StockNutrition = stockNutrition,
            TargetNutrition = Math.Max(perDay * targetDays, floor),
            IsShort = !r.Breastfed && stockNutrition < floor,
        };
    }

    /// <summary>
    /// Selects the standing bill that makes baby-edible food: a recipe whose single product a baby can eat.
    /// Native counts the product's stock toward the target, and other baby foods reduce it. None is selected
    /// unless the review is short.
    /// </summary>
    public static (BillSelection Selection, bool Ok) SelectBabyFoodBill(Fact<EquatableArray<ProductionBench>> benches, BabyFeeding review)
    {
        if (!benches.TryGetValue(out var rows) || !review.IsShort || !FieldPositive(review.TargetNutrition))
        {
            return (new BillSelection(), false);
        }

        return SelectTargetBill(
            rows.Items,
            new TargetBillSpec
            {
                Family = p => p.BabyEdible.TryGetValue(out var edible) && edible,
                Target = (def, nutrition) =>
                    (review.TargetNutrition - review.StockNutrition + review.ByDefinition.GetValueOrDefault(def)) / nutrition,
            }
        );
    }
}
