using NativeStructure = RimGovernor.Contracts.Wire.Observations.UpkeepStructure;

namespace RimGovernor.Brain.Policy;


/// <summary>Structures, Lighting, Flooring and Routes are complete native sections every routine frame reads; Lighting, Flooring and Routes are null only on a projection no routine frame built.</summary>
public sealed record UpkeepObservation
{
    /// <summary>The clearance census of the review; null when the review does not read it (RoundsCapabilities.ClearanceCensus).</summary>
    public ClearanceCensus? Clearance { get; init; }

    /// <summary>The clearance rows standing on the ground of a planned room (RoomObstructionIDs): tier 1 of the recovery queue.</summary>
    public IReadOnlySet<string> RoomObstructions { get; init; } = new HashSet<string>();

    public EquatableArray<RepairStructure> Structures { get; init; }

    /// <summary>The measured room census of the same read.</summary>
    public Fact<RoomObservation> Rooms { get; init; }

    /// <summary>The measured work-cell illumination census the rooms/lighting Need reviews.</summary>
    public LightingObservation? Lighting { get; init; }

    /// <summary>The measured room terrain census the rooms/flooring Need reviews.</summary>
    public FlooringObservation? Flooring { get; init; }

    /// <summary>The measured facility reachability and traffic census the layout/routes Need reviews.</summary>
    public RoutesObservation? Routes { get; init; }
}

/// <summary>A native structure census row with its def rows' repair priority (DefinitionCatalog.RepairPriority: 0 most urgent) and flammability as made (its def with its stuff).</summary>
public sealed record RepairStructure(NativeStructure Structure, int Priority, double Flammability);
