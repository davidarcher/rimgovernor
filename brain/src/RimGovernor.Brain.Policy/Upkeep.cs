using NativeStructure = RimGovernor.Contracts.Wire.Observations.UpkeepStructure;

namespace RimGovernor.Brain.Policy;


/// <summary>Each fact is a complete native section. An unavailable section cannot prove that its old targets disappeared, while a known empty section can.</summary>
public sealed record UpkeepObservation
{
    public Fact<EquatableArray<ClearanceTarget>> Clearance { get; init; }

    /// <summary>The clearance rows standing on the ground of a planned room (RoomObstructionIDs): tier 1 of the recovery queue.</summary>
    public IReadOnlySet<string> RoomObstructions { get; init; } = new HashSet<string>();

    public Fact<EquatableArray<RepairStructure>> Structures { get; init; }

    /// <summary>The measured room census of the same read.</summary>
    public Fact<RoomObservation> Rooms { get; init; }

    /// <summary>The measured work-cell illumination census the rooms/lighting Need reviews; unknown when native could not read it.</summary>
    public Fact<LightingObservation> Lighting { get; init; }

    /// <summary>The measured room terrain census the rooms/flooring Need reviews; unknown when native could not read it.</summary>
    public Fact<FlooringObservation> Flooring { get; init; }

    /// <summary>The measured facility reachability and traffic census the layout/routes Need reviews.</summary>
    public Fact<RoutesObservation> Routes { get; init; }
}

/// <summary>A native structure census row with its def rows' repair priority (DefinitionCatalog.RepairPriority: 0 most urgent) and flammability as made (its def with its stuff).</summary>
public sealed record RepairStructure(NativeStructure Structure, int Priority, double Flammability);
