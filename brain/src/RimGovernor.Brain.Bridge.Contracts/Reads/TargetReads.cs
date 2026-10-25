using RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// The single-target and census read models of the building dispatch verticals. The reads (Client.ReadX) are wire plumbing:
// these are the shapes the planners and boundaries consume.

/// <summary>One freshly read bed, retaining its actual building row.</summary>
public sealed record BedUseTarget
{
    public ObservationContext? Context { get; init; }

    public required O.BuildingState Observed { get; init; }
    private O.BuildingControl.Bed Bed => Observed.Settings.Control as O.BuildingControl.Bed
        ?? throw new ObservationContractException("building is not a bed");
    public bool Medical => Bed.Medical;
    public bool Prisoners => Bed.ForPrisoners;
}

/// <summary>What every blueprint and frame still needs, aggregated by resource def name.</summary>
public sealed record ConstructionDeficitRead
{
    public ObservationContext? Context { get; init; }

    public IReadOnlyDictionary<string, long> StillNeed { get; init; } = new Dictionary<string, long>();

}

/// <summary>The crop one plant grower is set to; empty when it has none.</summary>
public sealed record GrowerCropTarget
{
    public ObservationContext? Context { get; init; }

    public required O.BuildingState Observed { get; init; }
    public string? Crop => Observed.Settings.Control is O.BuildingControl.Grower grower
        ? grower.CropDefName : throw new ObservationContractException("building is not a plant grower");
}

/// <summary>Whether one storage building is present with its settings.</summary>
public sealed record StorageBuildingTarget
{
    public ObservationContext? Context { get; init; }

    public bool Present { get; init; }
}

/// <summary>The map's bounds as the cell read answered them.</summary>
public sealed record MapBounds
{
    public ObservationContext? Context { get; init; }

    public required O.MapSize Observed { get; init; }
    public Policy.Bounds Bounds => new(checked((int)Observed.Width), checked((int)Observed.Height));
}

/// <summary>One entity list page, its context and the rows keyed by entity id.</summary>
/// <typeparam name="T">The row message.</typeparam>
public sealed record EntityRows<T>
{
    public ObservationContext? Context { get; init; }

    public Table<T> Rows { get; init; }

    /// <summary>The tick the page was read at.</summary>
    public long AsOf() => Context?.Tick ?? 0;
}
