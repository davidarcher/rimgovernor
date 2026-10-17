namespace RimGovernor.Brain.Policy;

// Go: upkeep.go.

/// <summary>Each fact is a complete native section. An unavailable section cannot prove that its old targets disappeared, while a known empty section can.</summary>
public sealed record UpkeepObservation
{
    public Fact<EquatableArray<ClearanceTarget>> Clearance { get; init; }

    /// <summary>The planned-ground clearance work of the same read (PlannedGroundWork): unplanned buildings and floors on the ground of planned rooms not yet standing.</summary>
    public Fact<EquatableArray<string>> Ground { get; init; }

    /// <summary>The clearance rows standing on the ground of a planned room (RoomObstructionIDs): tier 1 of the recovery queue.</summary>
    public IReadOnlySet<string> RoomObstructions { get; init; } = new HashSet<string>();

    public Fact<EquatableArray<UpkeepStructure>> Structures { get; init; }

    public Fact<EquatableArray<UpkeepFilth>> Filth { get; init; }

    /// <summary>The measured room census of the same read.</summary>
    public Fact<RoomObservation> Rooms { get; init; }

    /// <summary>The measured work-cell illumination census the rooms/lighting Need reviews; unknown when native could not read it.</summary>
    public Fact<LightingObservation> Lighting { get; init; }

    /// <summary>The measured room terrain census the rooms/flooring Need reviews; unknown when native could not read it.</summary>
    public Fact<FlooringObservation> Flooring { get; init; }

    /// <summary>The measured facility reachability and traffic census the layout/routes Need reviews.</summary>
    public Fact<RoutesObservation> Routes { get; init; }
}

public sealed record UpkeepStructure
{
    public string Id { get; init; } = "";

    public Cell Cell { get; init; }

    public bool Home { get; init; }

    public long HitPoints { get; init; }

    public long MaxHitPoints { get; init; }

    public int Priority { get; init; }
}

public sealed record UpkeepFilth
{
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public bool Home { get; init; }

    /// <summary>The RoomRoleDef name.</summary>
    public string Room { get; init; } = "";

    /// <summary>The room census identity the filth lies in (unknown outdoors or before the native field existed).</summary>
    public Fact<string> RoomId { get; init; }

    public uint Thickness { get; init; }
}
