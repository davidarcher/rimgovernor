namespace RimGovernor.Brain.Policy;

public sealed record HomeCoverageTarget
{
    /// <summary>Complete, unbatched geometry. Legacy Home batches leave it unknown and cannot establish colony territory.</summary>
    public Fact<HomeExtentGeometry> ExtentGeometry { get; init; }

    public string Id { get; init; } = "";

    public Fact<string> Shape { get; init; }

    public Fact<long> Missing { get; init; }

    public Fact<long> Excluded { get; init; }

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public string Blocker { get; init; } = "";
}

public sealed record HomeCoverageObservation
{
    public long Revision { get; init; }

    public IReadOnlyList<HomeCoverageTarget> Targets { get; init; } = [];

    /// <summary>Every current home-area cell.</summary>
    public Fact<IReadOnlyList<Cell>> Home { get; init; }

    /// <summary>The game's auto-expand setting.</summary>
    public Fact<bool> AutoHome { get; init; }
}

public sealed record StoneStructure
{
    public string Id { get; init; } = "";

    public Resource Definition { get; init; }

    public Fact<double> Flammability { get; init; }
}

public static partial class PolicyFunctions
{
    public static readonly ConcernId MaintainHomeCoverage = new("MaintainHomeCoverage");

    public static readonly ConcernId MaintainStoneShell = new("MaintainStoneShell");

    /// <summary>The native project whose recipes cut the stone blocks a wall replacement needs.</summary>
    public const string StoneShellResearch = "Stonecutting";

    private static bool FacilityCells(IReadOnlyList<Cell> cells)
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
    /// Go <c>ReviewHomeCoverage</c>: lists every census target (all colonist buildings and every stockpile: the
    /// governor owns them whoever built them) still missing Home, at its current geometry. Throws
    /// <see cref="DomainException"/> where Go returned an error.
    /// </summary>
    public static Fact<IReadOnlyList<HomeCoverageTarget>> ReviewHomeCoverage(Fact<HomeCoverageObservation> observed)
    {
        var unknown = Unknown<IReadOnlyList<HomeCoverageTarget>>();
        if (!observed.TryGetValue(out var census))
        {
            return unknown;
        }
        DomainException.Require(census.Revision >= 0, "invalid Home census");
        var result = new List<HomeCoverageTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in census.Targets)
        {
            DomainException.Require(FoodId(row.Id) && seen.Add(row.Id), "invalid Home target identity");
            var mk = row.Missing.TryGetValue(out var missing);
            var ek = row.Excluded.TryGetValue(out var excluded);
            var sk = row.Shape.TryGetValue(out var shape);
            if (!mk || !ek || !sk || !FacilityCells(row.Cells))
            {
                return unknown;
            }
            DomainException.Require(
                FoodId(shape!) && excluded >= 0 && excluded <= missing && missing >= 0 && missing <= row.Cells.Count,
                "invalid Home target geometry or counts"
            );
            if (missing > 0 || row.Blocker.Length != 0)
            {
                result.Add(row with { Cells = [.. row.Cells] });
            }
        }
        return Known<IReadOnlyList<HomeCoverageTarget>>([.. result.OrderBy(t => t.Id, StringComparer.Ordinal)]);
    }

    /// <summary>Go <c>ReviewStoneShell</c>. Throws <see cref="DomainException"/> where Go returned an error.</summary>
    public static Fact<IReadOnlyList<string>> ReviewStoneShell(Fact<IReadOnlyList<ConstructionClaim>> owned, Fact<IReadOnlyList<StoneStructure>> structures)
    {
        var unknown = Unknown<IReadOnlyList<string>>();
        if (!owned.TryGetValue(out var buildings))
        {
            return unknown;
        }
        var walls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in buildings)
        {
            if (b.Building.Definition == "Wall")
            {
                DomainException.Require(FoodId(b.Identity.Current) && walls.Add(b.Identity.Current), "invalid owned wall");
            }
        }
        if (walls.Count == 0)
        {
            return Known<IReadOnlyList<string>>([]);
        }
        if (!structures.TryGetValue(out var rows))
        {
            return unknown;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var row in rows)
        {
            DomainException.Require(FoodId(row.Id) && seen.Add(row.Id) && ValidResource(row.Definition), "invalid structure census");
            if (!walls.Contains(row.Id))
            {
                continue;
            }
            if (!row.Flammability.TryGetValue(out var flam) || row.Definition.Value != "Wall")
            {
                return unknown;
            }
            DomainException.Require(FoodNumber(flam), "invalid wall flammability");
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
        return Known<IReadOnlyList<string>>(result);
    }
}
