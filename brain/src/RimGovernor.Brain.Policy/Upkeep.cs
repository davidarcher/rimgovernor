using NativeStructure = RimGovernor.Contracts.Wire.Observations.UpkeepStructure;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;


/// <summary>Structures, Lighting, Flooring and Routes are complete native sections every routine frame reads; absence fails at the planner boundary.</summary>
public sealed record UpkeepObservation
{
    /// <summary>The clearance census of the review; null when the review does not read it (RoundsCapabilities.ClearanceCensus).</summary>
    public ClearanceCensus? Clearance { get; init; }

    /// <summary>The clearance rows standing on the ground of a planned room (RoomObstructionIDs): tier 1 of the recovery queue.</summary>
    public IReadOnlySet<string> RoomObstructions { get; init; } = new HashSet<string>();

    public EquatableArray<RepairStructure> Structures { get; init; }

    /// <summary>The measured room census of the same read.</summary>
    public RoomObservation Rooms { get => field ?? throw new RequiredFactException("rooms"); init; }

    /// <summary>The measured work-cell illumination census the rooms/lighting Need reviews.</summary>
    public LightingObservation Lighting { get => field ?? throw new RequiredFactException("lighting census"); init; }

    /// <summary>The measured room terrain census the rooms/flooring Need reviews.</summary>
    public FlooringObservation Flooring { get => field ?? throw new RequiredFactException("flooring census"); init; }

    /// <summary>The measured facility reachability and traffic census the layout/routes Need reviews.</summary>
    public RimGovernor.Contracts.Wire.Observations.RoutesFacts Routes { get => field ?? throw new RequiredFactException("routes census"); init; }
}

/// <summary>A native structure census row with its def rows' repair priority (DefinitionCatalog.RepairPriority: 0 most urgent) and flammability as made (its def with its stuff).</summary>
public sealed record RepairStructure(NativeStructure Observed, O.BuildingState Building, int Priority, double Flammability)
{
    public string Id => Building.Building.Id;
    public string Definition => Building.Building.DefName;
    public Cell Cell => Building.Building.Place is O.EntityPlace.OnMap at ? at.Cell : throw new DomainException("repair building is unplaced");
    public bool Home => Observed.Home;
    public O.BuildingHitPoints.Tracked HitPoints => Building.HitPoints as O.BuildingHitPoints.Tracked
        ?? throw new DomainException("repair census building has no hit points");
}
