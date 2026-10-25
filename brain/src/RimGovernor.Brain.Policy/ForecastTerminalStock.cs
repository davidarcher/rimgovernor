namespace RimGovernor.Brain.Policy;

/// <summary>Whether a surviving lot feeds the modeled pawns: some pawn may eat it, none may, or it is edible and no pawn's food use
/// was observed for it, so which eats it is not read. A lot of the last kind carries <see cref="ForecastTerminalLot.FreshUntil"/>
/// of never; nothing reads it.</summary>
public enum FoodEligibility { Eligible, Ineligible, NoObservedUse }

/// <summary>A surviving lot from the detailed evaluator, with the native census eligibility kept beside its balance.</summary>
public sealed record ForecastTerminalLot(string Id, Resource Resource, double Count, double FreshUntil,
    FoodEligibility FoodEligible, double NutritionPerUnit, EquatableArray<PawnId> Eaters, bool Medicine);

public static class ForecastStockScope
{
    public const string Food = "map_food_supply/eligible_nonreserve/holder_and_diet/v1";

    public static Fact<double> OtherDemand(FoodSupply food, IEnumerable<string> modeledPawns)
    {
        var modeled = modeledPawns.ToHashSet(StringComparer.Ordinal);
        var other = food.Consumers.Where(c => !modeled.Contains(c.Id.Value)).ToArray();
        return other.All(c => double.IsFinite(c.NutritionPerDay) && c.NutritionPerDay >= 0)
            ? Known(other.Sum(c => c.NutritionPerDay)) : default;
    }

    /// <summary>Whether a fed eater of the stock may eat it; every eater is a consumer of the census. Throws <see cref="FoodFactsException"/>.</summary>
    internal static bool Eligible(FoodStock stock, FoodSupply supply)
    {
        if (stock.Reserve || stock.Corpse && stock.Forbidden) return false;
        if (stock.Rot is FoodRot.Rots { Ticks: <= 0 }) return false;
        foreach (var eater in stock.Eaters)
        {
            if (!stock.Holder.IsEmpty && stock.Holder != eater) continue;
            var consumer = supply.Consumers.FirstOrDefault(c => c.Id == eater) ?? throw new FoodFactsException();
            if (!(stock.IsHumanMeat || stock.IsHumanlike) || consumer.HumanMeatAcceptable) return true;
        }
        return false;
    }

    public static double Count(FoodSupply supply, Resource resource) =>
        supply.Stocks.Where(s => s.DefName == resource && Eligible(s, supply)).Sum(s => (double)s.Count);

    public static Fact<double> Nutrition(FoodSupply supply) =>
        Known(supply.Stocks.Where(s => Eligible(s, supply)).Sum(s => s.Nutrition));

    internal static Fact<double> Count(IEnumerable<ForecastTerminalLot> lots, Resource resource)
    {
        double count = 0;
        foreach (var lot in lots.Where(l => l.Resource == resource))
        {
            if (lot.FoodEligible is FoodEligibility.NoObservedUse) return default;
            if (lot.FoodEligible is FoodEligibility.Ineligible) continue;
            count += lot.Count;
        }
        return Known(count);
    }
}
