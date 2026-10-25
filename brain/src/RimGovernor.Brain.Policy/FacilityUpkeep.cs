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

    /// <summary>
    /// The flammable owned walls a stone upgrade is owed, in id order. Throws <see cref="RequiredFactException"/> when the
    /// owned construction (<c>current_construction</c>) or the structure census (<c>stone_structures</c>) is unread, and
    /// <see cref="DomainException"/> on an invalid read.
    /// </summary>
    public static EquatableArray<string> ReviewStoneShell(EquatableArray<ConstructionClaim> buildings, EquatableArray<RepairStructure> structures)
    {
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
            return [];
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var row in structures)
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
            throw new RequiredFactException("stone_structures");
        }
        result.Sort(StringComparer.Ordinal);
        return EquatableArray.From(result);
    }
}
