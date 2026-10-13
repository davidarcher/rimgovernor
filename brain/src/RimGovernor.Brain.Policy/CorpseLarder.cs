namespace RimGovernor.Brain.Policy;

public sealed record CorpseHandling
{
    public string Id { get; init; } = "";

    public Cell Cell { get; init; }

    public PawnId Hauler { get; init; }

    public bool FrozenDestination { get; init; }

    /// <summary>Whether the corpse lies in a stockpile (false: in the field).</summary>
    public Fact<bool> InStorage { get; init; }
}

/// <summary>
/// CookDemandNutrition is one batch per active cook bill, measured using its native ingredient filters.
/// Released corpses fund the same window so subsequent reviews do not release the entire larder before butchering.
/// </summary>
public sealed record FoodLarder
{
    public double RawMeatNutrition { get; init; }

    public double CookDemandNutrition { get; init; }

    public EquatableArray<CorpseHandling> Corpses { get; init; }
}

/// <summary>The one corpse change a review makes. An empty <see cref="Kind"/> means no change (Go's zero value).</summary>
public sealed record CorpseLarderMethod
{
    /// <summary>"allow", "forbid", "haul" or empty for none.</summary>
    public string Kind { get; init; } = "";

    public FoodStock? Stock { get; init; }

    public CorpseHandling? Handling { get; init; }

    public Cell Cell { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The rot runway a held corpse must keep, our own tuning: a quarter day leaves time for ordinary
    /// butchering and hauling. The dense meat thresholds below are tuning too; the game defines no "safe" days.
    /// </summary>
    private const long LarderRotMargin = GameTime.TicksPerDay / 4;

    private static bool DenseCorpse(double meat, double size) => meat > 225 || (size <= 0.75 && meat > 75);

    /// <summary>
    /// Changes at most one corpse per review. A warm corpse is never held; frozen dense corpses release in
    /// expiry/identity order. A quarter-day rot margin leaves time for ordinary butchering and hauling.
    /// An empty-Kind result is "no change". Throws <see cref="DomainException"/> on invalid facts.
    /// </summary>
    public static CorpseLarderMethod SelectCorpseLarder(FoodStorageObservation v)
    {
        var none = new CorpseLarderMethod();
        if (!v.Stocks.TryGetValue(out var stocks) || !v.Larder.TryGetValue(out var larder))
        {
            return none;
        }
        DomainException.Require(FoodNumber(larder.RawMeatNutrition) && FoodNumber(larder.CookDemandNutrition), "invalid corpse larder");
        v.Validate();
        var handling = new Dictionary<string, CorpseHandling>(StringComparer.Ordinal);
        foreach (var row in larder.Corpses)
        {
            DomainException.Require(FoodId(row.Id) && row.Cell.X >= 0 && row.Cell.Z >= 0, "invalid corpse location");
            DomainException.Require(handling.TryAdd(row.Id, row), "duplicate corpse location");
        }
        var corpses = new List<FoodStock>();
        var available = larder.RawMeatNutrition;
        foreach (var row in stocks)
        {
            var s = row.Stock;
            if (!s.Corpse || s.IsHumanlike)
            {
                continue;
            }
            var fk = s.Forbidden.TryGetValue(out var forbidden);
            var mk = s.MeatAmount.TryGetValue(out var meat);
            var bk = s.BodySize.TryGetValue(out var size);
            var nk = s.Nutrition.TryGetValue(out var nutrition);
            if (!fk || !mk || !bk || !nk)
            {
                return none;
            }
            DomainException.Require(FoodNumber(meat) && FoodNumber(size) && meat > 0 && size > 0, "invalid corpse yield");
            if (!forbidden)
            {
                available += nutrition;
            }
            corpses.Add(s);
        }
        DomainException.Require(FoodNumber(available), "corpse nutrition overflow");
        var ordered = corpses
            .OrderBy(s => s.RotTicks.IsKnown ? 0 : 1)
            .ThenBy(s => s.RotTicks.TryGetValue(out var t) ? t : 0)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        foreach (var s in ordered)
        {
            if (!handling.TryGetValue(s.Id, out var row))
            {
                continue;
            }
            s.Forbidden.TryGetValue(out var forbidden);
            var rk = s.Roofed.TryGetValue(out var roof);
            var tk = s.TemperatureC.TryGetValue(out var temp);
            var ek = s.RotTicks.TryGetValue(out var ticks);
            s.MeatAmount.TryGetValue(out var meat);
            s.BodySize.TryGetValue(out var size);
            if (!rk || !tk || !ek)
            {
                continue;
            }
            var frozen = roof && temp <= 0;
            var dense = DenseCorpse(meat, size);
            if (forbidden && (!frozen || !dense || ticks <= LarderRotMargin || available < larder.CookDemandNutrition))
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
            s.Forbidden.TryGetValue(out var forbidden);
            if (forbidden)
            {
                continue;
            }
            var rk = s.Roofed.TryGetValue(out var roof);
            var tk = s.TemperatureC.TryGetValue(out var temp);
            var ek = s.RotTicks.TryGetValue(out var ticks);
            s.MeatAmount.TryGetValue(out var meat);
            s.BodySize.TryGetValue(out var size);
            s.Nutrition.TryGetValue(out var nutrition);
            if (!rk || !tk || !ek)
            {
                continue;
            }
            if (roof && temp <= 0)
            {
                if (DenseCorpse(meat, size) && ticks > LarderRotMargin && available - nutrition >= larder.CookDemandNutrition)
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
