namespace RimGovernor.Brain.Policy;

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
    public static Fact<EquatableArray<string>> ReviewStoneShell(Fact<EquatableArray<ConstructionClaim>> owned, Fact<EquatableArray<RepairStructure>> structures)
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
            var s = row.Structure;
            DomainException.Require(FoodForecastRules.FoodId(s.Id) && seen.Add(s.Id) && AdmissionRules.ValidResource(new Resource(s.Definition)), "invalid structure census");
            if (!walls.Contains(s.Id))
            {
                continue;
            }
            // An owned wall the census reads as another def is a conflicting observation.
            DomainException.Require(s.Definition == "Wall", "owned wall read as another def");
            DomainException.Require(FoodForecastRules.FoodNumber(row.Flammability), "invalid wall flammability");
            if (row.Flammability > 0)
            {
                result.Add(s.Id);
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
