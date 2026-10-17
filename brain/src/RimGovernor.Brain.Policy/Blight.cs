namespace RimGovernor.Brain.Policy;

// Go: blight.go

/// <summary>
/// One native census row: a blighted plant standing in a player growing zone or on home ground.
/// <see cref="Designated"/> is native's own record of an existing cut or harvest designation;
/// <see cref="Eligible"/> is whether native offers the plant for a CutPlant designation.
/// </summary>
public sealed record BlightedPlant
{
    public string ID { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public bool Designated { get; init; }

    public bool Eligible { get; init; }
}

public static class BlightRules
{
    /// <summary>
    /// The binary food/blight deficit signal (<see cref="FoodRules.BlightNeedOf"/>): an unknown census stays unknown (absence is
    /// never evidence of recovery); otherwise deficit is simply "any blighted plant still stands", designated or not,
    /// because the Need is met by the cut, not by the order.
    /// </summary>
    public static Fact<bool> BlightDeficit(Fact<EquatableArray<BlightedPlant>> plants) =>
        plants.TryGetValue(out var rows) ? Known(rows.Count > 0) : Unknown<bool>();

    /// <summary>
    /// The plants the blight Need designates this cycle: every undesignated census row not already claimed by an
    /// open admission, lowest plant ID first, at most <paramref name="limit"/> rows. A designated plant is
    /// left to the work it already has; a claimed one to the admission that holds it.
    /// </summary>
    public static List<BlightedPlant> SelectBlightCuts(IEnumerable<BlightedPlant> plants, IReadOnlySet<string> claimed, int limit)
    {
        var output = new List<BlightedPlant>();
        foreach (var plant in plants)
        {
            if (plant.ID.Length == 0 || !plant.Eligible || plant.Designated || claimed.Contains(plant.ID))
            {
                continue;
            }

            output.Add(plant);
        }

        var sorted = output.OrderBy(p => p.ID, StringComparer.Ordinal).ToList();
        if (limit > 0 && sorted.Count > limit)
        {
            sorted.RemoveRange(limit, sorted.Count - limit);
        }

        return sorted;
    }
}
