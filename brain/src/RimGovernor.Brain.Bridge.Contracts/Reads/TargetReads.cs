using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

// The single-target and census read models of the building dispatch verticals (Go bed_use_target.go,
// claim_building_target.go, construction_deficit.go, grower_crop_target.go, storage_building.go,
// zone_delete_target.go, map_bounds.go, packed_install.go). The reads (Client.ReadX) are wire plumbing and not
// ported: these are the shapes the planners and boundaries consume.

/// <summary>Go's <c>BedUseTarget</c>: one bed's medical and prisoner use and its owners.</summary>
public sealed record BedUseTarget
{
    public ObservationContext? Context { get; init; }

    public string Thing { get; init; } = "";

    public bool Medical { get; init; }

    public bool Prisoners { get; init; }

    public IReadOnlyList<string> Owners { get; init; } = [];
}

/// <summary>Go's <c>ClaimBuildingTarget</c>: one claimable building, its CAS token and whether the colony already owns it.</summary>
public sealed record ClaimBuildingTarget
{
    public ObservationContext? Context { get; init; }

    public string Thing { get; init; } = "";

    public string Token { get; init; } = "";

    public bool PlayerOwned { get; init; }
}

/// <summary>Go's <c>ConstructionDeficitRead</c>: what every blueprint and frame still needs, by resource def name, and how many structures were counted.</summary>
public sealed record ConstructionDeficitRead
{
    public ObservationContext? Context { get; init; }

    public IReadOnlyDictionary<string, long> StillNeed { get; init; } = new Dictionary<string, long>();

    public int Structures { get; init; }
}

/// <summary>Go's <c>GrowerCropTarget</c>: the crop one plant grower is set to; empty when it has none.</summary>
public sealed record GrowerCropTarget
{
    public ObservationContext? Context { get; init; }

    public string Thing { get; init; } = "";

    public string Crop { get; init; } = "";
}

/// <summary>Go's <c>StorageBuildingTarget</c>: whether one storage building is present with its settings.</summary>
public sealed record StorageBuildingTarget
{
    public ObservationContext? Context { get; init; }

    public string Thing { get; init; } = "";

    public bool Present { get; init; }
}

/// <summary>Go's <c>ZoneDeleteTarget</c>: whether one zone is present, its type and its planted cell count.</summary>
public sealed record ZoneDeleteTarget
{
    public ObservationContext? Context { get; init; }

    public string Zone { get; init; } = "";

    public bool Present { get; init; }

    public string Type { get; init; } = "";

    public uint Planted { get; init; }
}

/// <summary>Go's <c>MapBounds</c>: the map's bounds as the cell read answered them.</summary>
public sealed record MapBounds
{
    public ObservationContext? Context { get; init; }

    public Policy.Bounds Bounds { get; init; }
}

/// <summary>Go's <c>PackedItem</c>: one packed item (a minified thing) with the thing inside it.</summary>
public sealed record PackedItem
{
    public string Id { get; init; } = "";

    public string Inner { get; init; } = "";

    public string InnerDef { get; init; } = "";

    public int Quality { get; init; }

    public bool QualityKnown { get; init; }

    public double MarketValue { get; init; }
}

/// <summary>Go's <c>EntityRows[T]</c>: one entity list page, its context and the rows keyed by entity id.</summary>
/// <typeparam name="T">The row message.</typeparam>
public sealed record EntityRows<T>
{
    public ObservationContext? Context { get; init; }

    public Table<T> Rows { get; init; }

    /// <summary>The tick the page was read at (Go <c>AsOf</c>).</summary>
    public long AsOf() => Context?.Tick ?? 0;
}
