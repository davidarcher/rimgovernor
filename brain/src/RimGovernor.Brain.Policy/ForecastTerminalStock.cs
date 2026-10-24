namespace RimGovernor.Brain.Policy;

/// <summary>A surviving lot from the detailed evaluator, with the native census eligibility kept beside its balance.</summary>
public sealed record ForecastTerminalLot(string Id, Resource Resource, Fact<double> Count, Fact<double> FreshUntil,
    Fact<bool> FoodEligible, Fact<double> NutritionPerUnit, EquatableArray<PawnId> Eaters, bool Medicine);

public static class ForecastStockScope
{
    public const string Food = "map_food_supply/eligible_nonreserve/holder_and_diet/v1";

    public static Fact<double> OtherDemand(Fact<FoodSupply> supply, IEnumerable<string> modeledPawns)
    {
        if (!supply.TryGetValue(out var food)) return default;
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
            if (!lot.FoodEligible.IsKnown) return default;
            if (!lot.FoodEligible.Value) continue;
            if (!lot.Count.IsKnown || !lot.FreshUntil.IsKnown) return default;
            count += lot.Count.Value;
        }
        return Known(count);
    }
}
