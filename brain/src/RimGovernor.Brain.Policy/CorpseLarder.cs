namespace RimGovernor.Brain.Policy;

/// <summary>The one corpse change a review makes. An empty <see cref="Kind"/> means no change.</summary>
public sealed record CorpseLarderMethod
{
    /// <summary>"allow", "forbid", "haul" or empty for none.</summary>
    public string Kind { get; init; } = "";

    public FoodStock? Stock { get; init; }

    public CorpseHandling? Handling { get; init; }
}

public static class CorpseLarderRules
{
    /// <summary>
    /// The rot runway a held corpse must keep, our own tuning: a quarter day leaves time for ordinary
    /// butchering and hauling. The dense meat thresholds below are tuning too; the game defines no "safe" days.
    /// </summary>
    private const long LarderRotMargin = GameTime.TicksPerDay / 4;

    private static bool DenseCorpse(double meat, double size) => meat > 225 || (size <= 0.75 && meat > 75);

    private static (double Meat, double Size) Body(FoodStock s) =>
        s.Form is FoodForm.Corpse { MeatAmount: var meat, BodySize: var size } ? (meat, size) : throw new DomainException($"food stock {s.Id} is no corpse");

    /// <summary>
    /// Changes at most one corpse per review. A warm corpse is never held; frozen dense corpses release in
    /// expiry/identity order. A quarter-day rot margin leaves time for ordinary butchering and hauling.
    /// An empty-Kind result is "no change". Throws <see cref="DomainException"/> on invalid facts.
    /// </summary>
    public static CorpseLarderMethod SelectCorpseLarder(FoodStorageObservation v)
    {
        var none = new CorpseLarderMethod();
        var stocks = v.Stocks;
        var larder = v.Larder;
        DomainException.Require(FoodForecastRules.FoodNumber(larder.RawMeatNutrition) && FoodForecastRules.FoodNumber(larder.CookDemandNutrition), "invalid corpse larder");
        v.Validate();
        var handling = new Dictionary<string, CorpseHandling>(StringComparer.Ordinal);
        foreach (var row in larder.Corpses)
        {
            DomainException.Require(FoodForecastRules.FoodId(row.Id) && row.Cell.X >= 0 && row.Cell.Z >= 0, "invalid corpse location");
            DomainException.Require(handling.TryAdd(row.Id, row), "duplicate corpse location");
        }
        var corpses = new List<FoodStock>();
        var available = larder.RawMeatNutrition;
        foreach (var row in stocks)
        {
            var s = row.Stock;
            if (!s.IsCorpse() || s.IsHumanlike)
            {
                continue;
            }
            var (meat, size) = Body(s);
            DomainException.Require(FoodForecastRules.FoodNumber(meat) && FoodForecastRules.FoodNumber(size) && meat > 0 && size > 0, "invalid corpse yield");
            if (!s.Forbidden)
            {
                available += s.Nutrition;
            }
            corpses.Add(s);
        }
        DomainException.Require(FoodForecastRules.FoodNumber(available), "corpse nutrition overflow");
        var ordered = corpses
            .OrderBy(s => s.Rot is FoodRot.Rots ? 0 : 1)
            .ThenBy(s => s.Rot is FoodRot.Rots { Ticks: var t } ? t : 0)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        foreach (var s in ordered)
        {
            if (!handling.TryGetValue(s.Id, out var row))
            {
                continue;
            }
            // A carried corpse is not held in a larder.
            if (s.Place is not FoodPlace.Stands { Roofed: var roof } || s.Rot is not FoodRot.Rots { Ticks: var ticks })
            {
                continue;
            }
            var (meat, size) = Body(s);
            var frozen = roof && s.TemperatureC <= 0;
            var dense = DenseCorpse(meat, size);
            if (s.Forbidden && (!frozen || !dense || ticks <= LarderRotMargin || available < larder.CookDemandNutrition))
            {
                return new CorpseLarderMethod { Kind = "allow", Stock = s, Handling = row };
            }
        }
        foreach (var s in ordered)
        {
            if (!handling.TryGetValue(s.Id, out var row))
            {
                continue;
            }
            if (s.Forbidden || s.Place is not FoodPlace.Stands { Roofed: var roof } || s.Rot is not FoodRot.Rots { Ticks: var ticks })
            {
                continue;
            }
            if (roof && s.TemperatureC <= 0)
            {
                var (meat, size) = Body(s);
                if (DenseCorpse(meat, size) && ticks > LarderRotMargin && available - s.Nutrition >= larder.CookDemandNutrition)
                {
                    return new CorpseLarderMethod { Kind = "forbid", Stock = s, Handling = row };
                }
            }
            else if (row.FrozenDestination && !row.Hauler.IsEmpty)
            {
                return new CorpseLarderMethod { Kind = "haul", Stock = s, Handling = row };
            }
        }
        return none;
    }
}
