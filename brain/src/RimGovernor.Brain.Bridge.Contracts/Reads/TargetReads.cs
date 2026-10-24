using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

// The single-target and census read models of the building dispatch verticals. The reads (Client.ReadX) are wire plumbing:
// these are the shapes the planners and boundaries consume.

/// <summary>One bed's medical and prisoner use and its owners.</summary>
public sealed record BedUseTarget
{
    public ObservationContext? Context { get; init; }

    public string Thing { get; init; } = "";

    public bool Medical { get; init; }

    public bool Prisoners { get; init; }

    public IReadOnlyList<string> Owners { get; init; } = [];
}

/// <summary>What every blueprint and frame still needs, by resource def name, and how many structures were counted.</summary>
public sealed record ConstructionDeficitRead
{
    public ObservationContext? Context { get; init; }

    public IReadOnlyDictionary<string, long> StillNeed { get; init; } = new Dictionary<string, long>();

    public int Structures { get; init; }
}

/// <summary>The crop one plant grower is set to; empty when it has none.</summary>
public sealed record GrowerCropTarget
{
    public ObservationContext? Context { get; init; }

    public string Thing { get; init; } = "";

    public string Crop { get; init; } = "";
}

/// <summary>Whether one storage building is present with its settings.</summary>
public sealed record StorageBuildingTarget
{
    public ObservationContext? Context { get; init; }

    public string Thing { get; init; } = "";

    public bool Present { get; init; }
}

/// <summary>The map's bounds as the cell read answered them.</summary>
public sealed record MapBounds
{
    public ObservationContext? Context { get; init; }

    public Policy.Bounds Bounds { get; init; }
}

/// <summary>One packed item (a minified thing) with the thing inside it.</summary>
public sealed record PackedItem
{
    public string Id { get; init; } = "";

    public string Inner { get; init; } = "";

    public string InnerDef { get; init; } = "";

    public int Quality { get; init; }

    public bool QualityKnown { get; init; }

    public double MarketValue { get; init; }
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
