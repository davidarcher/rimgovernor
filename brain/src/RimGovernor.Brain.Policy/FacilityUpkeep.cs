namespace RimGovernor.Brain.Policy;

public sealed record StoneStructure
{
    public string Id { get; init; } = "";

    public Resource Definition { get; init; }

    public Fact<double> Flammability { get; init; }
}

public static class FacilityUpkeepRules
{
    /// <summary>The native project whose recipes cut the stone blocks a wall replacement needs.</summary>
    public const string StoneShellResearch = "Stonecutting";

    internal static bool FacilityCells(IReadOnlyList<Cell> cells)
    {
        if (cells.Count < 1)
        {
            return false;
        }
        var seen = new HashSet<Cell>();
        foreach (var c in cells)
        {
            if (c.X < 0 || c.Z < 0 || c.X >= 4096 || c.Z >= 4096 || !seen.Add(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Throws <see cref="DomainException"/>.</summary>
    public static Fact<EquatableArray<string>> ReviewStoneShell(Fact<EquatableArray<ConstructionClaim>> owned, Fact<EquatableArray<StoneStructure>> structures)
    {
        var unknown = Unknown<EquatableArray<string>>();
        if (!owned.TryGetValue(out var buildings))
        {
            return unknown;
        }
        var walls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in buildings)
        {
            if (b.Building!.Definition == "Wall")
            {
                DomainException.Require(FoodForecastRules.FoodId(b.Identity.Current) && walls.Add(b.Identity.Current), "invalid owned wall");
            }
        }
        if (walls.Count == 0)
        {
            return Known<EquatableArray<string>>([]);
        }
        if (!structures.TryGetValue(out var rows))
        {
            return unknown;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var row in rows)
        {
            DomainException.Require(FoodForecastRules.FoodId(row.Id) && seen.Add(row.Id) && AdmissionRules.ValidResource(row.Definition), "invalid structure census");
            if (!walls.Contains(row.Id))
            {
                continue;
            }
            if (!row.Flammability.TryGetValue(out var flam) || row.Definition.Value != "Wall")
            {
                return unknown;
            }
            DomainException.Require(FoodForecastRules.FoodNumber(flam), "invalid wall flammability");
            if (flam > 0)
            {
                result.Add(row.Id);
            }
        }
        // A current owned wall missing from the same-tick structure census is a conflicting observation, not
        // evidence that its stone upgrade recovered.
        if (walls.Any(id => !seen.Contains(id)))
        {
            return unknown;
        }
        result.Sort(StringComparer.Ordinal);
        return Known(EquatableArray.From(result));
    }
}
