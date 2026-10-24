namespace RimGovernor.Brain.Policy;

/// <summary>A surviving lot from the detailed evaluator, with the native census eligibility kept beside its balance.</summary>
public sealed record ForecastTerminalLot(string Id, Resource Resource, Fact<double> Count, Fact<double> FreshUntil,
    Fact<bool> FoodEligible, Fact<double> NutritionPerUnit, EquatableArray<PawnId> Eaters, bool Medicine);

public static class ForecastStockScope
{
    public const string Food = "map_food_supply/eligible_nonreserve/holder_and_diet/v1";

    public static Fact<double> OtherDemand(Fact<FoodSupply> supply, IEnumerable<string> modeledPawns)
    {
        if (!supply.TryGetValue(out var food) || food.Complete != Known(true)) return default;
        var modeled = modeledPawns.ToHashSet(StringComparer.Ordinal);
        var other = food.Consumers.Where(c => !modeled.Contains(c.Id.Value)).ToArray();
        return other.All(c => c.NutritionPerDay.IsKnown && double.IsFinite(c.NutritionPerDay.Value) && c.NutritionPerDay.Value >= 0)
            ? Known(other.Sum(c => c.NutritionPerDay.Value)) : default;
    }

    internal static Fact<bool> Eligible(FoodStock stock, FoodSupply supply)
    {
        if (stock.Reserve || stock.Corpse && stock.Forbidden == Known(true)) return Known(false);
        if (!stock.Holder.IsKnown || stock.Corpse && !stock.Forbidden.IsKnown) return default;
        if (stock.Rot is FoodRot.Rots { Ticks: <= 0 }) return Known(false);
        var unknown = false;
        foreach (var eater in stock.Eaters)
        {
            if (!stock.Holder.Value.IsEmpty && stock.Holder.Value != eater) continue;
            var consumer = supply.Consumers.FirstOrDefault(c => c.Id == eater);
            if (consumer is null) { unknown = true; continue; }
            if (!(stock.IsHumanMeat || stock.IsHumanlike) || consumer.HumanMeatAcceptable == Known(true)) return Known(true);
            unknown |= !consumer.HumanMeatAcceptable.IsKnown;
        }
        return unknown ? default : Known(false);
    }

    public static Fact<double> Count(FoodSupply supply, Resource resource)
    {
        if (supply.Complete != Known(true)) return default;
        double count = 0;
        foreach (var row in supply.Stocks.Where(s => s.DefName == resource))
        {
            var eligible = Eligible(row, supply);
            if (!eligible.IsKnown) return default;
            if (!eligible.Value) continue;
            if (!row.Count.IsKnown) return default;
            count += row.Count.Value;
        }
        return Known(count);
    }

    public static Fact<double> Nutrition(FoodSupply supply)
    {
        if (supply.Complete != Known(true)) return default;
        double value = 0;
        foreach (var row in supply.Stocks)
        {
            var eligible = Eligible(row, supply);
            if (!eligible.IsKnown) return default;
            if (!eligible.Value) continue;
            if (!row.Nutrition.IsKnown) return default;
            value += row.Nutrition.Value;
        }
        return Known(value);
    }

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
