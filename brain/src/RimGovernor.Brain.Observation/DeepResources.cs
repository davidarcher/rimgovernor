namespace RimGovernor.Brain.Observation;

public sealed record DeepResourceLump
{
    public string Definition { get; init; } = "";

    public long Count { get; init; }

    public Cell Centre { get; init; }

    public uint CellCount { get; init; }
}

public sealed record MineralScanner
{
    public string ID { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Position { get; init; }

    public Fact<bool> Built { get; init; }

    public Fact<bool> Powered { get; init; }

    public Fact<bool> Working { get; init; }

    public Fact<long> TicksToNextFind { get; init; }

    public Fact<string> TargetResource { get; init; }
}

/// <summary>One spawned colonist drill. Depleted is the native verdict that no valuable deposit remains under it.</summary>
public sealed record DeepDrill
{
    public string ID { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Position { get; init; }

    public Fact<bool> Powered { get; init; }

    public Fact<bool> Depleted { get; init; }

    public Fact<bool> Designated { get; init; }

    public Fact<string> Resource { get; init; }

    public Fact<long> Remaining { get; init; }
}

public sealed record DeepResources
{
    public IReadOnlyList<DeepResourceLump> Lumps { get; init; } = [];

    public IReadOnlyList<MineralScanner> GroundScanners { get; init; } = [];

    public IReadOnlyList<MineralScanner> LongRangeScanners { get; init; } = [];

    public IReadOnlyList<DeepDrill> Drills { get; init; } = [];
}

public static partial class ObservationPackage
{
    /// <summary>Validates the census before projection. Missing sections remain unknown; an observed empty census establishes that no lumps/scanners exist.</summary>
    internal static Fact<DeepResources> ColonyDeepResources(O.DeepResourcesSection? section, DefinitionCatalog? catalog)
    {
        var f = section?.Observed;
        if (f is null)
        {
            return default;
        }
        var lumps = new List<DeepResourceLump>();
        foreach (var row in f.Lumps)
        {
            lumps.Add(new DeepResourceLump { Definition = row.DefName, Count = row.Count, Centre = new Cell(row.Centre?.X ?? 0, row.Centre?.Z ?? 0), CellCount = row.CellCount });
        }
        List<MineralScanner> Scanners(IEnumerable<O.MineralScannerState> rows)
        {
            var result = new List<MineralScanner>();
            foreach (var row in rows)
            {
                result.Add(new MineralScanner
                {
                    ID = row.BuildingId,
                    Definition = row.DefName,
                    Position = new Cell(row.Position?.X ?? 0, row.Position?.Z ?? 0),
                    Built = Optional(row.HasBuilt, row.Built),
                    Powered = Optional(row.HasPowered, row.Powered),
                    Working = Optional(row.HasWorking, row.Working),
                    TicksToNextFind = Optional(row.HasTicksToNextFind, row.TicksToNextFind),
                    TargetResource = TargetResource(row, catalog),
                });
            }
            return result;
        }
        var drills = new List<DeepDrill>();
        foreach (var row in f.Drills)
        {
            drills.Add(new DeepDrill
            {
                ID = row.BuildingId,
                Definition = row.DefName,
                Position = new Cell(row.Position?.X ?? 0, row.Position?.Z ?? 0),
                Powered = Optional(row.HasPowered, row.Powered),
                Depleted = Optional(row.HasDepleted, row.Depleted),
                Designated = Optional(row.HasDesignated, row.Designated),
                Resource = Optional(row.HasResource, row.Resource),
                Remaining = Optional(row.HasRemaining, row.Remaining),
            });
        }
        return Known(new DeepResources { Lumps = lumps, GroundScanners = Scanners(f.GroundScanners), LongRangeScanners = Scanners(f.LongRangeScanners), Drills = drills });
    }

    /// <summary>What a long-range scanner's aimed mineable yields, read from the mineable's def row; unknown when the scanner reports no target or the catalog has no yield for it.</summary>
    private static Fact<string> TargetResource(O.MineralScannerState row, DefinitionCatalog? catalog)
    {
        if (!row.HasTargetMineable)
        {
            return default;
        }
        var yield = RequireCatalog(catalog).MineableYield(row.TargetMineable);
        return yield != "" ? Known(yield) : default;
    }
}
