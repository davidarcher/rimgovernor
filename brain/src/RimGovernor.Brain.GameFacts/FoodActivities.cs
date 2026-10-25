using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Common;
using Cell = RimGovernor.Brain.Domain.Cell;
using System.Text.Json.Serialization;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>One pawn's harvest-and-haul row for a ripe food plant to one storage cell. Native emits the row only when the
/// pawn reaches the plant and the cell, so both walks are priced.</summary>
public sealed record FoodActivityOption(string Pawn, string Target, string Resource, Cell Origin, Cell Destination,
    string WorkGiver, int Count, double WorkTicks)
{
    /// <summary>The native stationary service cell; target and route anchors are not service positions.</summary>
    public ServicePositionRead ServicePosition { get; init; } = new ServicePositionRead.Unprojected("synthetic_or_unsupported_service");
    /// <summary>The Plants learning the harvest gives, when the pawn's Plants skill is not disabled.</summary>
    public required ProductiveSkillWork? SkillWork { get; init; }
    /// <summary>The plant's whole yield now (Plant.YieldNow), whatever one carrier's load.</summary>
    public required int SourceCount { get; init; }
    public required double ApproachTicks { get; init; }
    public required double DeliveryTicks { get; init; }
    /// <summary>Whether the pawn may harvest it (WorkGiver_GrowerHarvest's legality), not just haul it.</summary>
    public required bool HarvestAllowed { get; init; }
}
public sealed record FoodActivityRead
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<FoodActivityOption>))]
    public required EquatableArray<FoodActivityOption> Options { get; init; }
}
